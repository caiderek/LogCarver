using LogCarver.Core;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Shared plumbing for UndoSqlGenerator and ReplaySqlGenerator - the two
/// are mirror images of each other (undo: after -&gt; before, replay:
/// before -&gt; after), so the statement-building and literal-formatting
/// logic lives here once instead of twice.
///
/// IDENTITY columns (found for real 2026-10-08, via an independent
/// cross-check agent testing LogCarverGuard against a table using SQL
/// Server's ordinary default IDENTITY-PK pattern - every one of this
/// project's own test fixtures up to that point happened to use an
/// explicitly-assigned PK instead, so this was invisible until an outside
/// tester hit it on the very first try): SQL Server rejects outright any
/// UPDATE that tries to SET an identity column, even to its own current
/// value (error 8102) - and separately rejects an INSERT that names an
/// identity column in its column list unless IDENTITY_INSERT is ON for
/// that table. <paramref name="identityColumnName"/> on BuildUpdate/
/// BuildInsert below handles each: an UPDATE never needs to touch it at
/// all (a row's own identity value can never actually change, so omitting
/// it from SET loses nothing), while re-INSERTing a deleted row (undoing
/// a DELETE) genuinely needs its original identity value restored, which
/// requires the IDENTITY_INSERT wrap.
/// </summary>
internal static class SqlStatementBuilder
{
    /// <param name="identityColumnName">
    /// The table's identity column, if it has one and it's present in
    /// <paramref name="values"/>, else null. When given, the INSERT is
    /// wrapped in SET IDENTITY_INSERT ON/OFF so the original value can be
    /// restored exactly, instead of SQL Server silently generating a new
    /// one (or rejecting the statement outright) - see this class's own
    /// doc comment.
    /// </param>
    public static string BuildInsert(IReadOnlyDictionary<string, object?> values, string tableName, string? identityColumnName = null)
    {
        string columns = string.Join(", ", values.Keys.Select(EscapeIdentifier));
        string literals = string.Join(", ", values.Values.Select(FormatSqlLiteral));
        string escapedTable = EscapeIdentifier(tableName);
        string insert = $"INSERT INTO {escapedTable} ({columns}) VALUES ({literals});";
        return identityColumnName is not null && values.ContainsKey(identityColumnName)
            ? $"SET IDENTITY_INSERT {escapedTable} ON; {insert} SET IDENTITY_INSERT {escapedTable} OFF;"
            : insert;
    }

    public static string BuildDelete(IReadOnlyDictionary<string, object?> matchValues, string tableName) =>
        $"DELETE FROM {EscapeIdentifier(tableName)} WHERE {BuildWhereClause(matchValues)};";

    /// <param name="setValues">The state to write.</param>
    /// <param name="matchValues">
    /// The state to match in WHERE - every column, not just a primary key
    /// (a real table may not have a clean one). This also serves as an
    /// optimistic-concurrency guard: if the row has since changed away
    /// from this state, the WHERE won't match and the statement becomes a
    /// safe no-op instead of silently overwriting it.
    /// </param>
    /// <param name="identityColumnName">
    /// The table's identity column, if it has one, else null. Excluded
    /// from the SET clause entirely (never from WHERE, where matching on
    /// it is still valid) - see this class's own doc comment for why an
    /// UPDATE never needs to, and in fact cannot, touch this column.
    /// </param>
    public static string BuildUpdate(
        IReadOnlyDictionary<string, object?> setValues, IReadOnlyDictionary<string, object?> matchValues, string tableName,
        string? identityColumnName = null)
    {
        string setClause = string.Join(", ", setValues
            .Where(kv => kv.Key != identityColumnName)
            .Select(kv => $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}"));
        return $"UPDATE {EscapeIdentifier(tableName)} SET {setClause} WHERE {BuildWhereClause(matchValues)};";
    }

    // Real bug, found 2026-10-08 via an independent cross-check agent's
    // SECOND testing round, hours after the FIRST fix for this same
    // DateTime-comparison area shipped: wrapping only the LITERAL side in
    // CONVERT(DATETIME2(7), ...) (see FormatSqlLiteral's own doc comment)
    // fixed the outright-throws-an-error case, but comparing that
    // DATETIME2(7)-typed literal directly against a plain DATETIME column
    // is itself unreliable - confirmed via a direct, repeated sqlcmd probe:
    // `@datetimeCol = CONVERT(DATETIME2(7), @matchingLiteral)` intermittently
    // returns NO MATCH even when an explicit CAST of the column to
    // DATETIME2(7) first provably equals the same literal. SQL Server's
    // documented data-type-precedence table says DATETIME2 outranks
    // DATETIME (implying the DATETIME side should always widen up for an
    // implicit comparison), but that is not what was observed empirically -
    // whatever the real mechanism, relying on implicit cross-type
    // comparison here is not safe. Explicitly converting BOTH sides to the
    // same type removes the ambiguity entirely and was confirmed reliable
    // the same way. A plain string/int/etc. column is unaffected - only a
    // DateTime value's own column reference needs the wrap.
    private static string BuildWhereClause(IReadOnlyDictionary<string, object?> row) =>
        string.Join(" AND ", row.Select(kv => kv.Value switch
        {
            null => $"{EscapeIdentifier(kv.Key)} IS NULL",
            DateTime => $"CONVERT(DATETIME2(7), {EscapeIdentifier(kv.Key)}) = {FormatSqlLiteral(kv.Value)}",
            _ => $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}",
        }));

