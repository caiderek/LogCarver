namespace LogCarver.Core.SqlServer;

/// <summary>
/// Command timeout, in seconds, for the queries that scan the whole
/// fn_dblog. Default 120. On a database whose active log holds tens of
/// millions of records one scan can take minutes, so the CLI exposes this as
/// --timeout. 0 means no limit (SqlCommand semantics).
/// </summary>
public static class LogScanSettings
{
    public const int DefaultCommandTimeoutSeconds = 120;

    public static int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;
}
