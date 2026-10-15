using System.Globalization;
using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads each transaction's start time from its LOP_BEGIN_XACT record, so
/// row events can be filtered to an incident time window ("事故時間反查",
/// 研究紀錄 功能1). Transactions aren't scoped to one table, so this reads
/// the whole visible log, same as DdlBoundaryReader.
///
/// [Begin Time] is returned by fn_dblog as a string, not a native datetime
/// column - observed format "yyyy/MM/dd HH:mm:ss:fff" (note the colon
/// before milliseconds, not a period). This is undocumented and could
/// differ by server locale/version; a record whose time fails to parse is
/// dropped from the result rather than guessed at.
/// </summary>
public static class TransactionTimeReader
{
    private const string Sql = """
        SELECT [Transaction ID] AS TxId, [Begin Time] AS BeginTime
        FROM fn_dblog(NULL, NULL)
        WHERE [Operation] = 'LOP_BEGIN_XACT' AND [Begin Time] IS NOT NULL;
        """;

    private const string TimeFormat = "yyyy/MM/dd HH:mm:ss:fff";

    public static async Task<IReadOnlyDictionary<string, DateTime>> GetTransactionBeginTimesAsync(
        SqlConnection connection, CancellationToken ct = default)
    {
        var results = new Dictionary<string, DateTime>();
        await using var command = new SqlCommand(Sql, connection);
        // Full fn_dblog(NULL, NULL) scan, same as FnDblogReader and DdlBoundaryReader: the 30s default can time out on a large log.
        command.CommandTimeout = LogScanSettings.CommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(ct);
        int ordId = reader.GetOrdinal("TxId");
        int ordTime = reader.GetOrdinal("BeginTime");

        while (await reader.ReadAsync(ct))
        {
            string txId = reader.GetString(ordId);
            string rawTime = reader.GetString(ordTime);
            if (DateTime.TryParseExact(rawTime, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                results[txId] = parsed;
            }
        }
        return results;
    }
}