    // Bracket-quotes each dot-separated part of an identifier (schema.table
    // or a bare column name) and doubles up any embedded "]" - the SQL
    // Server quoted-identifier escaping rule. Column names come from
    // sys.columns metadata rather than a free-text source, but nothing
    // stops a table from having one that contains SQL syntax characters,
    // and this text is meant to be reviewed and run by a human rather than
    // executed by LogCarver itself - an unescaped identifier would let such
    // a column name inject extra statements into what looks like a plain
    // suggestion.
    //
    // The table name specifically can arrive here ALREADY bracket-quoted -
    // the CLI's tableName argument is whatever the caller typed, and typing
    // "[dbo].[Orders]" is completely ordinary (it's exactly what SSMS's own
    // "Script Table as" produces). Re-escaping an already-bracketed part
    // without accounting for that doubled every bracket - "[dbo].[Orders]"
    // came out as "[[dbo]]].[[Orders]]]", found via real customer testing
    // 2026-09-30. Stripping one pre-existing layer of brackets (and
    // reversing their "]]" -> "]" escaping) before re-escaping makes this
    // idempotent: an already-bracketed part round-trips to the same
    // brackets, and an unbracketed part is escaped exactly as before.
    private static string EscapeIdentifier(string name) =>
        string.Join(".", name.Split('.').Select(part =>
        {
            string raw = part.Length >= 2 && part[0] == '[' && part[^1] == ']'
                ? part[1..^1].Replace("]]", "]")
                : part;
            return $"[{raw.Replace("]", "]]")}]";
        }));

    private static string FormatSqlLiteral(object? value) => value switch
    {
        null => "NULL",
        int i => i.ToString(),
        // Added alongside BIGINT column decoding in RowDecoder - missed
        // here would repeat exactly the DECIMAL/NUMERIC gap above: a real
        // customer table ([LOG].[JobRun]'s bigint JobID) hitting the
        // NotSupportedException below outright crashes the whole
        // --undo/--replay/--export sql run, losing every other row too.
        long l => l.ToString(),
        // Found 2026-10-08 via LogCarverGuard's Rocky9 stress test: a
        // DATETIME2(7)-precision column (SYSDATETIME() has sub-millisecond
        // precision almost every time) formatted to only 3 fractional
        // digits here means the undo WHERE clause's literal never equals
        // the real stored value - every single row misses the match and
        // UndoSqlGenerator's optimistic-concurrency guard false-positives
        // as "the data changed", when it never did. .NET DateTime's own
        // tick resolution is 100ns, exactly DATETIME2(7)'s own max
        // precision, so 7 fractional digits round-trips losslessly.
        //
        // Real regression in that same fix, found 2026-10-08 via an
        // independent cross-check agent's second testing round, within
        // hours of the first fix shipping: the claim above that "lower-
        // precision DATETIME/SMALLDATETIME columns just get trailing
        // zeros, which the implicit conversion still matches correctly"
        // was never actually verified against a real DATETIME column, and
        // was wrong - a bare 7-fractional-digit string literal
        // ('...12:00:00.0000000') against a DATETIME target throws SQL
        // Server error 241 ("Conversion failed when converting date
        // and/or time from character string") outright, confirmed via a
        // direct sqlcmd probe (CONVERT accepts up to 3 digits for
        // DATETIME's own string-literal grammar, not 7). This broke both
        // directions: an UPDATE undo's WHERE clause would throw instead of
        // just mismatching, and a DELETE undo's INSERT (which embeds the
        // literal directly into VALUES) failed outright on restore. Fixed
        // by wrapping in an explicit CONVERT(DATETIME2(7), '...') instead
        // of a bare string literal - DATETIME2(7)'s own string grammar
        // accepts all 7 digits unconditionally (confirmed, it's what this
        // whole fix already relies on), and converting a DATETIME2 value
        // (not a raw string) to DATETIME/SMALLDATETIME afterward is an
        // ordinary, always-succeeding, well-defined precision-narrowing
        // conversion - never a string-parse error - confirmed via the same
        // direct sqlcmd probe: CONVERT(DATETIME2(7), '...0000000') assigned
        // to a DATETIME variable rounds cleanly to '...000', no error.
        DateTime dt => $"CONVERT(DATETIME2(7), '{dt:yyyy-MM-dd HH:mm:ss.fffffff}')",
        // decimal.ToString() never uses scientific notation (unlike
        // double/float), so this always produces a plain SQL Server
        // decimal/numeric literal - InvariantCulture avoids a comma
        // decimal separator on a non-US locale. Added alongside DECIMAL/
        // NUMERIC column decoding in RowDecoder; missed here until a real
        // customer table (DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice) crashed the whole
        // --undo/--replay/--export sql run outright with "Cannot format a
        // SQL literal for value of type System.Decimal" - every other
        // column in every other row was lost too, not just this value.
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // HighPrecisionDecimal.ToString() already produces a plain decimal
        // literal (no scientific notation) - see that type's own doc
        // comment for why DECIMAL(29-38 digits) needs it instead of
        // System.Decimal. Added alongside the DecodeDecimal 4-group case -
        // missed here until an independent cross-check agent hit a real
        // DECIMAL(38,10) column 2026-10-08 and got the same
        // NotSupportedException class as every other missing-case gap
        // above, just one level removed (RowDecoder itself had already
        // been refusing to decode this precision range at all before that
        // same session's fix - see RowDecoder's own doc comment).
        HighPrecisionDecimal hp => hp.ToString(),
        string s => $"N'{s.Replace("'", "''")}'",
        // SQL Server accepts a UNIQUEIDENTIFIER literal as a plain quoted
        // string in its canonical "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
        // form, which Guid.ToString()'s default format already produces.
        // Same missing-case pattern as every entry above: found 2026-10-08
        // via an independent cross-check agent hitting a GUID primary key
        // (an entirely ordinary schema choice) - this one degraded safely
        // (caught by GuardedSqlExecutor's existing UndoGenerationError
        // handling, not a crash), but the underlying gap meant GUID-keyed
        // tables had zero undo capability, full stop.
        Guid g => $"'{g}'",
        // T-SQL has no boolean literal - BIT columns are written/compared
        // as 1/0. Same missing-case pattern as bigint/decimal above: found
        // 2026-10-02 via LogCarverGuard stress testing against a BIT
        // column, where it crashed undo generation outright with this same
        // NotSupportedException (caught further up the stack now, so it no
        // longer loses the whole audit record - see GuardedSqlExecutor's
        // UndoGenerationError - but the underlying gap is still real and
        // worth fixing at the source).
        bool b => b ? "1" : "0",
        _ => throw new NotSupportedException($"Cannot format a SQL literal for value of type {value.GetType()}."),
    };
}
