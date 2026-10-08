namespace LogCarver.Core.SqlServer;

public enum RowEventKind { Insert, Delete, Update }

public sealed record RowEvent(
    string Lsn,
    RowEventKind Kind,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After,
    string? Note,
    DateTime? Timestamp,
    string PageId,
    int SlotId,
    string? TransactionId = null)
{
    /// <summary>
    /// True whenever this event's data should not be trusted as-is without
    /// a human looking at it first - a decode limitation, a patch failure,
    /// or (most importantly) a value whose bytes crossed a 512-byte log
    /// block boundary the offline scanner can detect but not yet repair
    /// (see RowHistoryReconstructor's *CorruptionNote members). Every code
    /// path in this codebase that sets Note does so exactly because the
    /// event isn't fully trustworthy, so this is derived from Note rather
    /// than tracked as a second, independently-settable flag that could
    /// drift out of sync with it.
    /// </summary>
    public bool NeedsManualReview => Note is not null;
}

/// <summary>
/// Walks a table's log records in LSN order and reconstructs each
/// physical row slot's history, applying RowPatcher to UPDATE diffs
/// against the most recently known image of that slot.
///
/// Row identity: grouped by (Page ID, Slot ID), not by primary key or by
/// record order. A MODIFY_ROW diff generally does not touch the primary
/// key, so it cannot be used to identify which row changed; physical
/// location is what SQL Server itself uses to mean "the same row" for an
/// in-place update, and is the only thing every record type actually
/// carries. (The research prototype paired records positionally, which
/// only works because it drove requests through one connection in a known
/// order - that assumption does not hold against real, concurrent
/// workloads and is not repeated here.)
/// </summary>
public static class RowHistoryReconstructor
{
    /// <summary>
    /// Appended to Note for LOP_MODIFY_ROW (UPDATE) whenever the record
    /// (or its known before-image) was flagged PossiblyCorrupted - see
    /// LogRecord's doc comment. Coarse and whole-event, unlike
    /// INSERT/DELETE's precise per-column note (see
    /// <see cref="BuildColumnCorruptionNote"/>): translating a boundary
    /// position through RowPatcher's byte-range splice arithmetic to
    /// attribute it to one specific column isn't implemented yet, so this
    /// says "the diff or its base image touched a boundary somewhere",
    /// not which of Before/After's columns it actually affected.
    /// </summary>
    internal const string UpdateCorruptionNote =
        "this update's diff and/or its before-image bytes cross a 512-byte log block boundary offline scanning doesn't yet reconstruct - some value here may be wrong; verify independently before relying on it";

    private static string BuildColumnCorruptionNote(IReadOnlyList<string> columns) =>
        $"column(s) [{string.Join(", ", columns)}] may be wrong - byte(s) making up its/their value cross a 512-byte log block boundary offline scanning doesn't yet reconstruct; verify independently before relying on it";

    private static bool HasAny(IReadOnlyList<int>? offsets) => offsets is { Count: > 0 };

