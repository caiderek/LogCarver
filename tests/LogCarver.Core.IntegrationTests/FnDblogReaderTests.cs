using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class FnDblogReaderTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Regression test: FnDblogReader's query must not filter to
    /// Context='LCX_CLUSTERED' only, which would silently exclude every
    /// DELETE (DELETE uses Context='LCX_MARK_AS_GHOST'). Change the SQL
    /// in FnDblogReader.cs back to that single-context filter and this
    /// test must fail.
    /// </summary>
    [Fact]
    public async Task ReadClusteredRecordsAsync_ReturnsAllThreeOperationTypes()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        Assert.Contains(records, r => r.Operation == "LOP_INSERT_ROWS");
        Assert.Contains(records, r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS");
        Assert.Contains(records, r => r.Operation == "LOP_DELETE_ROWS");
    }

    [Fact]
    public async Task ReadClusteredRecordsAsync_RecordsCarryPageAndSlotForRowIdentity()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        Assert.All(records, r =>
        {
            Assert.NotNull(r.PageId);
            Assert.NotNull(r.SlotId);
        });
    }

    /// <summary>
    /// Regression test: a heap table (no clustered index)
    /// uses Context='LCX_HEAP' for INSERT/UPDATE/DELETE alike, which a
    /// two-context filter would exclude entirely - every heap table
    /// silently reported 0 events, misread as "fn_dblog rotated past this"
    /// rather than "this tool never looked at heap tables' rows at all".
    /// </summary>
    [Fact]
    public async Task ReadClusteredRecordsAsync_OnAHeapTable_ReturnsAllThreeOperationTypes()
    {
        const string heapTable = "dbo.HeapTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{heapTable}') IS NOT NULL DROP TABLE {heapTable}; " +
            $"CREATE TABLE {heapTable} (Id INT NOT NULL, Note VARCHAR(200) NULL); " +
            $"INSERT INTO {heapTable} (Id, Note) VALUES (1, 'heap-row-1'), (2, 'heap-row-2'); " +
            $"UPDATE {heapTable} SET Note = 'heap-row-1-updated' WHERE Id = 1; " +
            $"DELETE FROM {heapTable} WHERE Id = 2;", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, heapTable);

        Assert.Contains(records, r => r.Operation == "LOP_INSERT_ROWS");
        Assert.Contains(records, r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS");
        Assert.Contains(records, r => r.Operation == "LOP_DELETE_ROWS");
        Assert.All(records, r => Assert.Equal("LCX_HEAP", r.Context));
    }

    /// <summary>
    /// Regression test: a heap table WITH a nonclustered
    /// index (e.g. one backing a PRIMARY KEY NONCLUSTERED) also has its own
    /// AllocUnitName ("schema.table.indexname") for that index's own
    /// maintenance activity - which an AllocUnitName-prefix
    /// guess for heaps ("schema.table" exact match, no suffix at all) does
    /// technically avoid pulling in via a LIKE prefix, but which a naive
    /// broader match easily could. This pins the fixed behavior down
    /// directly: DELETE must produce exactly one row event on the base
    /// table, not one on the table plus a spurious one from the index's own
    /// b-tree entry removal.
    /// </summary>
    [Fact]
    public async Task ReadClusteredRecordsAsync_OnAHeapWithANonclusteredIndex_DoesNotLeakTheIndexsOwnMaintenanceRecords()
    {
        const string heapTable = "dbo.HeapWithIndexTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{heapTable}') IS NOT NULL DROP TABLE {heapTable}; " +
            $"CREATE TABLE {heapTable} (Id INT NOT NULL, Note VARCHAR(200) NULL, " +
            $"CONSTRAINT PK_HeapWithIndexTestTable PRIMARY KEY NONCLUSTERED (Id)); " +
            $"INSERT INTO {heapTable} (Id, Note) VALUES (1, 'row-1'), (2, 'row-2'); " +
            $"DELETE FROM {heapTable} WHERE Id = 2;", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, heapTable);

        Assert.Equal(2, records.Count(r => r.Operation == "LOP_INSERT_ROWS"));
        Assert.Equal(1, records.Count(r => r.Operation == "LOP_DELETE_ROWS"));
        Assert.All(records, r => Assert.Equal($"{heapTable}", r.AllocUnitName));
    }

    /// <summary>
    /// Same bug class as the heap-with-index test above, but for a
    /// clustered table: a secondary nonclustered index's own AllocUnitName
    /// is "schema.table.indexname", which the original loose
    /// "schema.table.%" prefix guess also matched (any index name fits
    /// after the dot) - only never triggered because no earlier test table
    /// had a second index. Resolving the clustered index's own real name up
    /// front and matching it exactly closes this for clustered tables too.
    /// </summary>
    [Fact]
    public async Task ReadClusteredRecordsAsync_OnAClusteredTableWithASecondaryIndex_DoesNotLeakTheIndexsOwnMaintenanceRecords()
    {
        const string clusteredTable = "dbo.ClusteredWithSecondaryIndexTestTable";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{clusteredTable}') IS NOT NULL DROP TABLE {clusteredTable}; " +
            $"CREATE TABLE {clusteredTable} (Id INT NOT NULL PRIMARY KEY, Note VARCHAR(200) NULL); " +
            $"CREATE NONCLUSTERED INDEX IX_Note ON {clusteredTable} (Note); " +
            $"INSERT INTO {clusteredTable} (Id, Note) VALUES (1, 'row-1'), (2, 'row-2'); " +
            $"DELETE FROM {clusteredTable} WHERE Id = 2;", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, clusteredTable);

        Assert.Equal(2, records.Count(r => r.Operation == "LOP_INSERT_ROWS"));
        Assert.Equal(1, records.Count(r => r.Operation == "LOP_DELETE_ROWS"));
    }

    /// <summary>
    /// Regression test: ResolveOwnAllocUnitNameAsync must not build its fn_dblog
    /// match string from the caller's raw tableName text. Typing the table
    /// bracket-quoted - "[dbo].[TestTable]", exactly what SSMS's own
    /// "Script Table as" output produces, and a completely ordinary way to
    /// type a schema-qualified name - would give "[dbo].[TestTable]" as the
    /// match string, but fn_dblog's real AllocUnitName is never bracketed
    /// ("dbo.TestTable"), so the comparison would never match and every event
    /// would silently vanish, which the CLI reports as "fn_dblog already rotated
    /// past this data" even though the data is still in the active log.
    /// OBJECT_ID(@tableName) tolerates brackets fine, so schema/DDL lookups
    /// elsewhere are not affected - only this string-concatenation site.
    /// </summary>
    [Fact]
    public async Task ReadClusteredRecordsAsync_WithBracketQuotedTableName_StillMatches()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, "[dbo].[TestTable]");

        Assert.Equal(3, records.Count(r => r.Operation == "LOP_INSERT_ROWS"));
    }

    [Fact]
    public async Task ReadClusteredRecordsAsync_DoesNotMatchUnrelatedTableWithSharedPrefix()
    {
        // Guards against the AllocUnitName LIKE '%TableName%' mistake from
        // earlier in the research phase, which cross-matched e.g.
        // LogTestAlterType when searching for LogTest. dbo.TestTableExtra
        // shares "dbo.TestTable" as a literal prefix but is a distinct table.
        string extraTable = $"{SqlServerFixture.TableName}Extra";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{extraTable}') IS NOT NULL DROP TABLE {extraTable}; " +
            $"CREATE TABLE {extraTable} (Id INT NOT NULL PRIMARY KEY); " +
            $"INSERT INTO {extraTable} VALUES (999); INSERT INTO {extraTable} VALUES (998);", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        // TestTable only ever had 3 inserts (Id 1, 2, 3). If the query
        // matched by "AllocUnitName LIKE '%TestTable%'" instead of the
        // precise "TestTable.%" prefix, TestTableExtra's 2 inserts would
        // leak in and this count would be 5, not 3.
        Assert.Equal(3, records.Count(r => r.Operation == "LOP_INSERT_ROWS"));
    }
}
