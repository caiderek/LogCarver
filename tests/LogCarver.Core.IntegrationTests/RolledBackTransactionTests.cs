using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

/// <summary>
/// Found for real 2026-10-18: after BEGIN TRAN; UPDATE; ROLLBACK on a row, the next same-length
/// UPDATE of that row was decoded against a row image one byte too long and produced an undo SQL
/// with a garbled value (N'other-db-val〱'), with no error. SQL Server's compensation record for
/// the rollback carries no old bytes, which the splice read as "insert one byte"
/// (see CompensationNormalizer).
/// </summary>
[Collection("SqlServer")]
public class RolledBackTransactionTests(SqlServerFixture fixture)
{
    private const string Table = "dbo.RolledBackRowTest";

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task TheNextUpdateOfARowAfterARolledBackTransaction_IsDecodedCorrectly()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, $"DROP TABLE IF EXISTS {Table}; CREATE TABLE {Table} (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL);");
        try
        {
            await ExecAsync(connection, $"INSERT INTO {Table} VALUES (1, N'seed-initial');");
            await ExecAsync(connection, $"UPDATE {Table} SET Name = N'pcross-v0' WHERE Id = 1;");
            await ExecAsync(connection, $"BEGIN TRAN; UPDATE {Table} SET Name = N'pcross-v1' WHERE Id = 1; ROLLBACK;");
            await ExecAsync(connection, $"UPDATE {Table} SET Name = N'other-db-val1' WHERE Id = 1;"); // the row grows
            await ExecAsync(connection, $"UPDATE {Table} SET Name = N'other-db-val2' WHERE Id = 1;"); // same length

            var schema = await SchemaReader.GetTableSchemaAsync(connection, Table);
            var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, Table);
            var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns: []);

            // The rolled-back change is not an event; every other change decodes to what really happened.
            var names = history.Where(e => e.Kind == RowEventKind.Update).Select(e => (string?)e.After!["Name"]).ToList();
            Assert.Equal(["pcross-v0", "other-db-val1", "other-db-val2"], names);

            var last = history[^1];
            Assert.Null(last.Note);
            Assert.Equal("other-db-val1", last.Before!["Name"]);
            Assert.Equal("other-db-val2", last.After!["Name"]);
        }
        finally
        {
            await ExecAsync(connection, $"DROP TABLE IF EXISTS {Table};");
        }
    }
}
