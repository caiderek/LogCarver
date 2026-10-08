using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class UndoSqlGeneratorTests
{
    private static IReadOnlyDictionary<string, object?> Row(int id, string? note, int? amount) =>
        new Dictionary<string, object?> { ["Id"] = id, ["Note"] = note, ["Amount"] = amount };

    private static RowEvent MakeEvent(
        RowEventKind kind, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?>? after, string? note = null) =>
        new("lsn1", kind, before, after, note, null, "0001:0F", 1);

    [Fact]
    public void Insert_GeneratesDeleteMatchingTheInsertedRow()
    {
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "hello", 100));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [Note] = N'hello' AND [Amount] = 100;", sql);
    }

    [Fact]
    public void Delete_GeneratesInsertOfTheDeletedRow()
    {
        var evt = MakeEvent(RowEventKind.Delete, Row(5, "hello", 100), null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("INSERT INTO [dbo].[Orders] ([Id], [Note], [Amount]) VALUES (5, N'hello', 100);", sql);
    }

    [Fact]
    public void Update_SetsBeforeValues_MatchesOnAfterValues()
    {
        var evt = MakeEvent(RowEventKind.Update, Row(5, "old", 100), Row(5, "new", 200));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal(
            "UPDATE [dbo].[Orders] SET [Id] = 5, [Note] = N'old', [Amount] = 100 WHERE [Id] = 5 AND [Note] = N'new' AND [Amount] = 200;",
            sql);
    }

    [Fact]
    public void NullValues_UseIsNullInWhereAndNullLiteralInSetOrValues()
    {
        var evt = MakeEvent(RowEventKind.Update, Row(5, null, 1), Row(5, "was-null", null));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("[Note] = NULL", sql); // restoring to NULL in SET
        Assert.Contains("[Amount] IS NULL", sql); // matching a NULL current value in WHERE
    }

    [Fact]
    public void StringWithEmbeddedSingleQuote_IsEscapedByDoubling()
    {
        var evt = MakeEvent(RowEventKind.Delete, Row(5, "O'Brien", 1), null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("N'O''Brien'", sql);
    }

    [Fact]
    public void ColumnNameContainingSqlSyntax_IsBracketEscapedNotInjected()
    {
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["Weird]; DROP TABLE X; --"] = "v" };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Contains("[Weird]]; DROP TABLE X; --]", sql);
    }

    [Fact]
    public void RefusedEvent_NoImagesAvailable_ReturnsNullInsteadOfThrowing()
    {
        var evt = MakeEvent(RowEventKind.Update, null, null, "refused");

        Assert.Null(UndoSqlGenerator.Generate(evt, "dbo.Orders"));
    }

    [Fact]
    public void DecimalValue_FormatsAsPlainNumericLiteral_NotThrows()
    {
        // SqlStatementBuilder.FormatSqlLiteral had no case for `decimal` -
        // any row with a DECIMAL/NUMERIC column (added to RowDecoder
        // separately) crashed the entire Undo/Replay generation outright
        // with "Cannot format a SQL literal for value of type
        // System.Decimal" instead of producing a statement for this one
        // row, taking every other row in the export down with it. Real
        // customer impact: DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice columns.
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["Score"] = 123.45m };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [Score] = 123.45;", sql);
    }

    [Fact]
    public void NegativeDecimalValue_FormatsWithoutScientificNotation()
    {
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["Score"] = -123456789012.3456m };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [Score] = -123456789012.3456;", sql);
    }

    [Fact]
    public void BigIntValue_FormatsAsPlainNumericLiteral_NotThrows()
    {
        // Same class of gap as the decimal case above, found the same way:
        // SqlStatementBuilder.FormatSqlLiteral had no case for `long`, so
        // any row with a BIGINT column (added to RowDecoder separately)
        // crashed the entire Undo/Replay generation outright instead of
        // producing a statement for this one row. Real customer impact:
        // [LOG].[JobRun]'s bigint JobID identity column.
        var row = new Dictionary<string, object?> { ["JobId"] = 5_000_000_000L, ["Status"] = "RUNNING" };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [JobId] = 5000000000 AND [Status] = N'RUNNING';", sql);
    }

    [Fact]
    public void BoolValue_FormatsAsOneOrZero_NotThrows()
    {
        // Same class of gap as the decimal/bigint cases above, found via
        // LogCarverGuard stress testing 2026-10-02: SqlStatementBuilder.
        // FormatSqlLiteral had no case for `bool`, so any row with a BIT
        // column (decoded to C# bool in RowDecoder) crashed undo
        // generation outright with "Cannot format a SQL literal for value
        // of type System.Boolean" - T-SQL has no boolean literal, BIT is
        // written/compared as 1/0.
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["IsActive"] = true };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [IsActive] = 1;", sql);
    }

    [Fact]
    public void DateTimeValue_FormatsWithFullSubMillisecondPrecision_NotJustMilliseconds()
    {
        // Real bug, found 2026-10-08 via LogCarverGuard's Rocky9 remote
        // stress test: a DATETIME2(7) column (SYSDATETIME() almost always
        // has sub-millisecond precision) formatted to only 3 fractional
        // digits here meant the undo WHERE clause's literal never equaled
        // the actual stored value - every single row in a 100,000-row
        // restore reported a false-positive "data already changed"
        // conflict and restored 0 rows, even though nothing had actually
        // changed. .NET DateTime's own tick resolution is 100ns, exactly
        // DATETIME2(7)'s own max precision, so all 7 fractional digits
        // must round-trip, not just the first 3.
        var dt = new DateTime(2026, 10, 8, 8, 19, 9).AddTicks(6665310);
        var row = new Dictionary<string, object?> { ["Id"] = 1, ["UpdatedAt"] = dt };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 1 AND [UpdatedAt] = '2026-10-08 08:19:09.6665310';", sql);
    }

    [Fact]
    public void FalseBoolValue_FormatsAsZero()
    {
        var row = new Dictionary<string, object?> { ["Id"] = 5, ["IsActive"] = false };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 5 AND [IsActive] = 0;", sql);
    }

    [Fact]
    public void TableNameAlreadyBracketQuoted_IsNotDoubleEscaped()
    {
        // Regression test for a real bug found via customer testing on
        // 2026-09-30: the CLI's tableName argument is whatever the caller
        // typed, and typing "[LOG].[JobRun]" is completely ordinary -
        // exactly what SSMS's own "Script Table as" produces. Re-escaping
        // an already-bracketed name without accounting for that doubled
        // every bracket: EscapeIdentifier turned "[LOG].[JobRun]" into
        // "[[LOG]]].[[JobRun]]]" - syntactically broken SQL a customer
        // could not have run as-is.
        var evt = MakeEvent(RowEventKind.Insert, null, Row(5, "hello", 100));

        var sql = UndoSqlGenerator.Generate(evt, "[LOG].[JobRun]");

        Assert.Equal("DELETE FROM [LOG].[JobRun] WHERE [Id] = 5 AND [Note] = N'hello' AND [Amount] = 100;", sql);
    }
}