    public static IReadOnlyList<RowEvent> Reconstruct(
        IEnumerable<LogRecord> recordsInLsnOrder,
        IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<string> ddlBoundaryLsns,
        IReadOnlyDictionary<string, DateTime>? transactionBeginTimes = null)
    {
        var events = new List<RowEvent>();

        DateTime? TimestampOf(LogRecord r) =>
            r.TransactionId is not null && (transactionBeginTimes?.TryGetValue(r.TransactionId, out var t) ?? false)
                ? t
                : null;

        // Per physical slot: the most recently known row image, the LSN it
        // was actually written at, and whether that image's own record
        // touched a boundary anywhere in its RowLogContents (coarse - see
        // UpdateCorruptionNote for why this stays whole-record rather than
        // per-column) - a corrupt base image taints every future UPDATE
        // spliced onto it, not just the record it came from. The write
        // LSN - not the LSN of whatever record we're currently looking
        // at - is what the schema-drift guard must check: a row untouched
        // since before a metadata-only DDL (e.g. ADD COLUMN) is still
        // physically in the old layout even if we're now looking at it
        // from a later point in the log.
        var state = new Dictionary<(string PageId, int SlotId), (byte[] Bytes, string WrittenAtLsn, bool PossiblyCorrupted)>();

        static string? WithNote(string? existing, string? addition) => addition is null
            ? existing
            : (existing is null ? addition : $"{existing}; {addition}");

        foreach (var record in recordsInLsnOrder)
        {
            if (record.PageId is null || record.SlotId is null) continue;
            var key = (record.PageId, record.SlotId.Value);
            bool recordTouchedABoundary = HasAny(record.PossiblyCorruptedOffsetsInRowLogContents0) || HasAny(record.PossiblyCorruptedOffsetsInRowLogContents1);

            switch (record.Operation)
            {
                case "LOP_INSERT_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
                    {
                        var decoded = TryDecode(bytes, schema, record.Lsn, ddlBoundaryLsns,
                            record.PossiblyCorruptedOffsetsInRowLogContents0 ?? [], out var note, out var corruptedColumns);
                        if (corruptedColumns.Count > 0) note = WithNote(note, BuildColumnCorruptionNote(corruptedColumns));
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Insert, null, decoded, note, TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                        state[key] = (bytes, record.Lsn, recordTouchedABoundary);
                        break;
                    }

                case "LOP_DELETE_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
                    {
                        var decoded = TryDecode(bytes, schema, record.Lsn, ddlBoundaryLsns,
                            record.PossiblyCorruptedOffsetsInRowLogContents0 ?? [], out var note, out var corruptedColumns);
                        if (corruptedColumns.Count > 0) note = WithNote(note, BuildColumnCorruptionNote(corruptedColumns));
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Delete, decoded, null, note, TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                        state.Remove(key);
                        break;
                    }

                // LOP_MODIFY_COLUMNS is NOT a byte-range splice like LOP_MODIFY_ROW -
                // its RowLogContents0/1 are a column-level change descriptor (observed:
                // 8 and 4 bytes of small integers, not the row content itself; the new
                // value appears later in the physical record, outside RowLogContents
                // entirely - undecoded). SQL Server picks this operation over
                // LOP_MODIFY_ROW for some UPDATEs (observed: a large length change on
                // a column not covered by any index). Feeding its RowLogContents into
                // RowPatcher.Splice - as an earlier version of this method did by
                // grouping it with LOP_MODIFY_ROW - does not throw; it silently
                // produces a corrupted row that decodes to plausible-looking garbage
                // (e.g. a garbage Id and missing columns) with no error signal at all,
                // exactly what this project treats as the one unacceptable failure
                // mode. Refusing here, and forgetting this slot's last known image
                // (it's no longer trustworthy either), is required until this format
                // is reverse-engineered - do not merge this case back with
                // LOP_MODIFY_ROW's.
                case "LOP_MODIFY_COLUMNS":
                    {
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                            "diff not available (LOP_MODIFY_COLUMNS is a column-level change format, not a byte-range splice - not decoded)",
                            TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                        state.Remove(key);
                        break;
                    }

                case "LOP_MODIFY_ROW":
                    {
                        if (record.OffsetInRow is not int offset ||
                            record.RowLogContents0 is not { } rlc0 ||
                            record.RowLogContents1 is not { } rlc1)
                        {
                            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                                WithNote("diff not available (missing offset or RowLog Contents)", recordTouchedABoundary ? UpdateCorruptionNote : null),
                                TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                            break;
                        }

                        if (!state.TryGetValue(key, out var before))
                        {
                            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                                WithNote("before image unknown - this row's insert (or a prior update) is outside the observed log window", recordTouchedABoundary ? UpdateCorruptionNote : null),
                                TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                            break;
                        }

                        byte[] afterBytes;
                        try
                        {
                            afterBytes = RowPatcher.ApplyForward(before.Bytes, offset, rlc0, rlc1);
                        }
                        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                        {
                            // A corrupted byte in either image is a very
                            // plausible cause of exactly this failure (a
                            // byte-range splice that no longer lines up) -
                            // surface both images' flags, not just the
                            // current record's.
                            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null,
                                WithNote($"patch failed - before image and this diff do not line up ({ex.Message})",
                                    before.PossiblyCorrupted || recordTouchedABoundary ? UpdateCorruptionNote : null),
                                TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                            break;
                        }

                        var beforeDecoded = TryDecode(before.Bytes, schema, before.WrittenAtLsn, ddlBoundaryLsns, [], out var beforeNote, out var beforeCorruptedColumns);
                        var afterDecoded = TryDecode(afterBytes, schema, record.Lsn, ddlBoundaryLsns, [], out var afterNote, out var afterCorruptedColumns);
                        bool possiblyCorrupted = before.PossiblyCorrupted || recordTouchedABoundary;
                        // Real bug, found 2026-10-08 via an integration test
                        // for the off-row/LOB fix below: this branch used to
                        // discard both decodes' possiblyCorruptedColumns with
                        // `out _`, so an off-row column (or any other
                        // possiblyCorruptedColumns case) on an UPDATE never
                        // reached Note/NeedsManualReview at all - only
                        // INSERT/DELETE (above) ever built this note. Column
                        // names are deduplicated since Before and After
                        // commonly flag the same column.
                        var combinedCorruptedColumns = beforeCorruptedColumns.Concat(afterCorruptedColumns).Distinct().ToList();
                        string? columnCorruptionNote = combinedCorruptedColumns.Count > 0 ? BuildColumnCorruptionNote(combinedCorruptedColumns) : null;
                        string? note = WithNote(WithNote(beforeNote ?? afterNote, columnCorruptionNote), possiblyCorrupted ? UpdateCorruptionNote : null);
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Update, beforeDecoded, afterDecoded, note, TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                        state[key] = (afterBytes, record.Lsn, possiblyCorrupted);
                        break;
                    }
            }
        }

        return events;
    }

    private static IReadOnlyDictionary<string, object?>? TryDecode(
        byte[] bytes, IReadOnlyList<ColumnSchema> schema, string lsn, IReadOnlyList<string> ddlBoundaryLsns,
        IReadOnlyList<int> possiblyCorruptedOffsets, out string? note, out IReadOnlyList<string> possiblyCorruptedColumns)
    {
        try
        {
            note = null;
            return RowDecoder.Decode(bytes, schema, lsn, ddlBoundaryLsns, possiblyCorruptedOffsets, out possiblyCorruptedColumns);
        }
        catch (Exception ex) when (ex is SchemaDriftException or NotSupportedException or UnsupportedRowFormatException)
        {
            note = ex.Message;
            possiblyCorruptedColumns = [];
            return null;
        }
    }
}
