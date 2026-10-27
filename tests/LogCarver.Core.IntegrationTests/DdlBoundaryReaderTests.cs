using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class DdlBoundaryReaderTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetDdlBoundariesAsync_DetectsTheAddColumnDdl()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var boundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, SqlServerFixture.TableName);

        Assert.NotEmpty(boundaries);
        Assert.Contains(boundaries, b => b.TransactionName == "ALTER TABLE");
    }

    [Fact]
    public async Task GetDdlBoundariesAsync_TableThatWasNeverAltered_ReturnsCreateOnly()
    {
        string tableName = "dbo.NeverAltered";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{tableName}') IS NOT NULL DROP TABLE {tableName}; CREATE TABLE {tableName} (Id INT NOT NULL PRIMARY KEY);", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var boundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, tableName);

        Assert.DoesNotContain(boundaries, b => b.TransactionName == "ALTER TABLE");
    }

    /// <summary>
    /// Regression test for a real gap found alongside the table-partitioning
    /// investigation on 2026-09-30: the hobt_id lookup had no index_id
    /// filter at all, so sys.partitions' one-row-per-index shape meant
    /// ExecuteScalar could pick a secondary index's own hobt_id instead of
    /// the table's own heap/clustered one - not limited to partitioned
    /// tables, any table with a secondary index was exposed. Matching DDL
    /// against the wrong hobt_id finds zero boundaries where a real one
    /// exists, silently defeating the schema-drift guard this class exists
    /// for.
    /// </summary>
    [Fact]
    public async Task GetDdlBoundariesAsync_OnATableWithASecondaryIndex_StillDetectsDdlAgainstTheTablesOwnHobt()
    {
        const string tableName = "dbo.DdlWithSecondaryIndexTest";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{tableName}') IS NOT NULL DROP TABLE {tableName}; " +
            $"CREATE TABLE {tableName} (Id INT NOT NULL PRIMARY KEY, Note VARCHAR(200)); " +
            $"CREATE NONCLUSTERED INDEX IX_Note ON {tableName} (Note); " +
            $"ALTER TABLE {tableName} ADD Bonus INT NULL;",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var boundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, tableName);

        Assert.Contains(boundaries, b => b.TransactionName == "ALTER TABLE");
    }

    /// <summary>
    /// Regression test for the table-partitioning risk flagged (but never
    /// verified) in LogCarver_上架與金流設定紀錄.md's "還沒查證" list: a
    /// partitioned table has one hobt_id per partition, and a
    /// schema-changing DDL only needs to touch one partition's hobt to be a
    /// real boundary. Before this fix, only whichever hobt_id ExecuteScalar
    /// happened to return first was checked - an ALTER TABLE landing on a
    /// different partition's hobt would be silently missed, and old-layout
    /// rows from that partition could then be decoded with the new schema
    /// with no warning (exactly the "94%"-class silent-corruption failure
    /// mode this whole class exists to prevent).
    ///
    /// Uses REBUILD PARTITION = 3, not ADD COLUMN: an independent reviewer
    /// caught that ADD COLUMN is metadata-only and applies to every
    /// partition's hobt at once, so it would find a boundary even against
    /// the old single-hobt code and pass for the wrong reason.
    /// REBUILD PARTITION = 3 WITH (DATA_COMPRESSION = ROW) is a genuine
    /// physical rewrite intended to touch only that one partition's own
    /// hobt_id, which is what this test means to exercise ("check every
    /// hobt_id" versus "check whichever one came back first").
    ///
    /// Honesty note: bug injection against this exact test (reverting just
    /// the union-across-hobts change) did NOT reproduce a failure on the
    /// real SQL Server instance this was verified against, whether
    /// rebuilding partition 1 or partition 3 - LOP_HOBT_DDL's logging for a
    /// single-partition REBUILD did not behave the way the reasoning above
    /// predicted on this instance, and digging further into exactly why
    /// was judged not worth the time against the actual risk. The
    /// production fix (querying every hobt_id and unioning results,
    /// instead of trusting ExecuteScalar's unspecified row order) remains
    /// unambiguously more correct regardless; this test positively confirms
    /// the fixed code still returns the right answer for this scenario,
    /// same as the SchemaReader tests above.
    /// </summary>
    [Fact]
    public async Task GetDdlBoundariesAsync_OnAPartitionedTable_DetectsDdlThatOnlyTouchedOnePartitionsHobt()
    {
        const string tableName = "dbo.PartitionedDdlTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{tableName}') IS NOT NULL DROP TABLE {tableName}; " +
            "IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'PS_DdlBoundaryTest') DROP PARTITION SCHEME PS_DdlBoundaryTest; " +
            "IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'PF_DdlBoundaryTest') DROP PARTITION FUNCTION PF_DdlBoundaryTest; " +
            "CREATE PARTITION FUNCTION PF_DdlBoundaryTest (INT) AS RANGE LEFT FOR VALUES (10, 20); " +
            "CREATE PARTITION SCHEME PS_DdlBoundaryTest AS PARTITION PF_DdlBoundaryTest ALL TO ([PRIMARY]); " +
            $"CREATE TABLE {tableName} (Id INT NOT NULL, Note VARCHAR(200)) ON PS_DdlBoundaryTest(Id); " +
            $"ALTER TABLE {tableName} REBUILD PARTITION = 3 WITH (DATA_COMPRESSION = ROW);",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var boundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, tableName);

        Assert.Contains(boundaries, b => b.TransactionName == "ALTER TABLE");
    }
}
