using System.Globalization;
using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Recovers the starting image of rows whose INSERT is not in the log, so that
/// their UPDATEs - by far the common case for any row that is not brand new -
/// can still be decoded.
///
/// An UPDATE record only holds the bytes that changed, so it cannot be decoded
/// on its own; the reconstructor needs the row as it was before. When the
/// row's INSERT (or an earlier UPDATE) has already left the log, that image is
/// not there. But the row's image AFTER its last logged change is available:
///   - from the data page, if the row still exists (found by its key hash,
///     on the page it was last logged on or - if a page split moved it - on
///     whichever page the table says it is on now), or
///   - from the log itself, as the image of a later LOP_DELETE_ROWS.
/// Undoing the row's logged changes from that image, newest first, gives the
/// image before the first one.
///
/// Nothing here is taken on trust. Undoing a change requires that the bytes
/// the record says it wrote are really there; every step is checked, a row
/// that does not verify gets no seed (its UPDATE is then reported as "before
/// image unknown", as before), and a deleted row is only matched to a DELETE
/// image when exactly one candidate verifies.
/// </summary>
public static class RowImageAnchoring
{
    private const int HashLookupBatchSize = 500;

    public static async Task<IReadOnlyDictionary<string, RowImageSeed>> BuildSeedsAsync(
        SqlConnection connection, string tableName, IReadOnlyList<LogRecord> recordsInLsnOrder,
        CancellationToken ct = default)
    {
        var seeds = new Dictionary<string, RowImageSeed>();

        // Per row identity: the UPDATEs seen before any INSERT of that row.
        // A row with an INSERT in the window is already fully reconstructable.
        var chains = new Dictionary<string, List<LogRecord>>();
        var insertedKeys = new HashSet<string>();
        var deletes = new List<LogRecord>();

        // The same row-tracking rules the reconstructor uses, so a chain here
        // is exactly the set of records the reconstructor will see without an
        // earlier image.
        var rowKeys = new RowKeyIndex();

        foreach (var record in recordsInLsnOrder)
        {
            if (record.PageId is null || record.SlotId is null) continue;
            string key = rowKeys.Resolve(record);
            if (record.Operation is "LOP_INSERT_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS")
                rowKeys.Observe(record, key);
            else if (record.Operation == "LOP_DELETE_ROWS")
                rowKeys.ForgetSlot(record);
            switch (record.Operation)
            {
                case "LOP_INSERT_ROWS":
                    insertedKeys.Add(key);
                    break;
                case "LOP_DELETE_ROWS" when record.RowLogContents0 is { Length: > 0 }:
                    deletes.Add(record);
                    break;

                // Heap forwarding. A row that is turned into a pointer is
                // logged with its complete image up to that point, so that
                // image is the anchor for everything logged on it before.
                case "LOP_MODIFY_ROW" when !insertedKeys.Contains(key) &&
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
                    insertedKeys.Add(key);
                    break;

                // The pointer is turned back into the row: its image is known
                // from here on (the reconstructor takes it from the record).
                case "LOP_MODIFY_ROW" when record.OffsetInRow == 0 &&
                                           record.RowLogContents0 is { } oldPointer &&
                                           record.RowLogContents1 is { } rowBack &&
                                           RowFormat.IsForwardingStub(oldPointer) && RowFormat.IsPrimaryRecord(rowBack):
                    chains.Remove(key);
                    insertedKeys.Add(key);
                    break;

                case "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" when !insertedKeys.Contains(key):
                    if (!chains.TryGetValue(key, out var chain))
                        chains[key] = chain = [];
                    chain.Add(record);
                    break;
            }
        }

        if (chains.Count == 0)
            return seeds;

        var pageCache = new Dictionary<string, IReadOnlyList<PageRow>>();
        var unanchored = new List<string>();

        // 1. The row still exists on the page it was last logged on.
        foreach (var (key, chain) in chains)
        {
            ct.ThrowIfCancellationRequested();
            var last = chain[^1];
            var live = await FindOnPageAsync(connection, pageCache, last.PageId!, last, ct);
            if (live is not null)
            {
                var seed = UndoChain(live, chain);
                if (seed is not null)
                    seeds[key] = new RowImageSeed(seed, chain[0].Lsn);
            }
            else
            {
                unanchored.Add(key);
            }
        }

        // 2. A page split (or similar) moved it: ask the table where the key
        //    hash is now. One scan answers for all of them.
        var stillMissing = new List<string>();
        var keyHashes = unanchored
            .Select(k => (Key: k, Hash: RowIdentity.KeyHash(chains[k][^1].RowLogContents3)))
            .ToList();
        var locations = await LocateByKeyHashAsync(
            connection, tableName, keyHashes.Where(x => x.Hash is not null).Select(x => x.Hash!).Distinct().ToList(), ct);

        foreach (var (key, hash) in keyHashes)
        {
            var chain = chains[key];
            if (hash is not null && locations.TryGetValue(hash, out var pageId))
            {
                var live = await FindOnPageAsync(connection, pageCache, pageId, chain[^1], ct);
                if (live is not null)
                {
                    var seed = UndoChain(live, chain);
                    if (seed is not null)
                        seeds[key] = new RowImageSeed(seed, chain[0].Lsn);
                    continue;
                }
            }
            stillMissing.Add(key);
        }

        // 3. Otherwise it was deleted: one DELETE image must verify. The
        //    delete is looked for where the row was last seen first (same
        //    page and slot, then same page, then anywhere later in the log),
        //    and only an unambiguous match at the nearest level that has one
        //    is used.
        var usedDeletes = new HashSet<string>();
        foreach (var key in stillMissing)
        {
            var chain = chains[key];
            var last = chain[^1];
            var later = deletes
                .Where(d => string.CompareOrdinal(d.Lsn, last.Lsn) > 0 && !usedDeletes.Contains(d.Lsn))
                .ToList();
            var tiers = new[]
            {
                later.Where(d => d.PageId == last.PageId && d.SlotId == last.SlotId).ToList(),
                later.Where(d => d.PageId == last.PageId).ToList(),
                later,
            };

            foreach (var tier in tiers)
            {
                var verified = new List<(string Lsn, byte[] Image)>();
                foreach (var delete in tier)
                {
                    var candidate = UndoChain(delete.RowLogContents0!, chain);
                    if (candidate is not null)
                        verified.Add((delete.Lsn, candidate));
                }
                if (verified.Count == 0)
                    continue;
                if (verified.Count == 1)
                {
                    seeds[key] = new RowImageSeed(verified[0].Image, chain[0].Lsn);
                    usedDeletes.Add(verified[0].Lsn);
                }
                break;
            }
        }

        return seeds;
    }

