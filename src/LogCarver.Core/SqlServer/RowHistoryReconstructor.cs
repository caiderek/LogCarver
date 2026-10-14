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

    /// <param name="seedImages">
    /// Known images, by <see cref="RowIdentity"/>, of rows whose INSERT lies
    /// outside the records given (see RowImageAnchoring). Used only as the
    /// starting image of a row's first UPDATE; null or missing entries leave
    /// that UPDATE reported as "before image unknown".
    /// </param>
    public static IReadOnlyList<RowEvent> Reconstruct(
        IEnumerable<LogRecord> recordsInLsnOrder,
        IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<string> ddlBoundaryLsns,
        IReadOnlyDictionary<string, DateTime>? transactionBeginTimes = null,
        IReadOnlyDictionary<string, RowImageSeed>? seedImages = null)
    {
        var events = new List<RowEvent>();

        DateTime? TimestampOf(LogRecord r) =>
            r.TransactionId is not null && (transactionBeginTimes?.TryGetValue(r.TransactionId, out var t) ?? false)
                ? t
                : null;

        // Per row (see RowIdentity): the most recently known row image, the
        // LSN it was actually written at, whether that image's own record
        // touched a boundary anywhere in its RowLogContents (coarse - see
        // UpdateCorruptionNote for why this stays whole-record rather than
        // per-column) - a corrupt base image taints every future UPDATE
        // spliced onto it, not just the record it came from - and the page
        // it was last seen on. The write LSN - not the LSN of whatever
        // record we're currently looking at - is what the schema-drift guard
        // must check: a row untouched since before a metadata-only DDL
        // (e.g. ADD COLUMN) is still physically in the old layout even if
        // we're now looking at it from a later point in the log.
        var state = new Dictionary<string, RowState>();
        var rowsByPage = new Dictionary<string, HashSet<string>>();
        var seeded = new HashSet<string>();
        var rowKeys = new RowKeyIndex();

        void Remember(string key, string pageId, RowState row)
        {
            state[key] = row;
            if (!rowsByPage.TryGetValue(pageId, out var keys))
                rowsByPage[pageId] = keys = [];
            keys.Add(key);
        }

        static string? WithNote(string? existing, string? addition) => addition is null
            ? existing
            : (existing is null ? addition : $"{existing}; {addition}");

        // Shared by LOP_MODIFY_ROW and LOP_MODIFY_COLUMNS once each has
        // produced the row's after-image: decode both images and record the
        // new image as the row's known state.
        void EmitPatchedUpdate(LogRecord record, string key, RowState before, byte[] afterBytes, bool recordTouchedABoundary)
        {
            var beforeDecoded = TryDecode(before.Bytes, schema, before.WrittenAtLsn, ddlBoundaryLsns, [], out var beforeNote, out var beforeCorruptedColumns);
            var afterDecoded = TryDecode(afterBytes, schema, record.Lsn, ddlBoundaryLsns, [], out var afterNote, out var afterCorruptedColumns);
            bool possiblyCorrupted = before.PossiblyCorrupted || recordTouchedABoundary;
            // This branch used to discard both decodes'
            // possiblyCorruptedColumns with `out _`, so an
            // off-row column (or any other possiblyCorruptedColumns
            // case) on an UPDATE never reached Note/NeedsManualReview
            // at all - only INSERT/DELETE (above) ever built this
            // note. Column names are deduplicated since Before
            // and After commonly flag the same column.
            var combinedCorruptedColumns = beforeCorruptedColumns.Concat(afterCorruptedColumns).Distinct().ToList();
            string? columnCorruptionNote = combinedCorruptedColumns.Count > 0 ? BuildColumnCorruptionNote(combinedCorruptedColumns) : null;
            string? note = WithNote(WithNote(beforeNote ?? afterNote, columnCorruptionNote), possiblyCorrupted ? UpdateCorruptionNote : null);
            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, beforeDecoded, afterDecoded, note, TimestampOf(record), record.PageId!, record.SlotId!.Value, record.TransactionId));
            Remember(key, record.PageId!, new RowState(afterBytes, record.Lsn, possiblyCorrupted, record.PageId!));
        }

        void EmitRefused(LogRecord record, string? note) =>
            events.Add(new RowEvent(record.Lsn, RowEventKind.Update, null, null, note,
                TimestampOf(record), record.PageId!, record.SlotId!.Value, record.TransactionId));

        foreach (var record in recordsInLsnOrder)
        {
            if (record.PageId is null || record.SlotId is null) continue;
            string key = rowKeys.Resolve(record);
            if (record.Operation is "LOP_INSERT_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS")
                rowKeys.Observe(record, key);
            else if (record.Operation == "LOP_DELETE_ROWS")
                rowKeys.ForgetSlot(record);
            bool recordTouchedABoundary = HasAny(record.PossiblyCorruptedOffsetsInRowLogContents0) || HasAny(record.PossiblyCorruptedOffsetsInRowLogContents1);

            // A row first seen through an UPDATE starts from its anchored
            // image, if one was recovered for it.
            if (record.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" &&
                !state.ContainsKey(key) && seeded.Add(key) &&
                seedImages is not null && seedImages.TryGetValue(key, out var seed))
            {
                Remember(key, record.PageId, new RowState(seed.Bytes, seed.AsOfLsn, false, record.PageId));
            }

            switch (record.Operation)
            {
                case "LOP_INSERT_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
                    {
                        // A heap row that no longer fits where it is gets moved:
                        // the log shows a forwarded copy of its old image being
                        // "inserted" elsewhere, followed by the real change. The
                        // row already existed, so this is not an INSERT - only
                        // the location the following changes will be logged at.
                        if (RowFormat.IsForwardedRecord(bytes))
                        {
                            Remember(key, record.PageId, new RowState(bytes, record.Lsn, recordTouchedABoundary, record.PageId));
                            break;
                        }

                        // A page split caused by an UPDATE re-inserts the moved
                        // row on the new page, byte for byte. The row is
                        // already known, and a key cannot be inserted twice, so
                        // this is the row moving, not a new row. (Reporting it
                        // as an INSERT also made an undo of that transaction
                        // delete the row.)
                        if (state.TryGetValue(key, out var alreadyKnown) && alreadyKnown.Bytes.AsSpan().SequenceEqual(bytes))
                        {
                            Remember(key, record.PageId, alreadyKnown with { PageId = record.PageId });
                            break;
                        }

                        var decoded = TryDecode(bytes, schema, record.Lsn, ddlBoundaryLsns,
                            record.PossiblyCorruptedOffsetsInRowLogContents0 ?? [], out var note, out var corruptedColumns);
                        if (corruptedColumns.Count > 0) note = WithNote(note, BuildColumnCorruptionNote(corruptedColumns));
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Insert, null, decoded, note, TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));
                        Remember(key, record.PageId, new RowState(bytes, record.Lsn, recordTouchedABoundary, record.PageId));
                        break;
                    }

                case "LOP_DELETE_ROWS" when record.RowLogContents0 is { Length: > 0 } bytes:
                    {
                        // The pointer left behind where a forwarded row used to
                        // sit: it carries no data, the row's own DELETE is the
                        // one logged on the forwarded record.
                        if (RowFormat.IsForwardingStub(bytes))
                        {
                            state.Remove(key);
                            break;
                        }

                        // A forwarded record is also removed when the row moves
                        // back to its original place. That is not a delete if the
                        // original place has already been given its row back.
                        if (RowFormat.IsForwardedRecord(bytes) &&
                            RowFormat.ForwardedFromKey(bytes) is { } origin &&
                            state.TryGetValue(origin, out var originState) &&
                            !RowFormat.IsForwardingStub(originState.Bytes))
                        {
                            state.Remove(key);
                            break;
                        }

                        var decoded = TryDecode(bytes, schema, record.Lsn, ddlBoundaryLsns,
                            record.PossiblyCorruptedOffsetsInRowLogContents0 ?? [], out var note, out var corruptedColumns);
                        if (corruptedColumns.Count > 0) note = WithNote(note, BuildColumnCorruptionNote(corruptedColumns));
                        events.Add(new RowEvent(record.Lsn, RowEventKind.Delete, decoded, null, note, TimestampOf(record), record.PageId, record.SlotId.Value, record.TransactionId));

                        // A DELETE carries no key hash, so the row it removes
                        // is found by its bytes: the image being deleted is
                        // exactly the last known image of that row.
                        state.Remove(key);
                        if (rowsByPage.TryGetValue(record.PageId, out var pageKeys))
                        {
                            foreach (var candidate in pageKeys.ToList())
                            {
                                if (state.TryGetValue(candidate, out var known) && known.Bytes.AsSpan().SequenceEqual(bytes))
                                {
                                    state.Remove(candidate);
                                    pageKeys.Remove(candidate);
                                }
                            }
                        }
                        break;
                    }

                // LOP_MODIFY_COLUMNS is NOT a byte-range splice like LOP_MODIFY_ROW -
                // it is a list of edits (see ModifyColumnsPatcher for the format).
                // Feeding its RowLogContents into RowPatcher.Splice - as an earlier
                // version of this method did by grouping it with LOP_MODIFY_ROW -
                // does not throw; it silently produces a corrupted row that decodes
                // to plausible-looking garbage (e.g. a garbage Id and missing
                // columns) with no error signal at all, exactly what this project
                // treats as the one unacceptable failure mode. Do not merge this
                // case back with LOP_MODIFY_ROW's. It is only decoded when
                // ModifyColumnsPatcher can verify every edit against the known
                // before image; otherwise it is refused and the row's last known
                // image is forgotten (it is no longer trustworthy either).
                case "LOP_MODIFY_COLUMNS":
                    {
                        const string notDecoded = "diff not available (LOP_MODIFY_COLUMNS could not be decoded safely";

                        if (record.RowLogContents0 is not { } mc0 ||
                            record.RowLogContents1 is not { } mc1 ||
                            record.RowLogContents4 is not { } mc4)
                        {
                            EmitRefused(record, WithNote($"{notDecoded}: this reader does not supply RowLog Contents 0/1/4)", recordTouchedABoundary ? UpdateCorruptionNote : null));
                            state.Remove(key);
                            break;
                        }

                        if (!state.TryGetValue(key, out var before))
                        {
                            EmitRefused(record, WithNote(BeforeImageUnknownNote, recordTouchedABoundary ? UpdateCorruptionNote : null));
                            break;
                        }

                        if (!ModifyColumnsPatcher.TryApplyForward(before.Bytes, mc0, mc1, mc4, out var patched, out var failure))
                        {
                            EmitRefused(record, WithNote($"{notDecoded}: {failure})", before.PossiblyCorrupted || recordTouchedABoundary ? UpdateCorruptionNote : null));
                            state.Remove(key);
                            break;
                        }

                        EmitPatchedUpdate(record, key, before, patched!, recordTouchedABoundary);
                        break;
                    }

                case "LOP_MODIFY_ROW":
                    {
                        if (record.OffsetInRow is not int offset ||
                            record.RowLogContents0 is not { } rlc0 ||
                            record.RowLogContents1 is not { } rlc1)
                        {
                            EmitRefused(record, WithNote("diff not available (missing offset or RowLog Contents)", recordTouchedABoundary ? UpdateCorruptionNote : null));
                            break;
                        }

                        // Heap forwarding. The row's real change is logged on the
                        // forwarded copy; what happens at the original place is
                        // only bookkeeping, so it produces no event of its own.
                        if (offset == 0 && RowFormat.IsForwardingStub(rlc1))
                        {
                            // Row -> pointer to where it went.
                            Remember(key, record.PageId, new RowState(rlc1, record.Lsn, false, record.PageId));
                            break;
                        }

                        if (offset == 0 && RowFormat.IsForwardingStub(rlc0) && RowFormat.IsPrimaryRecord(rlc1))
                        {
                            // Pointer -> the row has come back here.
                            Remember(key, record.PageId, new RowState(rlc1, record.Lsn, recordTouchedABoundary, record.PageId));
                            break;
                        }

                        if (!state.TryGetValue(key, out var before))
                        {
                            EmitRefused(record, WithNote(BeforeImageUnknownNote, recordTouchedABoundary ? UpdateCorruptionNote : null));
                            break;
                        }

                        // RowLogContents0 is, by definition, the old bytes of
                        // the span being replaced - they must be what the
                        // known image has there. A mismatch means the image
                        // is not this row's (or is stale), and splicing would
                        // yield a plausible-looking wrong row.
                        if (offset < 0 || offset + rlc0.Length > before.Bytes.Length ||
                            !before.Bytes.AsSpan(offset, rlc0.Length).SequenceEqual(rlc0))
                        {
                            EmitRefused(record, WithNote("patch refused - the logged old bytes do not match the known before image",
                                before.PossiblyCorrupted || recordTouchedABoundary ? UpdateCorruptionNote : null));
                            state.Remove(key);
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
                            EmitRefused(record, WithNote($"patch failed - before image and this diff do not line up ({ex.Message})",
                                before.PossiblyCorrupted || recordTouchedABoundary ? UpdateCorruptionNote : null));
                            break;
                        }

                        EmitPatchedUpdate(record, key, before, afterBytes, recordTouchedABoundary);
                        break;
                    }
            }
        }

        return events;
    }

    internal const string BeforeImageUnknownNote =
        "before image unknown - this row's insert (or a prior update) is outside the observed log window";

    private sealed record RowState(byte[] Bytes, string WrittenAtLsn, bool PossiblyCorrupted, string PageId);

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
