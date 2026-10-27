using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

/// <summary>
/// Builds a real, disposable SQL Server database with a known
/// insert/update/delete/DDL workload, so the SQL-facing readers
/// (SchemaReader, FnDblogReader, DdlBoundaryReader, TransactionTimeReader)
/// get tested against actual fn_dblog output instead of a mock that can
/// only ever be as correct as our own assumptions about undocumented
/// behavior - which is exactly what let the DELETE Context bug through
/// unnoticed for two commits.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DatabaseName = "LogCarver_IntegrationTests";
    private const string MasterConnectionString = "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;";

    public string ConnectionString { get; } =
        $"Server=localhost;Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true;";

    public const string TableName = "dbo.TestTable";

    // Known ground truth for the workload below - tests assert against these, not magic numbers.
    public const int Row1Id = 1;
    public const string Row1Note = "first-row";
    public const int Row1AmountBeforeUpdate = 100;
    public const int Row1AmountAfterUpdate = 999;
    public const int Row2Id = 2; // inserted then deleted
    public const int Row3IdPostDdl = 3;
    public const int Row3Bonus = 42;

    public DateTime WorkloadStartLocal { get; private set; }
    public DateTime WorkloadEndLocal { get; private set; }

    public async Task InitializeAsync()
    {
        AssertSafeDatabaseName(DatabaseName);
        WorkloadStartLocal = DateTime.Now;

        await using (var master = new SqlConnection(MasterConnectionString))
        {
            await master.OpenAsync();
            await DropIfExistsAsync(master);
            await ExecAsync(master, $"CREATE DATABASE [{DatabaseName}];");

            // A freshly created FULL-recovery database behaves like SIMPLE
            // (auto-truncates the log on checkpoint) until its first full
            // backup - "pseudo-simple" mode. Without this, a slow test run
            // can have SQL Server checkpoint and silently wipe out this
            // fixture's own workload before a later test queries it - a real risk. Backing up to NUL
            // performs a real backup (anchoring true FULL behavior) without
            // writing a file anywhere.
            await ExecAsync(master, $"BACKUP DATABASE [{DatabaseName}] TO DISK = 'NUL:';");
        }

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, $"CREATE TABLE {TableName} (Id INT NOT NULL PRIMARY KEY, Note VARCHAR(200) NULL, Amount INT NULL);");
        await ExecAsync(conn, $"INSERT INTO {TableName} (Id, Note, Amount) VALUES ({Row1Id}, '{Row1Note}', {Row1AmountBeforeUpdate});");
        await ExecAsync(conn, $"INSERT INTO {TableName} (Id, Note, Amount) VALUES ({Row2Id}, 'second-row', 200);");
        await ExecAsync(conn, $"UPDATE {TableName} SET Amount = {Row1AmountAfterUpdate} WHERE Id = {Row1Id};");
        await ExecAsync(conn, $"DELETE FROM {TableName} WHERE Id = {Row2Id};");

        // A schema-changing DDL, so DdlBoundaryReader and the end-to-end
        // schema-drift guard have something real to detect.
        await ExecAsync(conn, $"ALTER TABLE {TableName} ADD Bonus INT NULL;");
        await ExecAsync(conn, $"INSERT INTO {TableName} (Id, Note, Amount, Bonus) VALUES ({Row3IdPostDdl}, 'post-ddl-row', 300, {Row3Bonus});");

        WorkloadEndLocal = DateTime.Now;
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();
        await DropIfExistsAsync(master);
    }

    private static async Task DropIfExistsAsync(SqlConnection master)
    {
        AssertSafeDatabaseName(DatabaseName);
        await ExecAsync(master, $"""
            IF DB_ID('{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Mirrors Assert-SafeTarget from the research phase's
    /// experiments/common.ps1: integration tests must never be able to
    /// create or drop anything outside their own dedicated sandbox
    /// database, no matter what DatabaseName is refactored to later.
    /// </summary>
    private static void AssertSafeDatabaseName(string name)
    {
        if (!name.StartsWith("LogCarver_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to operate on database '{name}': integration tests may only touch databases prefixed 'LogCarver_'.");
    }
}

[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