    private static async Task<byte[]?> FindOnPageAsync(
        SqlConnection connection, Dictionary<string, IReadOnlyList<PageRow>> pageCache,
        string pageId, LogRecord last, CancellationToken ct)
    {
        IReadOnlyList<PageRow> rows;
        try
        {
            if (!pageCache.TryGetValue(pageId, out rows!))
                pageCache[pageId] = rows = await DataPageReader.ReadRowsAsync(connection, pageId, ct);
        }
        catch (SqlException)
        {
            // Page not readable (permissions, deallocated page): no live row
            // can be anchored from it.
            return null;
        }

        var primary = rows.Where(r => r.RecordType is "PRIMARY_RECORD" or "FORWARDED_RECORD").ToList();
        string? hash = RowIdentity.KeyHash(last.RowLogContents3);
        if (hash is not null)
        {
            var byHash = primary.Where(r => r.KeyHash == hash).ToList();
            return byHash.Count == 1 ? byHash[0].Bytes : null; // 0 = not here, >1 = hash collision
        }

        // Heap: row ids do not move, so the logged slot is the row - but only
        // on the page it was logged on.
        return pageId == last.PageId ? primary.FirstOrDefault(r => r.SlotId == last.SlotId)?.Bytes : null;
    }

    /// <summary>
    /// Key hash (upper-case hex) to the page currently holding that row, for
    /// the hashes that identify exactly one row.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> LocateByKeyHashAsync(
        SqlConnection connection, string tableName, IReadOnlyList<string> hashes, CancellationToken ct)
    {
        var found = new Dictionary<string, string>();
        var ambiguous = new HashSet<string>();
        if (hashes.Count == 0)
            return found;

        string? qualified;
        await using (var resolve = new SqlCommand(
            "SELECT QUOTENAME(OBJECT_SCHEMA_NAME(OBJECT_ID(@t))) + N'.' + QUOTENAME(OBJECT_NAME(OBJECT_ID(@t)))", connection))
        {
            resolve.Parameters.AddWithValue("@t", tableName);
            qualified = await resolve.ExecuteScalarAsync(ct) as string;
        }
        if (qualified is null)
            return found;

        for (int start = 0; start < hashes.Count; start += HashLookupBatchSize)
        {
            var batch = hashes.Skip(start).Take(HashLookupBatchSize)
                .Where(h => h.Length == 12 && h.All(Uri.IsHexDigit))
                .ToList();
            if (batch.Count == 0)
                continue;

            // The values are validated hex, so they can be written as literals.
            string list = string.Join(",", batch.Select(h => $"N'({h.ToLowerInvariant()})'"));
            string sql =
                $"SELECT CONVERT(nvarchar(40), %%lockres%%), sys.fn_PhysLocFormatter(%%physloc%%) FROM {qualified} WITH (NOLOCK) " +
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
