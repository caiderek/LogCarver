using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Serializes reconstructed row events to CSV, a .sql script, or JSON for
/// handing off to audit/ticketing systems - the console output is for a
/// human reading it live, not for piping elsewhere.
///
/// The Write* methods stream directly to a TextWriter and are the
/// memory-safe path for a real table's full history: writing one event at
/// a time never holds more than one event's rendered text in memory at
/// once. The To* methods are thin StringWriter-backed wrappers kept for
/// tests and other small-scale/in-memory callers (e.g. an HTTP response
/// body) - do NOT use them against a real multi-million-row table export.
/// Building the entire output as one string/StringBuilder before writing it
/// out can fail with "Insufficient memory to continue the execution of the
/// program" on a table with millions of events: .NET caps a single
/// object/array at ~2GB, which a giant string or StringBuilder's internal
/// buffer reaches long before system memory runs out.
/// </summary>
public static class RowEventExporter
{
    /// <summary>
    /// Writes to a temp file beside <paramref name="path"/> (same
    /// directory, so the final move is a same-volume rename and thus
    /// atomic - never a slower, non-atomic cross-volume copy) via
    /// <paramref name="write"/>, then moves it to <paramref name="path"/>
    /// only on success. If <paramref name="write"/> throws partway through
    /// (a decode error deep in a multi-million-event stream, a disk-full
    /// IOException mid-write, etc.), the temp file is deleted rather than
    /// left behind or moved into place - the destination path either
    /// doesn't exist or holds a complete, valid export, never a silently
    /// truncated one. The old buffer-everything-then-write-once approach
    /// got this property for free (nothing was written until the whole
    /// string existed); streaming for memory safety loses that unless this
    /// helper does it explicitly instead.
    /// </summary>
    public static T WriteFileAtomically<T>(string path, Func<TextWriter, T> write, System.Text.Encoding? encoding = null)
    {
        string tempPath = path + ".tmp";
        try
        {
            T result;
            using (var writer = encoding is null ? new StreamWriter(tempPath) : new StreamWriter(tempPath, false, encoding))
            {
                result = write(writer);
            }
            File.Move(tempPath, path, overwrite: true);
            return result;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup; surface the original exception, not a cleanup failure */ }
            throw;
        }
    }

    /// <summary>
    /// One column per observed table column, prefixed Before_/After_ (not
    /// a single flattened "Col=Value, Col2=Value2" cell) so the result is
    /// directly usable in a spreadsheet - pivoting or filtering by one
    /// column's value doesn't work if every column is jammed into one cell.
    /// </summary>
    public static void WriteCsv(
        TextWriter writer, IEnumerable<RowEvent> events, IReadOnlyList<ColumnSchema> schema, string tableName,
        bool includeUndoSql, bool includeReplaySql, IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        var columns = schema.Select(c => c.Name).ToList();

        var header = new List<string> { "Lsn", "Timestamp", "Kind" };
        header.AddRange(columns.Select(c => $"Before_{c}"));
        header.AddRange(columns.Select(c => $"After_{c}"));
        header.Add("Note");
        if (includeUndoSql) header.Add("UndoSql");
        if (includeReplaySql) header.Add("ReplaySql");
        // Appended last, after every pre-existing column, not inserted
        // after Kind - this format is consumed by scripts, and a script reading
        // columns by position (rather than by
        // header name) would silently misread every column if a new one
        // were spliced into the middle. Appending only adds a column past
        // whatever a positional reader already expects; it never shifts
        // one that already existed. A reviewer scanning by eye/AutoFilter
        // in Excel finds it fine regardless of position, since it's a
        // named column with a highly distinctive header either way.
        header.Add("NeedsManualReview");

        writer.Write(string.Join(",", header.Select(CsvEscape)));
        writer.Write("\r\n");

        foreach (var e in events)
        {
            var row = new List<string>
            {
                e.Lsn,
                e.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "",
                e.Kind.ToString().ToUpperInvariant(),
            };
            row.AddRange(columns.Select(c => CellValue(e.Before, c)));
            row.AddRange(columns.Select(c => CellValue(e.After, c)));
            row.Add(e.Note ?? "");
            if (includeUndoSql) row.Add(UndoSqlGenerator.Generate(e, tableName, identityColumnName, keyColumns) ?? "");
            if (includeReplaySql) row.Add(ReplaySqlGenerator.Generate(e, tableName, identityColumnName) ?? "");
            row.Add(e.NeedsManualReview ? "YES" : "");
            writer.Write(string.Join(",", row.Select(CsvEscape)));
            writer.Write("\r\n");
        }
    }

    public static string ToCsv(
        IEnumerable<RowEvent> events, IReadOnlyList<ColumnSchema> schema, string tableName,
        bool includeUndoSql, bool includeReplaySql, IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        var sw = new StringWriter();
        WriteCsv(sw, events, schema, tableName, includeUndoSql, includeReplaySql, keyColumns, identityColumnName);
        return sw.ToString();
    }

    /// <summary>
    /// A plain, runnable-looking .sql script: one comment line identifying
    /// the source event (LSN/timestamp/kind) followed by its Undo and/or
    /// Replay statement. Caller must request at least one of the two -
    /// a script with only comment lines isn't a useful export.
    /// Returns the number of events that actually produced a statement, so
    /// a caller can tell "wrote an empty script because every event was
    /// unsupported" apart from "wrote a real script" without re-reading
    /// whatever it just streamed the output to.
    /// </summary>
    public static int WriteSql(TextWriter writer, IEnumerable<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql, IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        int produced = 0;
        foreach (var e in events)
        {
            string? undoSql = includeUndoSql ? UndoSqlGenerator.Generate(e, tableName, identityColumnName, keyColumns) : null;
            string? replaySql = includeReplaySql ? ReplaySqlGenerator.Generate(e, tableName, identityColumnName) : null;
            if (undoSql is null && replaySql is null)
                continue;
            produced++;

            string when = e.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "time unknown";
            writer.Write($"-- [{e.Lsn} {when}] {e.Kind.ToString().ToUpperInvariant()}");
            writer.Write('\n');
            // A Note on an event that still produced Undo/Replay SQL isn't
            // a decode failure (those events never reach here at all - see
            // the undoSql/replaySql null check above) - it's a
            // successfully-decoded value the reconstructor still isn't
            // fully confident in (currently: a record whose bytes crossed a
            // detected-but-unrepairable physical artifact - see LogRecord's
            // PossiblyCorruptedOffsetsInRowLogContents0/1 doc comment).
            // The "NEEDS MANUAL REVIEW" prefix (not just "NOTE") is
            // deliberate: a person skimming a multi-thousand-line script
            // for comment lines should be able to tell at a glance which
            // ones mean "just FYI" and which mean "verify this statement
            // before running it" - e.Note is only ever non-null for the
            // latter (see RowEvent.NeedsManualReview).
            if (e.Note is not null) { writer.Write($"-- NEEDS MANUAL REVIEW: {e.Note}"); writer.Write('\n'); }
            if (undoSql is not null) { writer.Write("-- UNDO\n"); writer.Write(undoSql); writer.Write('\n'); }
            if (replaySql is not null) { writer.Write("-- REPLAY\n"); writer.Write(replaySql); writer.Write('\n'); }
            writer.Write('\n');
        }
        return produced;
    }

    public static string ToSql(IEnumerable<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql, IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        var sw = new StringWriter();
        WriteSql(sw, events, tableName, includeUndoSql, includeReplaySql, keyColumns, identityColumnName);
        return sw.ToString();
    }

    /// <summary>
    /// The script the CLI's <c>--export sql</c> writes: a short safety header,
    /// then either every Undo statement (newest event first) or every Replay
    /// statement (oldest first). An undo has to peel the changes back in
    /// reverse - run oldest-first, the second undo of a row no longer matches
    /// its WHERE clause and silently affects nothing. Exactly one of the two
    /// is written because their orders contradict each other in one file.
    /// Returns the number of statements produced.
    /// </summary>
    public static int WriteSqlScript(
        TextWriter writer, IReadOnlyList<RowEvent> events, string tableName, bool undo,
        IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        writer.Write("-- Generated by LogCarver (https://github.com/caiderek/LogCarver)\n");
        writer.Write($"-- Table: {tableName}\n");
        writer.Write(undo
            ? "-- Mode: UNDO - newest event first. Run the statements from top to bottom.\n"
            : "-- Mode: REPLAY - oldest event first. Run the statements from top to bottom.\n");
        writer.Write("-- These are SUGGESTED statements. LogCarver never runs them itself.\n");
        writer.Write("-- Review every statement and try it on a non-production copy first.\n");
        writer.Write("-- A statement whose WHERE no longer matches (the row changed since) affects 0 rows on purpose.\n");
        writer.Write("-- Statements marked NEEDS MANUAL REVIEW must be checked before you run them.\n");
        writer.Write("-- Putting a deleted row back into a table with an IDENTITY column needs ALTER permission on the table.\n");
        writer.Write('\n');

        IEnumerable<RowEvent> ordered = undo ? Enumerable.Reverse(events) : events;
        return WriteSql(writer, ordered, tableName, includeUndoSql: undo, includeReplaySql: !undo, keyColumns, identityColumnName);
    }

    /// <summary>
    /// Built with System.Text.Json.Nodes (JsonObject/JsonValue) rather than
    /// JsonSerializer.Serialize&lt;T&gt; against a POCO - an AOT-published
    /// program (PublishAot) cannot use the reflection-based serializer, and
    /// the row values here are a bag of
    /// `object?` with no fixed shape a source-generated JsonSerializerContext
    /// could describe. JsonNode's own writer is hand-coded, not reflection,
    /// so it works under AOT.
    ///
    /// Written as one compact JSON object per line inside the array
    /// brackets, not a single JsonArray.ToJsonString() call over every
    /// event - the whole point of streaming is to never hold more than one
    /// event's rendered JSON in memory at a time.
    /// </summary>
    public static void WriteJson(TextWriter writer, IEnumerable<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql, IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        writer.Write('[');
        bool first = true;
        foreach (var e in events)
        {
            var obj = new JsonObject
            {
                ["Lsn"] = e.Lsn,
                ["Timestamp"] = e.Timestamp is { } t ? JsonValue.Create(t) : null,
                ["Kind"] = e.Kind.ToString().ToUpperInvariant(),
                ["NeedsManualReview"] = JsonValue.Create(e.NeedsManualReview),
                ["Before"] = ToJsonObject(e.Before),
                ["After"] = ToJsonObject(e.After),
                ["Note"] = e.Note,
                ["UndoSql"] = includeUndoSql ? UndoSqlGenerator.Generate(e, tableName, identityColumnName, keyColumns) : null,
                ["ReplaySql"] = includeReplaySql ? ReplaySqlGenerator.Generate(e, tableName, identityColumnName) : null,
            };
            writer.Write(first ? "\n" : ",\n");
            first = false;
            writer.Write(obj.ToJsonString());
        }
        writer.Write(first ? "]" : "\n]");
    }

    public static string ToJson(IEnumerable<RowEvent> events, string tableName, bool includeUndoSql, bool includeReplaySql, IReadOnlyList<string>? keyColumns = null, string? identityColumnName = null)
    {
        var sw = new StringWriter();
        WriteJson(sw, events, tableName, includeUndoSql, includeReplaySql, keyColumns, identityColumnName);
        return sw.ToString();
    }

    private static JsonObject? ToJsonObject(IReadOnlyDictionary<string, object?>? row)
    {
        if (row is null) return null;
        var obj = new JsonObject();
        foreach (var (key, value) in row)
        {
            obj[key] = value switch
            {
                null => null,
                int i => JsonValue.Create(i),
                // Added alongside BIGINT column decoding in RowDecoder -
                // without this, a bigint value fell into the generic
                // ToString() fallback below and came out as a quoted JSON
                // string ("12345") instead of a real number, unlike every
                // other integer type.
                long l => JsonValue.Create(l),
                DateTime dt => JsonValue.Create(dt),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return obj;
    }

    private static string CellValue(IReadOnlyDictionary<string, object?>? row, string column)
    {
        if (row is null || !row.TryGetValue(column, out var value) || value is null)
            return "";
        return value is DateTime dt
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : value.ToString() ?? "";
    }

    // CSV/formula injection (CWE-1236): a cell opened in Excel/Sheets whose
    // text starts with =, +, -, or @ can be interpreted as a formula rather
    // than plain text. The values here come from recovered database rows,
    // which this tool exists to examine precisely when the database may
    // have been tampered with - an attacker-planted value is exactly the
    // kind of content that could land in a cell here. Prefixing with a
    // leading apostrophe forces spreadsheet apps to treat it as text; it's
    // invisible in the rendered cell and doesn't change the value CSV
    // parsers (or a human diffing the file) see.
    private static readonly char[] FormulaTriggerChars = ['=', '+', '-', '@', '\t', '\r'];

    // RFC 4180: quote a field only if it contains a comma, quote, or line
    // break, and double up any internal quotes - quoting everything
    // unconditionally would still be correct but is noisier to read.
    private static string CsvEscape(string value)
    {
        if (value.Length > 0 && FormulaTriggerChars.Contains(value[0]))
            value = "'" + value;

        return value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
