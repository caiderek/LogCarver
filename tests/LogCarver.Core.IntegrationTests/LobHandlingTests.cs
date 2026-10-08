using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServerEdgeCases")]
public class LobHandlingTests(EdgeCaseFixture fixture)
{
    [Fact]
    public async Task InRowLobDecodesCleanly_OversizedLobDecodesAsThePlaceholder()
    {
        // Stale test, found 2026-10-08 while running the full suite after
        // today's off-row/LOB undo-corruption fix (see RowDecoder's and
        // TransactionUndoAssembler's own doc comments): this test still
        // asserted the OLD behavior (throws UnsupportedRowFormatException
        // and loses the whole row) from before that was replaced with a
        // placeholder-string decode (committed in d89fff0, confirmed via
        // `git show` to predate ALL of today's changes - this breakage is
        // pre-existing, not caused by anything in this round).
        //
        // EdgeCaseFixture's 100,000-char value is well past fn_dblog's own
        // ~8000-byte RowLog Contents capture limit, not past SQL Server's
        // off-row storage threshold specifically - so this hits the
        // separate "byte range exceeds what was captured" truncation case
        // (see RowDecoder's own doc comment), not the 0x8000 complex-column
        // case RowDecoderTests.cs's dedicated LobOffRowTest exercises. Both
        // cases share the same safe-degradation shape: decode to a clear
        // placeholder, flag the column, never lose the whole row.
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, EdgeCaseFixture.LobTableName);
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, EdgeCaseFixture.LobTableName);
        var inserts = records.Where(r => r.Operation == "LOP_INSERT_ROWS").ToList();

        Assert.Equal(2, inserts.Count); // one in-row insert, one oversized insert

        var decoded = inserts.Select(r => RowDecoder.Decode(r.RowLogContents0!, schema)).ToList();

        Assert.Contains(decoded, d => Equals(d["SmallLob"], EdgeCaseFixture.InRowValue));
        Assert.Contains(decoded, d => d["SmallLob"] is string s && s.Contains("not decoded", StringComparison.Ordinal));
    }
}
