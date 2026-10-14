using System.Globalization;
using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>Where the anchoring step reads data pages and row locations from.</summary>
public interface IAnchorSource
{
    /// <summary>The rows of a data page, or null when the page cannot be read.</summary>
    Task<IReadOnlyList<PageRow>?> ReadPageAsync(string pageId, CancellationToken ct);

    /// <summary>
    /// Key hash (upper-case hex) to the page currently holding that row, for
    /// the hashes that identify exactly one row; empty when it cannot be asked.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> LocateByKeyHashAsync(IReadOnlyList<string> hashes, CancellationToken ct);
}

/// <summary>
/// Recovers the starting image of rows whose INSERT is not in the log, so that
/// their UPDATEs - by far the common case for any row that is not brand new -
/// can still be decoded.
///
/// An UPDATE record only holds the bytes that changed, so it cannot be decoded
/// on its own; the reconstructor needs the row as it was before. When the
/// row's INSERT (or an earlier UPDATE) has already left the log, that image is
/// not there. But the row's image AFTER its last logged change is available:
///   - from the data page, if the row still exists - found by its key hash, on
///     the page it was last logged on or, if a page split moved it, on
///     whichever page the table says it is on now (a heap row, which has no
///     hash, only from the exact page and slot it was logged at and only if
///     nothing was inserted at or deleted from that slot since), or
///   - from the image of a later DELETE of the same page and slot when no
///     insert or physical delete at that slot or a lower one happened on the
///     page in between (those are the only things that move a clustered
///     row's slot, so the slot cannot mean another row), or
///   - from the row being re-inserted, byte for byte, by a page split.
/// Undoing the row's logged changes from that image, newest first, gives the
/// image before the first one.
///
/// Nothing here is taken on trust. Undoing a change requires that the bytes
/// the record says it wrote are really there, and the result must be a
/// self-consistent row. Because that is not proof of identity for a short
/// field, a seed is only given when exactly ONE source yields one: if the row
/// could also have been deleted and re-inserted, or two DELETE images
/// verify, there is no seed and the UPDATE stays "before image unknown".
/// </summary>
public static class RowImageAnchoring
{
    public static Task<IReadOnlyDictionary<string, RowImageSeed>> BuildSeedsAsync(
        SqlConnection connection, string tableName, IReadOnlyList<LogRecord> recordsInLsnOrder,
        CancellationToken ct = default, string? onlyTransactionId = null, bool logWasBounded = false,
        IReadOnlyList<LogRecord>? recordsAfterBound = null) =>
        BuildSeedsAsync(new SqlAnchorSource(connection, tableName), recordsInLsnOrder, ct, onlyTransactionId, logWasBounded, recordsAfterBound);

