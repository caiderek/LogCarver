using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;

const string OfflineWaitlistContact = "logcarveroffline@gmail.com";
const string OfflinePurchaseUrl = "https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1";
const string OfflineTrialUrl = "https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW";

if (args.Length == 0 || args[0] is "-h" or "--help" or "-?")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

if (args.Length < 3)
{
    Console.WriteLine("Missing required arguments.");
    Console.WriteLine();
    PrintUsage();
    return 1;
}

string server = args[0];
string database = args[1];
string tableName = args[2];

if (!TryParseOptions(args, out var options, out var parseError))
{
    Console.WriteLine(parseError);
    Console.WriteLine();
    PrintUsage();
    return 1;
}

try
{
    return await RunAsync(server, database, tableName, options);
}
catch (SqlException ex)
{
    Console.WriteLine($"Could not connect to or query SQL Server '{server}': {ex.Message}");
    Console.WriteLine("Check that the server name is correct, the instance is running and reachable, and your account has access to the database.");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
    return 1;
}

static bool TryParseOptions(string[] args, out CliOptions options, out string error)
{
    options = new CliOptions();
    error = "";

    int i = 3;
    while (i < args.Length)
    {
        string flag = args[i];

        if (flag == "--undo")
        {
            options.ShowUndoSql = true;
            i += 1;
            continue;
        }

        if (flag == "--replay")
        {
            options.ShowReplaySql = true;
            i += 1;
            continue;
        }

        if (i + 1 >= args.Length)
        {
            error = $"Missing value for '{flag}'.";
            return false;
        }
        string value = args[i + 1];
        i += 2;

        switch (flag)
        {
            case "--from":
                if (!DateTime.TryParse(value, out var from))
                {
                    error = $"Could not parse --from value '{value}' as a date/time. Try a format like \"2026-09-23T09:00\".";
                    return false;
                }
                options.From = from;
                break;

            case "--to":
                if (!DateTime.TryParse(value, out var to))
                {
                    error = $"Could not parse --to value '{value}' as a date/time. Try a format like \"2026-09-23T09:00\".";
                    return false;
                }
                options.To = to;
                break;

            case "--key":
                int eq = value.IndexOf('=');
                if (eq <= 0)
                {
                    error = $"Could not parse --key value '{value}'. Expected 'ColumnName=Value', e.g. --key Id=5.";
                    return false;
                }
                options.KeyColumn = value[..eq];
                options.KeyValue = value[(eq + 1)..];
                break;

            case "--snapshot":
                if (!DateTime.TryParse(value, out var asOf))
                {
                    error = $"Could not parse --snapshot value '{value}' as a date/time. Try a format like \"2026-09-23T09:00\".";
                    return false;
                }
                options.SnapshotAsOf = asOf;
                break;

            case "--user":
                options.SqlUser = value;
                break;

            case "--password":
                options.SqlPassword = value;
                break;

            case "--export":
                if (value is not ("csv" or "json"))
                {
                    error = $"Could not parse --export value '{value}'. Expected 'csv' or 'json'.";
                    return false;
                }
                options.ExportFormat = value;
                break;

            case "--output":
                options.OutputPath = value;
                break;

            default:
                error = $"Unknown option '{flag}'.";
                return false;
        }
    }

    if (options.SqlUser is not null != options.SqlPassword is not null)
    {
        error = "--user and --password must be used together, or not at all (omit both to use Windows integrated authentication).";
        return false;
    }
    if (options.OutputPath is not null && options.ExportFormat is null)
    {
        error = "--output requires --export csv or --export json.";
        return false;
    }
    return true;
}

