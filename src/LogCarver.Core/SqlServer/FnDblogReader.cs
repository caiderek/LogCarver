using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads row-level log records from fn_dblog for one table.
///
/// AllocUnitName resolution: fn_dblog's AllocUnitName for a table's own row
/// storage is "schema.table" for a heap (index_id=0, whose sys.indexes.name
/// is NULL) or "schema.table.indexname" for a clustered index (index_id=1).
/// This used to be *guessed* from the table name alone via a LIKE prefix
/// ("dbo.LogTest.%") - which works for a clustered table with no other
/// indexes, but two real bugs came from that guess, both found via CLI
/// testing against SQL Server 2019 on 2026-09-28:
///   1. A heap table's AllocUnitName has no index name to append, so it
///      never matched the "table.%" prefix pattern at all - every heap
///      table silently returned zero records.
///   2. A table (heap or clustered) with any OTHER index also has an
///      AllocUnitName of "schema.table.otherindexname" for that index's
///      own maintenance activity - which the loose "table.%" prefix (or a
///      naive "table" exact-match for heaps) also matches, pulling in that
///      index's own internal row layout (just the indexed columns, not the
///      full row) alongside the table's real data.
/// Resolving the table's actual index_id IN (0,1) name via sys.indexes
/// first and matching fn_dblog by exact equality against that one
/// constructed string eliminates both: no pattern, nothing to guess wrong.
///
/// Bracket bug (found via real customer testing, 2026-09-30): the
/// constructed string used to be built from the CALLER's own tableName
/// argument (e.g. "[LOG].[JobRun]", exactly how someone typing a
/// schema-qualified name naturally would, especially copy-pasted from
/// SSMS's own "Script Table as" output) - but fn_dblog's AllocUnitName is
/// never bracket-quoted ("LOG.JobRun.PK_LOG_JobRun"). Comparing
/// "[LOG].[JobRun].PK_LOG_JobRun" against that never matches, silently
/// returning zero rows for every real event - the exact wrong-diagnosis
/// failure mode this tool exists to avoid (it printed "fn_dblog found
/// nothing... may have rotated past" and pointed at LogCarverOffline,
/// when the data was sitting right there in the still-active log).
/// OBJECT_ID(@tableName) itself tolerates brackets/quoting/case fine, so
/// schema/DDL/compression lookups elsewhere never showed a symptom - only
/// this string-concatenation site did. Fixed by asking SQL Server for the
/// canonical, always-unbracketed schema/table name via
/// OBJECT_SCHEMA_NAME/OBJECT_NAME instead of reusing the caller's raw
/// input text, so any valid way of typing the same object (bracketed,
/// quoted, mixed case) resolves to the one string fn_dblog actually uses.
///
/// Context filter: a clustered-index table's INSERT/UPDATE use
/// Context='LCX_CLUSTERED', but DELETE uses 'LCX_MARK_AS_GHOST' (SQL
/// Server marks deleted rows as ghosts in-place before background cleanup
/// expunges them) - both must be included or every DELETE silently
/// disappears from the results. A heap table uses a third context,
/// 'LCX_HEAP', for INSERT/UPDATE/DELETE alike.
///
/// Scope note: only reads the live/active portion of the log that
/// fn_dblog exposes. Recovering data from VLFs that are reusable but not
/// yet overwritten requires reading the LDF file directly and is not
/// implemented here.
///
/// LSN upper-bounding (added 2026-10-08, see LogCarverGuard's own status
/// notes for the full incident): a real deployment hit 30+ minutes and
/// multi-GB memory for a single UPDATE's undo generation because every
/// fn_dblog call here scanned the database's ENTIRE active log, not just
/// the range relevant to the one transaction being undone - fn_dblog's
/// second positional parameter is a perfectly real end-LSN bound, it was
/// simply never passed. <paramref name="commitLsn"/> lets a caller supply
/// "don't read past this transaction's own commit" - safe because nothing
/// at or after a transaction's commit LSN can be part of ITS before/after
/// image. Deliberately NOT given a lower bound too: an UPDATE's
/// before-image requires replaying every prior write to the same
/// page/slot, which can be arbitrarily far back in the log - narrowing
/// the start would risk silently wrong (not just missing) before-images.
/// Left null (the default), behavior is unchanged from before this
/// parameter existed - a full fn_dblog(NULL, NULL) scan.
///
/// IMPORTANT - the string fn_dblog itself prints in [Current LSN]
/// ("AAAAAAAA:BBBBBBBB:CCCC", colon-hex) is NOT what its own start/end
/// parameters accept, despite that being the format every public example
/// (blog posts, even a Qlik support article) shows. Passing that format
/// back in throws SqlException 9005 ("Invalid parameter passed to
/// OpenRowset(DBLog, ...)") on every SQL Server version tried, including
/// 2025 - confirmed empirically 2026-10-08 after a first attempt at this
/// exact feature was built, fully tested, and reverted believing fn_dblog
/// had stopped accepting bounded calls entirely. It hadn't; the format was
/// wrong. The real accepted input format (reverse-engineered from
/// msdb.dbo.backupset's LSN columns, confirmed via a real begin/commit/
/// scan round-trip) is a single decimal string: VLF sequence number
/// (decimal, no padding) + log block offset (decimal, zero-padded to 10
/// digits) + slot number (decimal, zero-padded to 5 digits), e.g.
/// "8953000002721600001". Callers must pass <paramref name="commitLsn"/>
/// already converted to this format - see
/// GuardedSqlExecutor.ConvertHexLsnToNumericFormat, the one place that
/// conversion happens (right where a hex LSN is first captured from
/// fn_dblog's own output).
/// </summary>
public static class FnDblogReader
{
    private const string OwnIndexNameSql = """
        SELECT OBJECT_SCHEMA_NAME(i.object_id) AS SchemaName, OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(@tableName) AND i.index_id IN (0, 1);
        """;