    /// <param name="onlyTransactionId">
    /// When given, only rows this transaction touched are anchored: an undo
    /// does not want a data page read for every old row anyone updated.
    /// </param>
    /// <param name="logWasBounded">
    /// The records stop at some LSN (a transaction's commit) although the log
    /// goes on. The data page shows the row as it is NOW, so it is only
    /// evidence of the row as of the bound if nothing touched the row after it.
    /// </param>
    /// <param name="recordsAfterBound">
    /// The table's records after the bound. With a bounded log, a row is
    /// anchored from its data page unless it may have been deleted and
    /// inserted again (a later DELETE together with a later INSERT of its key
    /// or a hash-less INSERT; for a heap row, any DELETE or INSERT on its
    /// page). Later UPDATEs do not matter: undoing the chain on the row as it
    /// is now takes back only this transaction's changes. Null means the
    /// later records are not known, so no row is anchored from its page.
    /// </param>
    public static async Task<IReadOnlyDictionary<string, RowImageSeed>> BuildSeedsAsync(
        IAnchorSource source, IReadOnlyList<LogRecord> recordsInLsnOrder,
        CancellationToken ct = default, string? onlyTransactionId = null, bool logWasBounded = false,
        IReadOnlyList<LogRecord>? recordsAfterBound = null)
    {
        var seeds = new Dictionary<string, RowImageSeed>();

        // The same row-tracking rules the reconstructor uses, so a chain here
        // is exactly the set of records the reconstructor will see without an
        // earlier image.
        var rowKeys = new RowKeyIndex();

        // Per row: the changes seen before anything gave the reconstructor its
        // image (no INSERT of it in the log).
        var chains = new Dictionary<string, List<LogRecord>>();
        var known = new HashSet<string>();
        // Keys that were inserted again after changes were seen: the present
        // row may be another incarnation, so the live page and later DELETE
        // images are not evidence for them.
        var reinserted = new Dictionary<string, LogRecord>();
        // The subset whose re-insert looks like a page split moving the row
        // (another page, and the key was not deleted in between): only those
        // may serve as the image source.
        var splitMoves = new Dictionary<string, LogRecord>();
        var lastDeleteLsn = new Dictionary<string, string>();
        var lastInsertPageOfTx = new Dictionary<string, string>();
        var deletesByPage = new Dictionary<string, List<LogRecord>>();
        var churnByPage = new Dictionary<string, List<(string Lsn, int Slot, bool Clustered)>>();

        // A page formatted by something other than an insert or a split (an
        // index rebuild, say) puts rows where the log has no per-row record:
        // where a row is "now" cannot be followed then.
        var formatTx = new HashSet<string>();
        var insertOrSplitTx = new HashSet<string>();

        foreach (var record in recordsInLsnOrder)
        {
            if (record.TransactionId is not null)
            {
                if (record.Operation == "LOP_FORMAT_PAGE") formatTx.Add(record.TransactionId);
                else if (record.Operation is "LOP_INSERT_ROWS" or "LOP_DELETE_SPLIT") insertOrSplitTx.Add(record.TransactionId);
            }

            if (record.PageId is null || record.SlotId is null) continue;
            if (record.Operation == "LOP_FORMAT_PAGE") continue;
            string key = rowKeys.Resolve(record);
            if (record.Operation is "LOP_INSERT_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS")
                rowKeys.Observe(record, key);
            else if (record.Operation == "LOP_DELETE_ROWS")
            {
                lastDeleteLsn[key] = record.Lsn;
                rowKeys.ObserveDelete(record);
            }
            else if (record.Operation == "LOP_DELETE_SPLIT")
            {
                // The same bookkeeping as the reconstructor: rows from the
                // split slot up are on the new page now, and nothing that
                // relied on those slots of the old page holds any more.
                if (!churnByPage.TryGetValue(record.PageId, out var splitChurn))
                    churnByPage[record.PageId] = splitChurn = [];
                splitChurn.Add((record.Lsn, record.SlotId.Value, true));

                if (record.IsCompensation)
                {
                    rowKeys.ForgetFromSlot(record.PageId, 0);
                    if (record.TransactionId is not null && lastInsertPageOfTx.TryGetValue(record.TransactionId, out var undonePage))
                        rowKeys.ForgetFromSlot(undonePage, 0);
                }
                else if (record.TransactionId is not null &&
                         lastInsertPageOfTx.TryGetValue(record.TransactionId, out var newPage) && newPage != record.PageId &&
                         rowKeys.ObserveSplit(record.PageId, record.SlotId.Value, newPage) is not null)
                {
                    // moved
                }
                else
                {
                    rowKeys.ForgetFromSlot(record.PageId, record.SlotId.Value);
                }
                continue;
            }

            if (record.Operation == "LOP_INSERT_ROWS" && record.TransactionId is not null)
                lastInsertPageOfTx[record.TransactionId] = record.PageId;

            if (record.Operation is "LOP_INSERT_ROWS" or "LOP_DELETE_ROWS")
            {
                if (!churnByPage.TryGetValue(record.PageId, out var churn))
                    churnByPage[record.PageId] = churn = [];

                // A clustered page's slots move when a row is inserted, or
                // physically removed, at or below them. A ghosted delete leaves
                // the row where it is; a heap's slots never move, they are
                // only reused.
                bool shifts = record.Context == "LCX_CLUSTERED";
                bool slotReuse = record.Context == "LCX_HEAP";
                if (shifts || slotReuse || record.Operation == "LOP_INSERT_ROWS")
                    churn.Add((record.Lsn, record.SlotId.Value, shifts));
            }

            switch (record.Operation)
            {
                case "LOP_INSERT_ROWS":
                    // The same key inserted again after changes were seen: a
                    // page split moving the row, or a delete and a re-insert.
                    if (chains.TryGetValue(key, out var chainSoFar) && !known.Contains(key) && record.RowLogContents0 is { Length: > 0 })
                    {
                        if (reinserted.TryAdd(key, record))
                        {
                            var chainEnd = chainSoFar[^1];
                            bool deletedSince = lastDeleteLsn.TryGetValue(key, out var deletedAt) &&
                                                string.CompareOrdinal(deletedAt, chainEnd.Lsn) > 0;
                            if (record.PageId != chainEnd.PageId && !deletedSince)
                                splitMoves[key] = record;
                        }
                    }
                    known.Add(key);
                    break;

                case "LOP_DELETE_ROWS" when record.RowLogContents0 is { Length: > 0 }:
                    if (!deletesByPage.TryGetValue(record.PageId, out var onPage))
                        deletesByPage[record.PageId] = onPage = [];
                    onPage.Add(record);
                    break;

                // Heap forwarding. A row that is turned into a pointer is
                // logged with its complete image up to that point, so that
                // image is the anchor for everything logged on it before.
                case "LOP_MODIFY_ROW" when !known.Contains(key) &&
                                           record.OffsetInRow == 0 &&
                                           record.RowLogContents0 is { } rowImage &&
                                           record.RowLogContents1 is { } pointer &&
                                           RowFormat.IsForwardingStub(pointer) && RowFormat.IsPrimaryRecord(rowImage):
                    if (chains.Remove(key, out var beforeForwarding))
                    {
                        var seed = UndoChain(rowImage, beforeForwarding);
                        if (seed is not null)
                            seeds[key] = new RowImageSeed(seed, beforeForwarding[0].Lsn);
                    }
                    known.Add(key);
                    break;

                // The pointer is turned back into the row: its image is known
                // from here on (the reconstructor takes it from the record).
                case "LOP_MODIFY_ROW" when record.OffsetInRow == 0 &&
                                           record.RowLogContents0 is { } oldPointer &&
                                           record.RowLogContents1 is { } rowBack &&
                                           RowFormat.IsForwardingStub(oldPointer) && RowFormat.IsPrimaryRecord(rowBack):
                    chains.Remove(key);
                    known.Add(key);
                    break;

                case "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" when !known.Contains(key):
                    if (!chains.TryGetValue(key, out var chain))
                        chains[key] = chain = [];
                    chain.Add(record);
                    break;
            }
        }

        // How many chains (of ANY transaction) a DELETE image verifies for. The
        // count is taken before chains are narrowed to the requested
        // transaction: the ambiguity is real even if the other chain is not
        // one anyone asked about.
        var chainsPerDelete = new Dictionary<string, int>();
        foreach (var (key, chain) in chains)
        {
            var last = chain[^1];
            if (!deletesByPage.TryGetValue(last.PageId!, out var pageDeletes)) continue;
            foreach (var delete in pageDeletes)
            {
                if (DeleteImageFor(key, chain, delete) is not null)
                    chainsPerDelete[delete.Lsn] = chainsPerDelete.GetValueOrDefault(delete.Lsn) + 1;
            }
        }

        if (onlyTransactionId is not null)
        {
            foreach (var key in chains.Where(c => !c.Value.Any(r => r.TransactionId == onlyTransactionId))
                                       .Select(c => c.Key).ToList())
                chains.Remove(key);
        }

        // Chains that already have a seed from the pointer-conversion image
        // need nothing further.
        foreach (var key in seeds.Keys.ToList())
            chains.Remove(key);

        if (chains.Count == 0)
            return seeds;

        // Did anything between the two LSNs change what this slot of this page
        // means? (Page splits are not visible; a row a split moved elsewhere
        // simply is not found where it was, so they only cause a refusal.)
        bool SlotMayHaveChanged(string pageId, int slot, bool clustered, string afterLsn, string beforeLsn)
        {
            if (!churnByPage.TryGetValue(pageId, out var changes))
                return false;
            foreach (var (lsn, changedSlot, shifts) in changes)
            {
                if (string.CompareOrdinal(lsn, afterLsn) <= 0) continue;
                if (string.CompareOrdinal(lsn, beforeLsn) >= 0) break;
                if (clustered ? changedSlot <= slot : changedSlot == slot) return true;
            }
            return false;
        }

        // The image of a later DELETE that could be this chain's row's last
        // image: same page and slot, nothing moved the slot in between, not
        // after a re-insert, and the chain verifies from it.
        byte[]? DeleteImageFor(string key, List<LogRecord> chain, LogRecord delete)
        {
            var last = chain[^1];
            if (string.CompareOrdinal(delete.Lsn, last.Lsn) <= 0) return null;
            if (delete.SlotId != last.SlotId) return null;
            if (SlotMayHaveChanged(last.PageId!, last.SlotId!.Value, last.Context != "LCX_HEAP", last.Lsn, delete.Lsn)) return null;
            if (reinserted.TryGetValue(key, out var ins) && string.CompareOrdinal(delete.Lsn, ins.Lsn) > 0) return null;
            return UndoChain(delete.RowLogContents0!, chain);
        }

        var pageCache = new Dictionary<string, IReadOnlyList<PageRow>?>();

        async Task<IReadOnlyList<PageRow>?> PageAsync(string pageId)
        {
            if (!pageCache.TryGetValue(pageId, out var rows))
                pageCache[pageId] = rows = await source.ReadPageAsync(pageId, ct);
            return rows;
        }

        // With a bounded log: could the row on the data page be a different
        // incarnation of this row, or a different row in this slot?
        //
        // Later UPDATEs - by anyone, of any column - are fine: the chain is
        // undone on the row as it is now, every step verifies the bytes it
        // says it wrote, so the result is the row as it is now with only this
        // transaction's changes taken back, and that is exactly the column
        // level undo wanted (a later change to the same bytes fails to verify
        // and is a real conflict). What cannot be told is a row that was
        // DELETEd and inserted again: a new row, same key. That needs a later
        // DELETE and a later INSERT.
        bool deletedAfter = false;
        bool hashlessInsertAfter = false;
        var insertedHashesAfter = new HashSet<string>();
        var deletePagesAfter = new HashSet<string>();
        var insertPagesAfter = new HashSet<string>();
        if (logWasBounded && recordsAfterBound is not null)
        {
            foreach (var later in recordsAfterBound)
            {
                if (later.Operation == "LOP_DELETE_ROWS")
                {
                    deletedAfter = true;
                    if (later.PageId is not null) deletePagesAfter.Add(later.PageId);
                }
                else if (later.Operation == "LOP_INSERT_ROWS")
                {
                    if (later.PageId is not null) insertPagesAfter.Add(later.PageId);
                    string? laterHash = RowIdentity.KeyHash(RowIdentity.LockInformation(later));
                    if (laterHash is not null) insertedHashesAfter.Add(laterHash);
                    else hashlessInsertAfter = true;
                }
            }
        }

        bool LiveIsAsOfTheBound(LogRecord last, string? hash)
        {
            if (!logWasBounded) return true;
            if (recordsAfterBound is null) return false;
            if (hash is not null)
            {
                // A page split re-inserts the same key without any DELETE of
                // it; only delete + insert can make a new row.
                return !(deletedAfter && (insertedHashesAfter.Contains(hash) || hashlessInsertAfter));
            }
            if (last.Context != "LCX_HEAP")
                return !(deletedAfter && (insertedHashesAfter.Count > 0 || hashlessInsertAfter));
            // No hash (a heap row): its slot is only the row's if no row was
            // removed from or put on that page afterwards.
            return !deletePagesAfter.Contains(last.PageId!) && !insertPagesAfter.Contains(last.PageId!);
        }

        // The row index has followed every record up to the bound; carry it
        // through what happened after, so that "where is this row now" holds
        // for rows that have no key hash (see below) as well.
        var layoutChangedKeys = new HashSet<string>();
        if (logWasBounded && recordsAfterBound is not null)
        {
            var insertPageOfTx = new Dictionary<string, string>();
            foreach (var later in recordsAfterBound)
            {
                if (later.PageId is null || later.SlotId is null) continue;
                switch (later.Operation)
                {
                    case "LOP_INSERT_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS":
                    {
                        string laterKey = rowKeys.Resolve(later);
                        if (later.Operation != "LOP_INSERT_ROWS" && ChangesRowLayout(later))
                            layoutChangedKeys.Add(laterKey);
                        rowKeys.Observe(later, laterKey);
                        if (later.Operation == "LOP_INSERT_ROWS" && later.TransactionId is not null)
                            insertPageOfTx[later.TransactionId] = later.PageId;
                        break;
                    }
                    case "LOP_DELETE_ROWS":
                        rowKeys.ObserveDelete(later);
                        break;
                    case "LOP_DELETE_SPLIT":
                        if (later.IsCompensation)
                        {
                            rowKeys.ForgetFromSlot(later.PageId, 0);
                            if (later.TransactionId is not null && insertPageOfTx.TryGetValue(later.TransactionId, out var undone))
                                rowKeys.ForgetFromSlot(undone, 0);
                        }
                        else if (later.TransactionId is not null &&
                                 insertPageOfTx.TryGetValue(later.TransactionId, out var splitTo) && splitTo != later.PageId &&
                                 rowKeys.ObserveSplit(later.PageId, later.SlotId.Value, splitTo) is not null)
                        {
                            // moved
                        }
                        else
                        {
                            rowKeys.ForgetFromSlot(later.PageId, later.SlotId.Value);
                        }
                        break;
                }
            }
        }

        // Pass 1: where is each chain's row on its last-known page?
        var onLastPage = new Dictionary<string, byte[]?>();
        var unlocated = new List<string>();
        foreach (var (key, chain) in chains)
        {
            ct.ThrowIfCancellationRequested();
            if (reinserted.ContainsKey(key)) continue; // the present row may be another incarnation

            var last = chain[^1];
            string? hash = RowIdentity.KeyHash(last.RowLogContents3);
            if (!LiveIsAsOfTheBound(last, hash)) continue;

            // A clustered row whose record carries no key hash (lock
            // escalation) has no identity on the page by itself, but every
            // move the log shows - slot shifts, splits, deletes - has been
            // followed, so where it is now is known. The chain still has to
            // verify against the bytes found there.
            if (hash is null && last.Context != "LCX_HEAP")
            {
                if (!formatTx.All(insertOrSplitTx.Contains)) continue;
                if (rowKeys.PlaceOf(key) is { } place &&
                    await PageAsync(place.Page) is { } placeRows &&
                    placeRows.FirstOrDefault(r => r.SlotId == place.Slot &&
                                                  r.RecordType is "PRIMARY_RECORD" or "FORWARDED_RECORD") is { } found)
                    onLastPage[key] = found.Bytes;
                continue;
            }

            var rows = await PageAsync(last.PageId!);
            byte[]? image = rows is null ? null : PickRow(rows, last, hash, SlotMayHaveChanged);
            if (image is not null)
                onLastPage[key] = image;
            else if (hash is not null)
                unlocated.Add(key);
        }

        // Pass 2: a page split moved them - ask the table where the hash is now.
        var wanted = unlocated
            .Select(k => RowIdentity.KeyHash(chains[k][^1].RowLogContents3)!)
            .Distinct().ToList();
        var locations = wanted.Count == 0
            ? new Dictionary<string, string>()
            : (await source.LocateByKeyHashAsync(wanted, ct)).ToDictionary(p => p.Key, p => p.Value);

        foreach (var key in unlocated)
        {
            var last = chains[key][^1];
            string hash = RowIdentity.KeyHash(last.RowLogContents3)!;
            if (!locations.TryGetValue(hash, out var pageId)) continue;
            var rows = await PageAsync(pageId);
            if (rows is null) continue;
            var matches = rows.Where(r => r.RecordType is "PRIMARY_RECORD" or "FORWARDED_RECORD" && r.KeyHash == hash).ToList();
            if (matches.Count == 1)
                onLastPage[key] = matches[0].Bytes;
        }

        // Decide, per chain, from the sources that verify.
        var candidatesByKey = new Dictionary<string, List<(string Source, byte[] Image, string? DeleteLsn)>>();
        foreach (var (key, chain) in chains)
        {
            var last = chain[^1];
            var candidates = new List<(string Source, byte[] Image, string? DeleteLsn)>();

            if (onLastPage.TryGetValue(key, out var live) && live is not null &&
                // A later UPDATE that changed the row's length shifts every
                // byte behind it: this chain's offsets then address other
                // bytes of the present row. Only a chain that lies wholly in
                // the fixed-length part is unaffected by that.
                (!layoutChangedKeys.Contains(key) || ChainStaysInFixedRegion(chain, live)) &&
                UndoChain(live, chain) is { } fromLive)
                candidates.Add(("live", fromLive, null));

            if (splitMoves.TryGetValue(key, out var again) && again.RowLogContents0 is { } insertedImage &&
                UndoChain(insertedImage, chain) is { } fromReinsert)
                candidates.Add(("reinsert", fromReinsert, null));

            if (deletesByPage.TryGetValue(last.PageId!, out var deletes))
            {
                foreach (var delete in deletes)
                {
                    if (DeleteImageFor(key, chain, delete) is { } fromDelete)
                        candidates.Add(("delete", fromDelete, delete.Lsn));
                }
            }

            candidatesByKey[key] = candidates;
        }

        // One DELETE image can only be one row's last image. If it verifies
        // for several chains (counted above), which of them it belongs to is
        // not known: none of them takes it as a source.
        foreach (var (key, candidates) in candidatesByKey)
        {
            // Exactly one source. Two that verify mean the same bytes could
            // belong to a deleted-and-reinserted row, or to another row: no seed.
            if (candidates.Count != 1 || (candidates[0].DeleteLsn is { } d && chainsPerDelete[d] > 1))
                continue;

            seeds[key] = new RowImageSeed(candidates[0].Image, chains[key][0].Lsn);
        }

        return seeds;
    }