static void PrintUsage()
{
    Console.WriteLine("Usage: LogCarver.Cli <server> <database> <schema.table> [--from <datetime>] [--to <datetime>] [--key <Column>=<Value>] [--undo] [--replay] [--snapshot <datetime>] [--user <name> --password <pw>] [--export csv|json [--output <path>]]");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --from \"2026-09-23T09:00\" --to \"2026-09-23T10:00\"");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --key Id=5   (only this row's full history)");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --key Id=5 --undo   (suggest SQL to reverse each event)");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --replay   (suggest forward SQL reproducing each event)");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --snapshot \"2026-09-23T09:30\"   (reconstruct table state at that moment)");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --user sa --password \"...\"   (SQL auth instead of Windows account)");
    Console.WriteLine("Example: LogCarver.Cli localhost MyDatabase dbo.Orders --export csv --output orders.csv");
    Console.WriteLine();
    Console.WriteLine("--from/--to filter which events are PRINTED to that incident window;");
    Console.WriteLine("reconstruction still uses the table's full observed history so before/after stay accurate.");
    Console.WriteLine("--key matches against either the before or after image of each event, so a row is found");
    Console.WriteLine("whether the column changed in that event or not.");
    Console.WriteLine("--undo/--replay print a suggested SQL statement reversing/reproducing each shown event.");
    Console.WriteLine("Their WHERE clauses match every observed column, not just a primary key, so they become a");
    Console.WriteLine("safe no-op if the row changed again since LogCarver saw it. Review before running - LogCarver");
    Console.WriteLine("never executes anything itself.");
    Console.WriteLine("--snapshot reconstructs what every row looked like at that exact moment, instead of listing");
    Console.WriteLine("events. It ignores --from/--to/--key/--undo/--replay.");
    Console.WriteLine("--user/--password connect with SQL authentication instead of the current Windows account -");
    Console.WriteLine("both or neither must be given. The password is visible in your shell history and this");
    Console.WriteLine("process's command line while it runs; prefer Windows authentication where you can.");
    Console.WriteLine("--export writes structured csv/json instead of the normal console listing - one column per");
    Console.WriteLine("observed table column (Before_<Col>/After_<Col>), not a single flattened cell. Applies to");
    Console.WriteLine("the event listing only, not --snapshot. --output writes to that file instead of stdout.");
    Console.WriteLine();
    Console.WriteLine("No data ever leaves this machine.");
}

