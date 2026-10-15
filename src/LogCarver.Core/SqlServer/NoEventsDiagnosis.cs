using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// What the online log looks like right now, read only to explain a
/// "0 row events" result. Zero events has several different causes (log
/// already rotated, wrong database, wrong table name, TRUNCATE/DROP, other
/// database) that the CLI used to collapse into one "VLF rotated" message.
/// </summary>
public sealed record LogOverview(
    string DatabaseName,
    string? RecoveryModel,
    string? LogReuseWait,
    bool IsSysadmin,
    IReadOnlyList<(string AllocUnitName, int Rows)> TopDeleteTables);

public static class NoEventsDiagnosis
{
    private const string OverviewSql = """
        SELECT DB_NAME() AS DbName,
               (SELECT recovery_model_desc FROM sys.databases WHERE name = DB_NAME()) AS Recovery,
               (SELECT log_reuse_wait_desc FROM sys.databases WHERE name = DB_NAME()) AS ReuseWait,
               IS_SRVROLEMEMBER('sysadmin') AS IsSa;
        """;

    private const string TopDeletesSql = """
        SELECT TOP 5 [AllocUnitName] AS Unit, COUNT(*) AS Rows
        FROM fn_dblog(NULL, NULL)
        WHERE [Operation] = 'LOP_DELETE_ROWS' AND [AllocUnitName] IS NOT NULL
          AND [AllocUnitName] NOT LIKE 'sys.%'
        GROUP BY [AllocUnitName]
        ORDER BY COUNT(*) DESC;
        """;

    public static async Task<LogOverview> ReadOverviewAsync(SqlConnection connection, CancellationToken ct = default)
    {
        string db = "";
        string? recovery = null, reuse = null;
        bool isSa = false;
        await using (var cmd = new SqlCommand(OverviewSql, connection))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                db = r.IsDBNull(0) ? "" : r.GetString(0);
                recovery = r.IsDBNull(1) ? null : r.GetString(1);
                reuse = r.IsDBNull(2) ? null : r.GetString(2);
                isSa = !r.IsDBNull(3) && r.GetInt32(3) == 1;
            }
        }

        var top = new List<(string, int)>();
        await using (var cmd = new SqlCommand(TopDeletesSql, connection))
        {
            cmd.CommandTimeout = LogScanSettings.CommandTimeoutSeconds;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                top.Add((r.GetString(0), r.GetInt32(1)));
        }

        return new LogOverview(db, recovery, reuse, isSa, top);
    }

    /// <summary>
    /// Plain-text explanation lines. Pure function so it can be unit tested
    /// without a SQL Server. <paramref name="oldest"/>/<paramref name="newest"/>
    /// are the earliest/latest LOP_BEGIN_XACT times currently in the log
    /// (null when the log shows no transactions); <paramref name="from"/> is the
    /// user's --from, when given.
    /// </summary>
    public static IReadOnlyList<string> Explain(
        LogOverview overview, string tableName, DateTime? oldest, DateTime? newest, DateTime? from)
    {
        var lines = new List<string>();
        const string Fmt = "yyyy-MM-dd HH:mm:ss";

        lines.Add($"No row events for {tableName} were found in the online log of database [{overview.DatabaseName}].");
        lines.Add("What the log looks like right now:");
        lines.Add($"  recovery model: {overview.RecoveryModel ?? "unknown"}, log reuse wait: {overview.LogReuseWait ?? "unknown"}");
        lines.Add(oldest is { } o && newest is { } n
            ? $"  the log covers transactions from {o.ToString(Fmt)} to {n.ToString(Fmt)}"
            : "  the log shows no transactions with a begin time");
        if (!overview.IsSysadmin)
            lines.Add("  this login is NOT sysadmin; fn_dblog normally needs sysadmin, so this result may be incomplete");

        if (overview.TopDeleteTables.Count > 0)
        {
            lines.Add("  tables that still have deleted rows in this log (most first):");
            foreach (var (unit, rows) in overview.TopDeleteTables)
                lines.Add($"    {unit}: {rows} deleted row(s)");
        }
        else
        {
            lines.Add("  no deleted rows for any table are in this log");
        }

        lines.Add("Most likely reason:");
        if (from is { } f && oldest is { } oldestTime && oldestTime > f)
        {
            lines.Add($"  The log starts at {oldestTime.ToString(Fmt)}, after the time you gave with --from ({f.ToString(Fmt)}).");
            lines.Add("  Anything that happened before that has already been reused, so 0 rows is the correct answer for this log.");
        }
        else if (string.Equals(overview.RecoveryModel, "SIMPLE", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("  The database is in SIMPLE recovery: the log is truncated at every checkpoint, so older deletes are usually gone.");
        }
        else if (overview.TopDeleteTables.Count > 0)
        {
            lines.Add("  The log still holds deletes, but not for this table. Check the database name and the schema.table spelling,");
            lines.Add("  and whether the rows were removed by TRUNCATE TABLE or DROP TABLE (those are not logged row by row), or in another database.");
        }
        else
        {
            lines.Add("  Nothing was deleted row by row in this log. The log may have been reused, or the rows were removed another way");
            lines.Add("  (TRUNCATE TABLE / DROP TABLE), or in a different database.");
        }

        return lines;
    }
}
