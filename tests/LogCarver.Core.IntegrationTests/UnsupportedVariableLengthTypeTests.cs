using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServerEdgeCases")]
public class UnsupportedVariableLengthTypeTests(EdgeCaseFixture fixture)
{
    public static TheoryData<string> Refused() => new(EdgeCaseFixture.RefusedVariableLengthTables);
    public static TheoryData<string> LegacyLobs() => new(EdgeCaseFixture.LegacyLobTables);

    private async Task<(IReadOnlyList<ColumnSchema> Schema, LogRecord Insert, IReadOnlyList<LogRecord> Records)> LoadAsync(string table)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, table);
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, table);
        return (schema, Assert.Single(records, r => r.Operation == "LOP_INSERT_ROWS"), records);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task XmlAndSqlVariant_AreRefused_NotDecodedAsVarcharText(string table)
    {
        var (schema, insert, _) = await LoadAsync(table);

        var ex = Assert.Throws<NotSupportedException>(() => RowDecoder.Decode(insert.RowLogContents0!, schema));

        Assert.Contains("not implemented", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task XmlAndSqlVariant_RowIsReportedAsNeedingReview_InsteadOfGarbage(string table)
    {
        var (schema, _, records) = await LoadAsync(table);

        var history = RowHistoryReconstructor.Reconstruct(records, schema, [], null);

        var insert = Assert.Single(history, e => e.Kind == RowEventKind.Insert);
        Assert.Null(insert.After);
        Assert.True(insert.NeedsManualReview);
    }

    [Theory]
    [MemberData(nameof(LegacyLobs))]
    public async Task LegacyLobTypes_DegradeToThePlaceholder_AndFlagTheColumn(string table)
    {
        var (schema, insert, _) = await LoadAsync(table);

        var values = RowDecoder.Decode(insert.RowLogContents0!, schema, [], out var flagged);

        Assert.Equal("<off-row value, not decoded>", values["V"]);
        Assert.Contains("V", flagged);
    }
}
