using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace LogCarver.Core.IntegrationTests;

/// <summary>
/// Rolled-back work against a REAL SQL Server, in every shape we can think of, each followed by
/// further changes to the same row. The oracle is the database itself: after every committed step
/// the row is read back, and the events LogCarver reconstructs must be exactly those committed
/// steps (kind, before, after), with nothing from the rolled-back work in them. An event that is
/// silently wrong is the worst outcome; an event that is merely refused or flagged also fails here,
/// because every shape in this matrix is expected to decode completely.
///
/// Why this exists: the rollback handling was first tested only with hand-written log records that
/// assumed what SQL Server writes for a rollback (2026-10-14), and the assumption was wrong -
/// see RolledBackTransactionTests and CompensationNormalizer.
/// </summary>
[Collection("SqlServer")]
public class RollbackMatrixTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    private sealed record Step(string Sql, bool Commits);

    private sealed record RowState(int Id, string? Name, int? Amount, string? Note);

    private const string Insert1 = "INSERT INTO {T} VALUES (1, N'alpha-name', 100, 'note-one');";

    public static TheoryData<string> ScenarioNames => new(Scenarios.Keys);

    private static readonly Dictionary<string, Step[]> Scenarios = new()
    {
        ["fixed-width update rolled back, then fixed-width update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Amount = 200 WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Amount = 300 WHERE Id = 1;", true),
        ],
        ["same-length varchar update rolled back, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Note = 'note-two' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Note = 'note-six' WHERE Id = 1;", true),
        ],
        ["growing nvarchar update rolled back, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Name = N'a-much-longer-name-than-before' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Name = N'beta' WHERE Id = 1;", true),
        ],
        ["shrinking nvarchar update rolled back, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Name = N'a' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Name = N'gamma-name' WHERE Id = 1;", true),
        ],
        ["multi-column update rolled back, then multi-column update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Name = N'x-name', Amount = 5, Note = 'n5' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Name = N'y-name-longer', Amount = 6, Note = 'n6' WHERE Id = 1;", true),
        ],
        ["two updates in one rolled-back transaction, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Amount = 1 WHERE Id = 1; UPDATE {T} SET Name = N'zzzzzzzzzz' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Amount = 2 WHERE Id = 1;", true),
        ],
        ["savepoint rollback keeps the earlier statement, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Amount = 111 WHERE Id = 1; SAVE TRAN s1; UPDATE {T} SET Name = N'inner-name' WHERE Id = 1; ROLLBACK TRAN s1; COMMIT;", true),
            new("UPDATE {T} SET Note = 'after-save' WHERE Id = 1;", true),
        ],
        ["delete rolled back, then update"] =
        [
            new("BEGIN TRAN; DELETE FROM {T} WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Amount = 7 WHERE Id = 1;", true),
        ],
        ["delete rolled back, then committed delete"] =
        [
            new("BEGIN TRAN; DELETE FROM {T} WHERE Id = 1; ROLLBACK;", false),
            new("DELETE FROM {T} WHERE Id = 1;", true),
        ],
        ["insert of another row rolled back, then update of the first"] =
        [
            new("BEGIN TRAN; INSERT INTO {T} VALUES (2, N'ghost', 0, 'ghost'); ROLLBACK;", false),
            new("UPDATE {T} SET Amount = 9 WHERE Id = 1;", true),
        ],
        ["update then delete in one rolled-back transaction, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Amount = 55 WHERE Id = 1; DELETE FROM {T} WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Amount = 56 WHERE Id = 1;", true),
        ],
        ["rolled back twice in a row, then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Name = N'first-try-name' WHERE Id = 1; ROLLBACK;", false),
            new("BEGIN TRAN; UPDATE {T} SET Name = N'second' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Name = N'final-name-here' WHERE Id = 1;", true),
        ],
        ["committed update, rolled back update, committed update (growing both ways)"] =
        [
            new("UPDATE {T} SET Name = N'grown-grown-grown-grown' WHERE Id = 1;", true),
            new("BEGIN TRAN; UPDATE {T} SET Name = N'tiny' WHERE Id = 1; ROLLBACK;", false),
            new("UPDATE {T} SET Name = N'mid-size-name' WHERE Id = 1;", true),
        ],
        ["statement-level failure inside a transaction (constraint), then update"] =
        [
            new("BEGIN TRAN; UPDATE {T} SET Amount = 77 WHERE Id = 1; BEGIN TRY INSERT INTO {T} VALUES (1, N'dup', 1, 'dup'); END TRY BEGIN CATCH SELECT 1 AS failed; END CATCH ROLLBACK;", false),
            new("UPDATE {T} SET Amount = 78 WHERE Id = 1;", true),
        ],
    };

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<RowState?> ReadRowAsync(SqlConnection connection, string table)
    {
        await using var command = new SqlCommand($"SELECT Id, Name, Amount, Note FROM {table} WHERE Id = 1;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new RowState(reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static RowState? ToState(IReadOnlyDictionary<string, object?>? values) =>
        values is null ? null : new RowState(
            Convert.ToInt32(values["Id"]), values["Name"] as string,
            values["Amount"] is null ? null : Convert.ToInt32(values["Amount"]), values["Note"] as string);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task EventsAreExactlyTheCommittedChanges_OrFlagged_NeverSilentlyWrong(string scenarioName)
    {
        var steps = Scenarios[scenarioName];
        string table = "dbo.RbMatrix_" + Math.Abs(scenarioName.GetHashCode() % 100000);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, $"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL, Amount INT NULL, Note VARCHAR(100) NULL);");
        try
        {
            // What the database says after every committed step is the truth we compare against.
            var expected = new List<(RowEventKind Kind, RowState? Before, RowState? After)>();
            RowState? current = null;

            await ExecAsync(connection, Insert1.Replace("{T}", table));
            var afterInsert = await ReadRowAsync(connection, table);
            expected.Add((RowEventKind.Insert, null, afterInsert));
            current = afterInsert;

            foreach (var step in steps)
            {
                await ExecAsync(connection, step.Sql.Replace("{T}", table));
                if (!step.Commits) continue;

                var now = await ReadRowAsync(connection, table);
                expected.Add((now is null ? RowEventKind.Delete : RowEventKind.Update, current, now));
                current = now;
            }

            var schema = await SchemaReader.GetTableSchemaAsync(connection, table);
            var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, table);
            var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns: []);

            var problems = new List<string>();
            var flagged = history.Where(e => e.NeedsManualReview).ToList();
            var trusted = history.Where(e => !e.NeedsManualReview).ToList();

            // Every event we would trust must be one of the committed changes, exactly.
            foreach (var e in trusted)
            {
                var got = (e.Kind, Before: ToState(e.Before), After: ToState(e.After));
                if (!expected.Any(x => x.Kind == got.Kind && Equals(x.Before, got.Before) && Equals(x.After, got.After)))
                    problems.Add($"SILENTLY WRONG {got.Kind}: before={got.Before} after={got.After}");
            }

            // Every shape in this matrix decodes completely: nothing flagged, nothing missing, nothing extra.
            // (A shape that cannot be decoded safely would be refused rather than guessed, which is why
            // the check above only fails on events that look trustworthy; here it must not even come to that.)
            foreach (var f in flagged)
                problems.Add($"flagged instead of decoded: {f.Note}");
            if (trusted.Count != expected.Count)
                problems.Add($"expected {expected.Count} events, got {trusted.Count}");

            output.WriteLine($"[{scenarioName}] expected {expected.Count} event(s); reconstructed {history.Count}: " +
                             $"{trusted.Count} trusted, {flagged.Count} flagged" +
                             (flagged.Count > 0 ? " -> " + string.Join(" | ", flagged.Select(f => f.Note!.Length > 70 ? f.Note[..70] : f.Note)) : ""));

            Assert.True(problems.Count == 0, $"{scenarioName}:\n  " + string.Join("\n  ", problems));
        }
        finally
        {
            await ExecAsync(connection, $"DROP TABLE IF EXISTS {table};");
        }
    }
}