    private const string Sql = """
        SELECT [Current LSN] AS Lsn, [Operation] AS Operation, [Context] AS Context,
               [Offset in Row] AS OffsetInRow, [AllocUnitName] AS AllocUnitName,
               [Page ID] AS PageId, [Slot ID] AS SlotId, [Transaction ID] AS TransactionId,
               [RowLog Contents 0] AS Rlc0, [RowLog Contents 1] AS Rlc1
        FROM fn_dblog(NULL, @commitLsn)
        WHERE [AllocUnitName] = @allocUnitName
          AND [Context] IN ('LCX_CLUSTERED', 'LCX_MARK_AS_GHOST', 'LCX_HEAP')
        ORDER BY [Current LSN];
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    /// <param name="commitLsn">
    /// Upper-bounds the scan to records at or before this LSN (typically the
    /// undo-triggering transaction's own commit LSN), already converted to
    /// fn_dblog's real numeric input format - see this class's own doc
    /// comment. Null means unbounded - the original fn_dblog(NULL, NULL)
    /// behavior.
    /// </param>
    public static async Task<IReadOnlyList<LogRecord>> ReadClusteredRecordsAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default, string? commitLsn = null)
    {
        string? allocUnitName = await ResolveOwnAllocUnitNameAsync(connection, tableName, ct);
        if (allocUnitName is null)
            return [];

        await using var command = new SqlCommand(Sql, connection);
        // fn_dblog(NULL, NULL) scans the whole active log, not just this
        // table's rows - on a database with a large active log this can
        // genuinely take well over the ADO.NET default 30s (confirmed
        // 2026-10-04 stress-testing LogCarverGuard's native install: a
        // 5-table FK cascade's undo generation took 1m46s total across
        // several of these scans). 30s was timing this out even when the
        // scan would have completed in well under two minutes - raised so
        // a genuinely slow-but-finishable scan doesn't get timeout the same
        // as a truly stuck one. Passing commitLsn (see this method's own
        // doc comment) shrinks the scanned range in the first place, so
        // this timeout is now a backstop for the unbounded/legacy case,
        // not the expected normal cost.
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("@allocUnitName", allocUnitName);
        command.Parameters.AddWithValue("@commitLsn", (object?)commitLsn ?? DBNull.Value);

        var results = new List<LogRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        int ordLsn = reader.GetOrdinal("Lsn");
        int ordOp = reader.GetOrdinal("Operation");
        int ordCtx = reader.GetOrdinal("Context");
        int ordOffset = reader.GetOrdinal("OffsetInRow");
        int ordAlloc = reader.GetOrdinal("AllocUnitName");
        int ordPage = reader.GetOrdinal("PageId");
        int ordSlot = reader.GetOrdinal("SlotId");
        int ordTxId = reader.GetOrdinal("TransactionId");
        int ordRlc0 = reader.GetOrdinal("Rlc0");
        int ordRlc1 = reader.GetOrdinal("Rlc1");

        while (await reader.ReadAsync(ct))
        {
            results.Add(new LogRecord(
                Lsn: reader.GetString(ordLsn),
                Operation: reader.GetString(ordOp),
                Context: reader.GetString(ordCtx),
                OffsetInRow: reader.IsDBNull(ordOffset) ? null : reader.GetInt16(ordOffset),
                AllocUnitName: reader.IsDBNull(ordAlloc) ? null : reader.GetString(ordAlloc),
                PageId: reader.IsDBNull(ordPage) ? null : reader.GetString(ordPage),
                SlotId: reader.IsDBNull(ordSlot) ? null : reader.GetInt32(ordSlot),
                TransactionId: reader.IsDBNull(ordTxId) ? null : reader.GetString(ordTxId),
                RowLogContents0: reader.IsDBNull(ordRlc0) ? null : (byte[])reader[ordRlc0],
                RowLogContents1: reader.IsDBNull(ordRlc1) ? null : (byte[])reader[ordRlc1]));
        }
        return results;
    }

    /// <summary>
    /// Builds the exact AllocUnitName fn_dblog uses for this table's own
    /// row storage (not any secondary index's), or null if the table
    /// doesn't exist. A heap's sys.indexes.name is NULL for index_id=0.
    /// Built from SQL Server's own canonical schema/table name (see this
    /// class's doc comment's "Bracket bug" note), never from the caller's
    /// raw tableName text - OBJECT_ID(@tableName) is the only place that
    /// text is used, and it tolerates any valid way of naming the object.
    /// </summary>
    private static async Task<string?> ResolveOwnAllocUnitNameAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = new SqlCommand(OwnIndexNameSql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        string qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
        return reader.IsDBNull(2) ? qualifiedName : $"{qualifiedName}.{reader.GetString(2)}";
    }
}