static async Task<int> RunAsync(string server, string database, string tableName, CliOptions options)
{
    var connectionString = SqlConnectionFactory.BuildConnectionString(server, database, options.SqlUser, options.SqlPassword);

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    var versionCheck = await SqlServerVersion.CheckAsync(connection);
    if (!versionCheck.IsValidated)
    {
        Console.WriteLine(
            $"WARNING: SQL Server {versionCheck.ProductVersion} has not been validated. " +
            $"Decoded values may be wrong without any error. Validated versions: {string.Join(", ", SqlServerVersion.ValidatedVersions)}");
    }

    var schema = await SchemaReader.GetTableSchemaAsync(connection, tableName);
    if (schema.Count == 0)
    {
        Console.WriteLine($"No schema found for '{tableName}' in {database}. Check that it exists and that you included its schema, e.g. 'dbo.Orders' rather than just 'Orders'.");
        return 1;
    }

    var compressionTypes = await CompressionChecker.GetCompressionTypesAsync(connection, tableName);
    if (CompressionChecker.IsCompressed(compressionTypes))
    {
        Console.WriteLine(
            $"'{tableName}' uses {string.Join("/", compressionTypes.Where(t => t != "NONE"))} compression. " +
            "Compressed rows use a different physical layout that this tool does not decode yet - refusing rather than guessing.");
        return 1;
    }

    var ddlBoundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, tableName);
    var ddlBoundaryLsns = ddlBoundaries.Select(b => b.Lsn).ToList();
    if (ddlBoundaries.Count > 0)
    {
        Console.WriteLine($"Detected {ddlBoundaries.Count} schema-changing DDL boundary(ies) for {tableName}:");
        foreach (var b in ddlBoundaries)
            Console.WriteLine($"  LSN {b.Lsn}: {b.TransactionName ?? "(unnamed transaction)"}");
        Console.WriteLine("Records older than the newest boundary will be refused, not guessed.");
        Console.WriteLine();
    }

    var transactionTimes = await TransactionTimeReader.GetTransactionBeginTimesAsync(connection);
    var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, tableName);

    // Reconstruction always runs over the table's FULL observed history, not
    // just the [from,to] window or a --key filter - an UPDATE inside the
    // window (or for the requested row) still needs its "before" image,
    // which may have been written outside it. Filtering only affects what
    // gets printed below.
    // Rows whose INSERT has already left the log are anchored in their
    // current (or deleted) state and walked backwards - see RowImageAnchoring.
    var seeds = await RowImageAnchoring.BuildSeedsAsync(connection, tableName, records);
    var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns, transactionTimes, seeds);

    if (options.SnapshotAsOf is { } asOf)
    {
        var snapshot = SnapshotBuilder.BuildSnapshot(history, asOf);
        Console.WriteLine($"{snapshot.Rows.Count} row(s) in {tableName} as of {asOf:yyyy-MM-dd HH:mm:ss.fff}:");
        if (snapshot.EventsWithUnresolvedTimestampIgnored > 0)
            Console.WriteLine(
                $"Note: {snapshot.EventsWithUnresolvedTimestampIgnored} event(s) with no resolvable timestamp were ignored " +
                "and could not be placed in time - the snapshot may be missing their effect.");
        int snapshotNeedsReview = snapshot.Rows.Count(row => row.NeedsManualReview);
        if (snapshotNeedsReview > 0)
            Console.WriteLine(
                $"{snapshotNeedsReview} of {snapshot.Rows.Count} row(s) ({(double)snapshotNeedsReview / snapshot.Rows.Count:P0}) are flagged NEEDS MANUAL REVIEW below.");
        Console.WriteLine();

        foreach (var row in snapshot.Rows)
        {
            if (row.Values is null)
            {
                Console.WriteLine($"[{row.PageId}:{row.SlotId}] NEEDS MANUAL REVIEW - not shown - {row.Note}");
                continue;
            }
            Console.WriteLine($"[{row.PageId}:{row.SlotId}] {Format(row.Values)}");
        }

        return 0;
    }

    var toShow = history.Where(e =>
        (options.From is null || e.Timestamp is null || e.Timestamp >= options.From) &&
        (options.To is null || e.Timestamp is null || e.Timestamp <= options.To) &&
        (options.KeyColumn is null || RowEventFilter.MatchesKey(e, options.KeyColumn, options.KeyValue!)))
        .ToList();

    if (options.ExportFormat is not null)
    {
        // Streams directly to the destination instead of building the
        // whole export as one in-memory string first - see
        // RowEventExporter's class doc comment for why (a real customer
        // table's full history hit .NET's ~2GB single-object size ceiling
        // building one giant string, via this same code path in the
        // sibling LogCarverOffline.Cli). Writing to a real file goes
        // through WriteFileAtomically so a decode error or disk-full
        // partway through never leaves a truncated file at the requested
        // path - streaming loses the old buffer-everything approach's
        // free "nothing written until it all succeeded" property, so this
        // gets it back explicitly instead.
        void WriteTo(TextWriter writer)
        {
            if (options.ExportFormat == "csv")
                RowEventExporter.WriteCsv(writer, toShow, schema, tableName, options.ShowUndoSql, options.ShowReplaySql);
            else
                RowEventExporter.WriteJson(writer, toShow, tableName, options.ShowUndoSql, options.ShowReplaySql);
        }

        int needsReviewCount = toShow.Count(e => e.NeedsManualReview);
        string? reviewSummary = needsReviewCount > 0
            ? $"{needsReviewCount} of {toShow.Count} event(s) ({(double)needsReviewCount / toShow.Count:P0}) are flagged NeedsManualReview - " +
              "see that column/field and its Note before relying on those specific rows."
            : null;

        if (options.OutputPath is not null)
        {
            RowEventExporter.WriteFileAtomically<object?>(options.OutputPath, writer => { WriteTo(writer); return null; });
            Console.WriteLine($"Wrote {toShow.Count} row event(s) for {tableName} to {options.OutputPath}.");
            if (reviewSummary is not null) Console.WriteLine(reviewSummary);
        }
        else
        {
            WriteTo(Console.Out);
            // The export itself just went to stdout, most likely piped
            // onward (e.g. `--export csv > file.csv`) - printing the
            // summary there too would corrupt that data. stderr carries it
            // without touching the piped stream.
            if (reviewSummary is not null) Console.Error.WriteLine(reviewSummary);
        }
        return 0;
    }

    if (options.KeyColumn is not null)
    {
        Console.WriteLine($"{toShow.Count} row event(s) for {tableName} where {options.KeyColumn}={options.KeyValue}:");
        int unresolvable = history.Count(e => e.Before is null && e.After is null);
        if (unresolvable > 0)
            Console.WriteLine(
                $"Note: {unresolvable} other event(s) in this table could not be decoded (see refusals above) and were not checked against --key - " +
                "the row you're looking for may be among them, not necessarily absent.");
    }
    else if (options.From is not null || options.To is not null)
        Console.WriteLine($"{toShow.Count} of {history.Count} row event(s) for {tableName} fall in [{options.From}, {options.To}] (events with no resolvable timestamp are always shown):");
    else
        Console.WriteLine($"{toShow.Count} row event(s) for {tableName}:");

    int needsReviewInListing = toShow.Count(e => e.NeedsManualReview);
    if (needsReviewInListing > 0)
        Console.WriteLine(
            $"{needsReviewInListing} of them ({(double)needsReviewInListing / toShow.Count:P0}) are flagged NEEDS MANUAL REVIEW below - " +
            "see each one's note before relying on it.");

    if (history.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("fn_dblog found nothing for this table. This usually means the relevant VLF has already");
        Console.WriteLine("been marked reusable and rotated past - the data may still be physically present in the");
        Console.WriteLine(".ldf file even though SQL Server itself can no longer report it. LogCarverOffline reads");
        Console.WriteLine("the raw .ldf bytes directly and can often recover exactly this case.");
        Console.WriteLine($"Free trial (no license, first 10 events): {OfflineTrialUrl}");
        Console.WriteLine($"Purchase: {OfflinePurchaseUrl}");
        Console.WriteLine($"Questions: {OfflineWaitlistContact}");
    }

    Console.WriteLine();

    foreach (var e in toShow)
    {
        string label = e.Kind switch
        {
            RowEventKind.Insert => "INSERT",
            RowEventKind.Delete => "DELETE",
            RowEventKind.Update => "UPDATE",
            _ => e.Kind.ToString(),
        };
        string when = e.Timestamp is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss.fff") : "time unknown";

        if (e.Before is null && e.After is null)
        {
            Console.WriteLine($"[{e.Lsn} {when}] {label}: NEEDS MANUAL REVIEW - not shown - {e.Note}");
            continue;
        }

        string beforeText = e.Before is null ? "(none)" : Format(e.Before);
        string afterText = e.After is null ? "(none)" : Format(e.After);

        Console.WriteLine(e.Kind == RowEventKind.Update
            ? $"[{e.Lsn} {when}] {label}: {beforeText} -> {afterText}"
            : $"[{e.Lsn} {when}] {label}: {(e.Kind == RowEventKind.Insert ? afterText : beforeText)}");

        if (e.Note is not null)
            Console.WriteLine($"    NEEDS MANUAL REVIEW: {e.Note}");

        if (options.ShowUndoSql)
        {
            var undoSql = UndoSqlGenerator.Generate(e, tableName);
            if (undoSql is not null)
                Console.WriteLine($"    UNDO: {undoSql}");
        }

        if (options.ShowReplaySql)
        {
            var replaySql = ReplaySqlGenerator.Generate(e, tableName);
            if (replaySql is not null)
                Console.WriteLine($"    REPLAY: {replaySql}");
        }
    }

    return 0;
}

static string Format(IReadOnlyDictionary<string, object?> row) =>
    string.Join(", ", row.Select(kv => $"{kv.Key}={FormatValue(kv.Value)}"));

// DateTime.ToString() with no format uses CurrentCulture, which garbles
// through non-UTF8 consoles on non-en-US systems (e.g. zh-TW's 上午/下午
// AM/PM markers) and isn't copy-paste friendly. Force an invariant,
// unambiguous format instead.
static string FormatValue(object? value) => value switch
{
    null => "NULL",
    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
    _ => value.ToString() ?? "NULL",
};

sealed class CliOptions
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? KeyColumn { get; set; }
    public string? KeyValue { get; set; }
    public bool ShowUndoSql { get; set; }
    public bool ShowReplaySql { get; set; }
    public DateTime? SnapshotAsOf { get; set; }
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }
    public string? ExportFormat { get; set; }
    public string? OutputPath { get; set; }
}
