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
        CancellationToken ct = default, string? onlyTransactionId = null) =>
        BuildSeedsAsync(new SqlAnchorSource(connection, tableName), recordsInLsnOrder, ct, onlyTransactionId);

    /// <param name="onlyTransactionId">
    /// When given, only rows this transaction touched are anchored: an undo
    /// does not want a data page read for every old row anyone updated.
    /// </param>
    public static async Task<IReadOnlyDictionary<string, RowImageSeed>> BuildSeedsAsync(
        IAnchorSource source, IReadOnlyList<LogRecord> recordsInLsnOrder,
        CancellationToken ct = default, string? onlyTransactionId = null)
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
        var reinserted = new Dictionary<string, LogRecord>();
        var deletesByPage = new Dictionary<string, List<LogRecord>>();
        var churnByPage = new Dictionary<string, List<(string Lsn, int Slot, bool Clustered)>>();

        foreach (var record in recordsInLsnOrder)
        {
            if (record.PageId is null || record.SlotId is null) continue;
            string key = rowKeys.Resolve(record);
            if (record.Operation is "LOP_INSERT_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS")
                rowKeys.Observe(record, key);
            else if (record.Operation == "LOP_DELETE_ROWS")
                rowKeys.ObserveDelete(record);

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
                    if (chains.ContainsKey(key) && !known.Contains(key) && record.RowLogContents0 is { Length: > 0 })
                        reinserted.TryAdd(key, record);
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

        var pageCache = new Dictionary<string, IReadOnlyList<PageRow>?>();

        async Task<IReadOnlyList<PageRow>?> PageAsync(string pageId)
        {
            if (!pageCache.TryGetValue(pageId, out var rows))
                pageCache[pageId] = rows = await source.ReadPageAsync(pageId, ct);
            return rows;
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
        var usedDeletes = new HashSet<string>();
        foreach (var (key, chain) in chains)
        {
            var last = chain[^1];
            var candidates = new List<(string Source, byte[] Image, string? DeleteLsn)>();

            if (onLastPage.TryGetValue(key, out var live) && live is not null &&
                UndoChain(live, chain) is { } fromLive)
                candidates.Add(("live", fromLive, null));

            if (reinserted.TryGetValue(key, out var again) && again.RowLogContents0 is { } insertedImage &&
                UndoChain(insertedImage, chain) is { } fromReinsert)
                candidates.Add(("reinsert", fromReinsert, null));

            if (deletesByPage.TryGetValue(last.PageId!, out var deletes))
            {
                foreach (var delete in deletes)
                {
                    if (string.CompareOrdinal(delete.Lsn, last.Lsn) <= 0 || usedDeletes.Contains(delete.Lsn)) continue;
                    if (delete.SlotId != last.SlotId) continue;
                    if (SlotMayHaveChanged(last.PageId!, last.SlotId!.Value, last.Context != "LCX_HEAP", last.Lsn, delete.Lsn)) continue;
                    if (reinserted.TryGetValue(key, out var ins) && string.CompareOrdinal(delete.Lsn, ins.Lsn) > 0) continue;
                    if (UndoChain(delete.RowLogContents0!, chain) is { } fromDelete)
                        candidates.Add(("delete", fromDelete, delete.Lsn));
                }
            }

            // Exactly one source. Two that verify mean the same bytes could
            // belong to a deleted-and-reinserted row, or to another row: no seed.
            if (candidates.Count != 1)
                continue;

            seeds[key] = new RowImageSeed(candidates[0].Image, chain[0].Lsn);
            if (candidates[0].DeleteLsn is { } used)
                usedDeletes.Add(used);
        }

        return seeds;
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
