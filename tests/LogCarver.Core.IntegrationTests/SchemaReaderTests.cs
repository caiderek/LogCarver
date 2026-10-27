using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class SchemaReaderTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetTableSchemaAsync_MatchesKnownColumnLayout()
    {
        // dbo.TestTable is declared Id INT, Note VARCHAR(200), Amount INT,
        // Bonus INT (added later via ALTER TABLE). Fixed-length columns
        // (Id, Amount, Bonus) are packed consecutively in declaration
        // order in the fixed region; Note, the only variable-length
        // column, is the 1st such column (LeafOffset -1) regardless of
        // where it sits among the fixed ones.
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var schema = await SchemaReader.GetTableSchemaAsync(connection, SqlServerFixture.TableName);

        Assert.Equal(4, schema.Count);

        var id = schema.Single(c => c.Name == "Id");
        Assert.Equal(4, id.LeafOffset); // first fixed column, right after the 4-byte row header
        Assert.Equal(56, id.SystemTypeId); // int

        var note = schema.Single(c => c.Name == "Note");
        Assert.Equal(-1, note.LeafOffset); // 1st variable-length column
        Assert.Equal(167, note.SystemTypeId); // varchar

        var amount = schema.Single(c => c.Name == "Amount");
        Assert.Equal(8, amount.LeafOffset); // 2nd fixed column: right after Id's 4 bytes

        var bonus = schema.Single(c => c.Name == "Bonus");
        Assert.Equal(56, bonus.SystemTypeId);
        // Bonus's own LeafOffset isn't asserted here - it depends on ADD
        // COLUMN placement, which SchemaDriftTests / the end-to-end test
        // cover from the angle that actually matters (old rows must not
        // be decoded using it).
    }

    /// <summary>
    /// Regression test: sys.partitions has one row per index,
    /// not per table. A heap table with any nonclustered index (here, one
    /// backing a NONCLUSTERED PRIMARY KEY) has two partition rows -
    /// index_id=0 for the heap itself, index_id=2 for the index. Without
    /// filtering to index_id IN (0, 1), the query joined in the index's own
    /// internal column layout alongside the heap's, producing a bogus
    /// SystemTypeId for the column not covered by the index.
    /// </summary>
    [Fact]
    public async Task GetTableSchemaAsync_OnAHeapWithANonclusteredIndex_OnlyReadsTheHeapsOwnColumnLayout()
    {
        const string heapTable = "dbo.HeapWithNonclusteredPk";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{heapTable}') IS NOT NULL DROP TABLE {heapTable}; " +
            $"CREATE TABLE {heapTable} (Id INT NOT NULL, CustomerName NVARCHAR(100), " +
            $"CONSTRAINT PK_HeapWithNonclusteredPk PRIMARY KEY NONCLUSTERED (Id));",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, heapTable);

        Assert.Equal(2, schema.Count);
        var customerName = schema.Single(c => c.Name == "CustomerName");
        Assert.Equal(231, customerName.SystemTypeId); // nvarchar, not the nonclustered index's internal layout
    }

    /// <summary>
    /// Regression test for a risk flagged (but never verified) in
    /// LogCarver_上架與金流設定紀錄.md's "還沒查證" list: sys.partitions has
    /// one row per PARTITION too, not just per index - a table split across
    /// N partitions returns this entire column layout N times over. Before
    /// this fix, GetTableSchemaAsync appended every one of those duplicate
    /// rows, which corrupted RowDecoder's positional variable-length-column
    /// matching downstream (N duplicates per column shifts the mapping,
    /// producing wrong decoded values - not an exception, the worst failure
    /// mode for a forensics tool). A column's physical layout never varies
    /// by partition (only which partition a given ROW lives in does), so
    /// de-duplicating by ColumnId is the correct fix, not a workaround.
    /// </summary>
    [Fact]
    public async Task GetTableSchemaAsync_OnAPartitionedTable_DoesNotReturnDuplicateColumns()
    {
        const string partitionedTable = "dbo.PartitionedTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{partitionedTable}') IS NOT NULL DROP TABLE {partitionedTable}; " +
            "IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'PS_SchemaReaderTest') DROP PARTITION SCHEME PS_SchemaReaderTest; " +
            "IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'PF_SchemaReaderTest') DROP PARTITION FUNCTION PF_SchemaReaderTest; " +
            "CREATE PARTITION FUNCTION PF_SchemaReaderTest (INT) AS RANGE LEFT FOR VALUES (10, 20); " +
            "CREATE PARTITION SCHEME PS_SchemaReaderTest AS PARTITION PF_SchemaReaderTest ALL TO ([PRIMARY]); " +
            $"CREATE TABLE {partitionedTable} (Id INT NOT NULL, Note VARCHAR(200)) ON PS_SchemaReaderTest(Id); " +
            $"INSERT INTO {partitionedTable} (Id, Note) VALUES (5, 'partition-1'), (15, 'partition-2'), (25, 'partition-3');",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        // Confirms the setup actually spans more than one partition -
        // otherwise this test would pass for the wrong reason (a
        // partition-scheme table SQL Server happened to keep all rows in
        // one partition of).
        await using (var checkPartitions = new SqlCommand(
            $"SELECT COUNT(*) FROM sys.partitions WHERE object_id = OBJECT_ID('{partitionedTable}') AND index_id IN (0,1);", setup))
        {
            long partitionCount = (int)(await checkPartitions.ExecuteScalarAsync())!;
            Assert.True(partitionCount > 1, $"test setup didn't actually create multiple partitions (got {partitionCount})");
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, partitionedTable);

        Assert.Equal(2, schema.Count); // Id + Note, not 2 * partition-count
        Assert.Equal(1, schema.Count(c => c.Name == "Id"));
        Assert.Equal(1, schema.Count(c => c.Name == "Note"));
    }

    /// <summary>
    /// Regression test for the fix above: blindly keeping "whichever partition's row came back first"
    /// is only safe if every partition's physical row layout is actually
    /// identical. SQL Server allows per-partition DATA_COMPRESSION
    /// (ALTER TABLE ... REBUILD PARTITION = n WITH (DATA_COMPRESSION = ...)),
    /// and a compressed partition's rows use a different physical layout
    /// than an uncompressed one - silently picking one partition's layout
    /// and applying it to a differently-shaped partition's rows would be an
    /// even quieter version of the exact bug the de-duplication fix closes.
    /// Confirms GetTableSchemaAsync detects the disagreement and refuses
    /// rather than guessing.
    /// </summary>
    [Fact]
    public async Task GetTableSchemaAsync_OnATableWithMixedPerPartitionCompression_ThrowsRatherThanGuessing()
    {
        const string partitionedTable = "dbo.MixedCompressionTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{partitionedTable}') IS NOT NULL DROP TABLE {partitionedTable}; " +
            "IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'PS_MixedCompressionTest') DROP PARTITION SCHEME PS_MixedCompressionTest; " +
            "IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'PF_MixedCompressionTest') DROP PARTITION FUNCTION PF_MixedCompressionTest; " +
            "CREATE PARTITION FUNCTION PF_MixedCompressionTest (INT) AS RANGE LEFT FOR VALUES (10, 20); " +
            "CREATE PARTITION SCHEME PS_MixedCompressionTest AS PARTITION PF_MixedCompressionTest ALL TO ([PRIMARY]); " +
            $"CREATE TABLE {partitionedTable} (Id INT NOT NULL, Note VARCHAR(200)) ON PS_MixedCompressionTest(Id); " +
            $"INSERT INTO {partitionedTable} (Id, Note) VALUES (5, 'partition-1'), (15, 'partition-2'), (25, 'partition-3'); " +
            // Only partition 1 gets row-compressed - partitions 2 and 3
            // stay uncompressed, a genuine physical-layout mismatch.
            $"ALTER TABLE {partitionedTable} REBUILD PARTITION = 1 WITH (DATA_COMPRESSION = ROW);",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => SchemaReader.GetTableSchemaAsync(connection, partitionedTable));
        Assert.Contains("inconsistent physical layout", ex.Message);
    }

    /// <summary>
    /// Regression test: a table whose CLUSTERED index is non-unique reserves
    /// a hidden "uniquifier" slot invisible to sys.columns, shifting every
    /// real variable-length column's own LeafOffset label down by one -
    /// decoding without accounting for this puts NVARCHAR columns' bytes one
    /// slot off from where they belong (a plausible-looking wrong value,
    /// not an honest failure - see ColumnSchema.LeafOffset's doc comment).
    /// </summary>
    [Fact]
    public async Task GetTableSchemaAsync_OnATableWithNonUniqueClusteredIndex_InjectsTheReservedSlot()
    {
        const string table = "dbo.NonUniqueClusteredTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{table}') IS NOT NULL DROP TABLE {table}; " +
            $"CREATE TABLE {table} (Id INT NOT NULL, ColA NVARCHAR(20) NOT NULL, " +
            "Sortable DATETIME2 NOT NULL, ColB NVARCHAR(20) NULL); " +
            $"ALTER TABLE {table} ADD CONSTRAINT PK_NonUniqueClusteredTestTable PRIMARY KEY NONCLUSTERED (Id); " +
            $"CREATE CLUSTERED INDEX IX_NonUniqueClusteredTestTable ON {table}(Sortable);", // non-unique
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, table);

        Assert.Equal(5, schema.Count); // 4 real columns + the synthetic reserved slot
        var reserved = schema.Single(c => c.Name == ColumnSchema.ReservedUniquifierSlotName);
        Assert.Equal(-1, reserved.LeafOffset);
        Assert.Equal(1, reserved.LeafNullBit);

        var colA = schema.Single(c => c.Name == "ColA");
        Assert.Equal(-2, colA.LeafOffset); // shifted down by one because of the reserved slot above
    }

    /// <summary>Same schema shape, but UNIQUE clustered - no reservation needed, confirms this isn't triggered by non-PK clustering alone.</summary>
    [Fact]
    public async Task GetTableSchemaAsync_OnATableWithUniqueClusteredIndex_DoesNotInjectAnything()
    {
        const string table = "dbo.UniqueClusteredTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{table}') IS NOT NULL DROP TABLE {table}; " +
            $"CREATE TABLE {table} (Id INT NOT NULL, ColA NVARCHAR(20) NOT NULL, " +
            "Sortable DATETIME2 NOT NULL, ColB NVARCHAR(20) NULL); " +
            $"ALTER TABLE {table} ADD CONSTRAINT PK_UniqueClusteredTestTable PRIMARY KEY NONCLUSTERED (Id); " +
            $"CREATE UNIQUE CLUSTERED INDEX IX_UniqueClusteredTestTable ON {table}(Sortable);",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, table);

        Assert.Equal(4, schema.Count); // just the 4 real columns, no reserved slot
        Assert.DoesNotContain(schema, c => c.Name == ColumnSchema.ReservedUniquifierSlotName);
        var colA = schema.Single(c => c.Name == "ColA");
        Assert.Equal(-1, colA.LeafOffset);
    }

    [Fact]
    public async Task GetTableSchemaAsync_UnknownTable_ReturnsEmpty()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var schema = await SchemaReader.GetTableSchemaAsync(connection, "dbo.NoSuchTable");

        Assert.Empty(schema);
    }
}