    /// <summary>
    /// Whether this UPDATE may have changed the row's length (so moved the
    /// bytes behind its change). LOP_MODIFY_ROW replaces a span: the length
    /// changed if the old and new spans differ in size. LOP_MODIFY_COLUMNS is
    /// a list of edits; one whose offset differs between the old and new row
    /// shows an earlier edit changed the length, and its last edit's new
    /// length is not stored, so it is only taken as length-neutral when every
    /// offset pair is equal and each old block is the same padded size as the
    /// new block.
    /// </summary>
    private static bool ChangesRowLayout(LogRecord record)
    {
        if (record.Operation == "LOP_MODIFY_ROW")
            return record.RowLogContents0 is not { } o || record.RowLogContents1 is not { } n || o.Length != n.Length;

        if (record.RowLogContents0 is not { } r0 || record.RowLogContents1 is not { } r1 || record.RowLogContents4 is not { } r4)
            return true;
        int edits = r1.Length / 2;
        if (r0.Length < edits * 4) return true;
        int paddedOld = 0;
        for (int i = 0; i < edits; i++)
        {
            if (BitConverter.ToUInt16(r0, i * 4) != BitConverter.ToUInt16(r0, i * 4 + 2)) return true;
            int oldLength = BitConverter.ToUInt16(r1, i * 2);
            paddedOld += (oldLength + 3) / 4 * 4;
        }
        return r4.Length != paddedOld * 2;
    }

