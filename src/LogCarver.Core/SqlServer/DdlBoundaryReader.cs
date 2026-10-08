using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

public sealed record DdlBoundary(string Lsn, string? TransactionName, string TransactionId);

/// <summary>
/// Finds the LSNs of DDL transactions that changed a table's physical row
/// layout, so records written before them can be refused rather than
/// silently decoded wrong (研究紀錄 第九節 - "最危險的發現").
///
/// Correlation method: LOP_HOBT_DDL fires for both metadata-only DDL (e.g.
/// ADD COLUMN, which touches zero data pages) and physical rewrites (e.g.
/// ALTER COLUMN changing a type) alike, and its [Description] field always
/// contains "rowset &lt;hobt_id&gt;". Matching on hobt_id - not on whether the
/// same transaction also touched the table's AllocUnitName - is what makes
/// this work for metadata-only DDL too; an AllocUnitName-based join finds
/// zero boundaries for ADD COLUMN and silently defeats the guard.
///
/// hobt_id, index_id, and partitioning: sys.partitions has one row per
/// (index, partition) - the old query here had no index_id filter at all,
/// so any table with a secondary index (extremely common - not limited to
/// partitioned tables) could pick that index's own hobt_id instead of the
/// table's own heap/clustered one via ExecuteScalar's arbitrary "whichever
/// row came back first, no ORDER BY" behavior, silently matching DDL
/// against the wrong rowset and finding zero boundaries where real ones
/// exist. A partitioned table compounds this: even after filtering to
/// index_id IN (0,1) (same fix SchemaReader already applies), a table with
/// N partitions still has N distinct hobt_ids, one per partition, and a
/// schema-changing DDL only needs to touch ONE partition's hobt to be a
/// real boundary. GetDdlBoundariesAsync now collects every hobt_id for the
/// table and unions the boundaries found under each, so a DDL affecting
/// any partition is caught, not just whichever one happened to be first.
/// </summary>
public static class DdlBoundaryReader
{
    private const string HobtIdSql = """
        SELECT hobt_id FROM sys.partitions
        WHERE object_id = OBJECT_ID(@tableName) AND index_id IN (0, 1);
        """;

    private const string BoundarySql = """
        SELECT DISTINCT bx.[Current LSN] AS DdlLsn, bx.[Transaction Name] AS TxName, bx.[Transaction ID] AS TxId
        FROM fn_dblog(NULL, @commitLsn) bx
        JOIN (
            SELECT DISTINCT [Transaction ID]
            FROM fn_dblog(NULL, @commitLsn)
            WHERE [Operation] = 'LOP_HOBT_DDL' AND [Description] LIKE @rowsetPattern
        ) ddl ON ddl.[Transaction ID] = bx.[Transaction ID]
        WHERE bx.[Operation] = 'LOP_BEGIN_XACT'
        ORDER BY bx.[Current LSN];
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    /// <param name="commitLsn">
    /// Upper-bounds both scans to this LSN (typically the undo-triggering
    /// transaction's own commit LSN, already in fn_dblog's real numeric
    /// input format - see FnDblogReader's doc comment) - a DDL boundary
    /// after that point can't affect how records up to and including this
    /// transaction decode. Null means unbounded (the original behavior). No
    /// lower bound is offered - a schema change from arbitrarily far in the
    /// past can still be the boundary that makes an old record's layout
    /// different from today's, same reasoning as FnDblogReader's own doc
    /// comment.
    /// </param>
    public static async Task<IReadOnlyList<DdlBoundary>> GetDdlBoundariesAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default, string? commitLsn = null)
    {
        IReadOnlyList<long> hobtIds = await GetHobtIdsAsync(connection, tableName, ct);
        if (hobtIds.Count == 0) return [];

        // Merged by LSN, not just concatenated - a single DDL transaction
        // touching multiple partitions in one statement (e.g. an
        // ALTER TABLE ... ADD COLUMN, which is metadata-only but applies to
        // every partition at once) would otherwise show up once per
        // matching hobt_id.
        //
        // TODO (noted, not fixed - correctness over performance for now):
        // this re-scans the entire active log (BoundarySql's two
        // fn_dblog(NULL,NULL) passes) once per hobt_id, i.e. once per
        // partition for a partitioned table. Fine for the common case, but
        // scales linearly with partition count - a table with hundreds of
        // (e.g. date-)partitions would do hundreds of full scans. A single-
        // scan version is possible: pull every LOP_HOBT_DDL row once,
        // parse "rowset <id>." out of Description client-side, filter
        // against a HashSet of hobtIds, then do one LOP_BEGIN_XACT join on
        // the surviving transaction IDs.
        var byLsn = new Dictionary<string, DdlBoundary>();
        foreach (long hobtId in hobtIds)
        {
            await using var command = new SqlCommand(BoundarySql, connection);
            // Same reasoning as FnDblogReader's CommandTimeout bump - this
            // query does two fn_dblog scans per hobt_id, unbounded unless
            // commitLsn is given.
            command.CommandTimeout = 120;
            command.Parameters.AddWithValue("@rowsetPattern", $"%rowset {hobtId}.%");
            command.Parameters.AddWithValue("@commitLsn", (object?)commitLsn ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(ct);
            int ordLsn = reader.GetOrdinal("DdlLsn");
            int ordName = reader.GetOrdinal("TxName");
            int ordId = reader.GetOrdinal("TxId");
            while (await reader.ReadAsync(ct))
            {
                string lsn = reader.GetString(ordLsn);
                byLsn[lsn] = new DdlBoundary(
                    Lsn: lsn,
                    TransactionName: reader.IsDBNull(ordName) ? null : reader.GetString(ordName),
                    TransactionId: reader.GetString(ordId));
            }
        }
        return byLsn.Values.OrderBy(b => b.Lsn, StringComparer.Ordinal).ToList();
    }

    private static async Task<IReadOnlyList<long>> GetHobtIdsAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = new SqlCommand(HobtIdSql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);

        var hobtIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hobtIds.Add(reader.GetInt64(0));
        return hobtIds;
    }
}