    /// <summary>
    /// Every byte this chain changed lies before the end of the row's
    /// fixed-length data (the uint16 at byte 2 of the row), where no change of
    /// a variable-length column can move it.
    /// </summary>
    private static bool ChainStaysInFixedRegion(IReadOnlyList<LogRecord> chain, byte[] row)
    {
        if (row.Length < 4) return false;
        int fixedEnd = BitConverter.ToUInt16(row, 2);
        foreach (var record in chain)
        {
            if (record.Operation == "LOP_MODIFY_ROW")
            {
                if (record.OffsetInRow is not int offset || record.RowLogContents1 is not { } n ||
                    record.RowLogContents0 is not { } o || o.Length != n.Length || offset < 0 || offset + n.Length > fixedEnd)
                    return false;
            }
            else
            {
                if (record.RowLogContents0 is not { } r0 || record.RowLogContents1 is not { } r1) return false;
                int edits = r1.Length / 2;
                if (r0.Length < edits * 4) return false;
                for (int i = 0; i < edits; i++)
                {
                    int oldOffset = BitConverter.ToUInt16(r0, i * 4);
                    int newOffset = BitConverter.ToUInt16(r0, i * 4 + 2);
                    int oldLength = BitConverter.ToUInt16(r1, i * 2);
                    if (oldOffset != newOffset || oldOffset + oldLength > fixedEnd) return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The row to anchor on within a page: by key hash when the chain has one,
    /// otherwise - heaps only - the exact slot, and only if nothing was
    /// inserted at or deleted from that slot since the last logged change.
    /// </summary>
    private static byte[]? PickRow(
        IReadOnlyList<PageRow> rows, LogRecord last, string? hash,
        Func<string, int, bool, string, string, bool> slotMayHaveChanged)
    {
        var present = rows.Where(r => r.RecordType is "PRIMARY_RECORD" or "FORWARDED_RECORD").ToList();

        if (hash is not null)
        {
            var byHash = present.Where(r => r.KeyHash == hash).ToList();
            return byHash.Count == 1 ? byHash[0].Bytes : null; // 0 = not here, >1 = hash collision
        }

        if (last.Context != "LCX_HEAP")
            return null; // a clustered row with no hash has no identity on the page

        // The upper bound is "now": everything after the change counts.
        if (slotMayHaveChanged(last.PageId!, last.SlotId!.Value, false, last.Lsn, "\uffff"))
            return null;

        return present.FirstOrDefault(r => r.SlotId == last.SlotId)?.Bytes;
    }

    /// <summary>
    /// The row as it was before every record of <paramref name="chain"/>, given
    /// its image after the last one; null if any step fails to verify.
    /// </summary>
    public static byte[]? UndoChain(byte[] imageAfterLast, IReadOnlyList<LogRecord> chain)
    {
        byte[] row = imageAfterLast;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var record = chain[i];
            byte[]? before = record.Operation == "LOP_MODIFY_COLUMNS"
                ? UndoModifyColumns(row, record)
                : UndoModifyRow(row, record);
            if (before is null)
                return null;
            row = before;
        }
        return row;
    }

    private static byte[]? UndoModifyRow(byte[] after, LogRecord record)
    {
        if (record.OffsetInRow is not int offset ||
            record.RowLogContents0 is not { } oldBytes ||
            record.RowLogContents1 is not { } newBytes)
            return null;

        if (offset < 0 || offset + newBytes.Length > after.Length ||
            !after.AsSpan(offset, newBytes.Length).SequenceEqual(newBytes))
            return null;

        var before = RowPatcher.ApplyBackward(after, offset, oldBytes, newBytes);
        return ModifyColumnsPatcher.IsSelfConsistent(before) ? before : null;
    }

    private static byte[]? UndoModifyColumns(byte[] after, LogRecord record)
    {
        if (record.RowLogContents0 is not { } r0 || record.RowLogContents1 is not { } r1 || record.RowLogContents4 is not { } r4)
            return null;

        return ModifyColumnsPatcher.TryApplyBackward(after, r0, r1, r4, out var before, out _) ? before : null;
    }
}

/// <summary>Reads data pages and row locations from a live SQL Server.</summary>
public sealed class SqlAnchorSource(SqlConnection connection, string tableName) : IAnchorSource
{
    private const int BatchSize = 500;

    public async Task<IReadOnlyList<PageRow>?> ReadPageAsync(string pageId, CancellationToken ct)
    {
        try
        {
            return await DataPageReader.ReadRowsAsync(connection, pageId, ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidCastException or InvalidOperationException or FormatException)
        {
            // Not readable (permissions, deallocated page, unexpected output):
            // nothing can be anchored from it.
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> LocateByKeyHashAsync(IReadOnlyList<string> hashes, CancellationToken ct)
    {
        var found = new Dictionary<string, string>();
        var ambiguous = new HashSet<string>();
        if (hashes.Count == 0)
            return found;

        try
        {
            string? qualified;
            await using (var resolve = new SqlCommand(
                "SELECT QUOTENAME(OBJECT_SCHEMA_NAME(OBJECT_ID(@t))) + N'.' + QUOTENAME(OBJECT_NAME(OBJECT_ID(@t)))", connection))
            {
                resolve.Parameters.AddWithValue("@t", tableName);
                qualified = await resolve.ExecuteScalarAsync(ct) as string;
            }
            if (qualified is null)
                return found;

            for (int start = 0; start < hashes.Count; start += BatchSize)
            {
                var batch = hashes.Skip(start).Take(BatchSize)
                    .Where(h => h.Length == 12 && h.All(Uri.IsHexDigit))
                    .ToList();
                if (batch.Count == 0)
                    continue;

                // The values are validated hex, so they can be written as
                // literals. INDEX(0) is the heap or clustered index itself, so
                // %%physloc%% is the row's real location, not an index entry's.
                string list = string.Join(",", batch.Select(h => $"N'({h.ToLowerInvariant()})'"));
                string sql =
                    $"SELECT CONVERT(nvarchar(40), %%lockres%%), sys.fn_PhysLocFormatter(%%physloc%%) FROM {qualified} WITH (NOLOCK, INDEX(0)) " +
                    $"WHERE %%lockres%% IN ({list})";

                await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string hash = reader.GetString(0).Trim('(', ')').ToUpperInvariant();
                    string? page = ToPageId(reader.GetString(1));
                    if (page is null)
                        continue;
                    if (found.ContainsKey(hash) || ambiguous.Contains(hash))
                    {
                        found.Remove(hash);
                        ambiguous.Add(hash); // two rows share it: do not guess
                    }
                    else
                    {
                        found[hash] = page;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidCastException or InvalidOperationException)
        {
            return new Dictionary<string, string>();
        }

        return found;
    }

    /// <summary>"(file:page:slot)" to fn_dblog's "FFFF:PPPPPPPP" (hex).</summary>
    private static string? ToPageId(string physLoc)
    {
        var parts = physLoc.Trim('(', ')').Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int file) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page))
            return null;
        return $"{file:X4}:{page:X8}";
    }
}
