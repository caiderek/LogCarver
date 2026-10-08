using LogCarver.Core;
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
        //
        // The literal is wrapped in CONVERT(DATETIME2(7), '...') rather
        // than a bare string - see FormatSqlLiteral's own doc comment for
        // why a bare 7-digit string literal outright throws (not just
        // mismatches) against a plain DATETIME column, a real regression
        // in this same fix found hours after it first shipped.
        var dt = new DateTime(2026, 10, 8, 8, 19, 9).AddTicks(6665310);
        var row = new Dictionary<string, object?> { ["Id"] = 1, ["UpdatedAt"] = dt };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal(
            "DELETE FROM [dbo].[Orders] WHERE [Id] = 1 AND CONVERT(DATETIME2(7), [UpdatedAt]) = CONVERT(DATETIME2(7), '2026-10-08 08:19:09.6665310');",
            sql);
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

    [Fact]
    public void UpdateOnAnIdentityTable_ExcludesTheIdentityColumnFromSet_ButKeepsItInWhere()
    {
        // Real bug, found 2026-10-08 via an independent cross-check agent
        // testing LogCarverGuard against a table using SQL Server's
        // ordinary default IDENTITY-PK pattern - every test fixture in
        // this project's own history up to that point happened to use an
        // explicitly-assigned PK instead, so this was invisible until an
        // outside tester hit it on the very first try. SQL Server rejects
        // outright any UPDATE that tries to SET an identity column, even
        // to its own unchanged value (error 8102) - restore_audit_record
        // failed 100% of the time on any identity-keyed table as a result.
        // A row's own identity value can never actually change via UPDATE
        // in the first place, so simply never SETting it loses nothing.
        var evt = MakeEvent(RowEventKind.Update, before: Row(5, "before", 100), after: Row(5, "after", 100));

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders", identityColumnName: "Id");

        Assert.Equal(
            "UPDATE [dbo].[Orders] SET [Note] = N'before', [Amount] = 100 WHERE [Id] = 5 AND [Note] = N'after' AND [Amount] = 100;",
            sql);
    }

    [Fact]
    public void DeleteUndoOnAnIdentityTable_WrapsTheReinsertInIdentityInsertOnOff()
    {
        // Sibling fix to the UPDATE case above: undoing a DELETE re-INSERTs
        // the row, and restoring its ORIGINAL identity value (not letting
        // SQL Server assign a new one) requires SET IDENTITY_INSERT ON
        // around the INSERT - without it, SQL Server rejects an INSERT
        // that names an identity column in its column list outright.
        var evt = MakeEvent(RowEventKind.Delete, before: Row(5, "gone", 100), after: null);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders", identityColumnName: "Id");

        Assert.Equal(
            "SET IDENTITY_INSERT [dbo].[Orders] ON; INSERT INTO [dbo].[Orders] ([Id], [Note], [Amount]) VALUES (5, N'gone', 100); SET IDENTITY_INSERT [dbo].[Orders] OFF;",
            sql);
    }

    [Fact]
    public void NoIdentityColumnGiven_BehavesExactlyAsBefore()
    {
        // identityColumnName defaults to null - every existing caller
        // (CLI, exporter, pre-2026-10-08 tests) must see identical output
        // to before this parameter existed.
        var updateEvt = MakeEvent(RowEventKind.Update, before: Row(5, "before", 100), after: Row(5, "after", 100));
        Assert.Equal(
            "UPDATE [dbo].[Orders] SET [Id] = 5, [Note] = N'before', [Amount] = 100 WHERE [Id] = 5 AND [Note] = N'after' AND [Amount] = 100;",
            UndoSqlGenerator.Generate(updateEvt, "dbo.Orders"));

        var deleteEvt = MakeEvent(RowEventKind.Delete, before: Row(5, "gone", 100), after: null);
        Assert.Equal(
            "INSERT INTO [dbo].[Orders] ([Id], [Note], [Amount]) VALUES (5, N'gone', 100);",
            UndoSqlGenerator.Generate(deleteEvt, "dbo.Orders"));
    }

    [Fact]
    public void GuidValue_FormatsAsAQuotedString()
    {
        // Same missing-case pattern as bigint/decimal/bool before it: found
        // 2026-10-08 via an independent cross-check agent hitting a GUID
        // primary key (an entirely ordinary schema choice) - undo
        // generation crashed with NotSupportedException, degrading safely
        // (caught further up the stack, same as the bool gap) but leaving
        // GUID-keyed tables with zero undo capability. SQL Server accepts
        // a UNIQUEIDENTIFIER literal as a plain quoted string in its
        // canonical form.
        var guid = new Guid("12345678-90ab-cdef-1234-567890abcdef");
        var row = new Dictionary<string, object?> { ["Id"] = guid, ["Note"] = "hello" };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = '12345678-90ab-cdef-1234-567890abcdef' AND [Note] = N'hello';", sql);
    }

    [Fact]
    public void HighPrecisionDecimalValue_FormatsAsAPlainDecimalLiteral()
    {
        // Same missing-case pattern, found the same session: a
        // DECIMAL(38,10) column (precision outside System.Decimal's own
        // ~28-29 digit ceiling) used to be refused at the DECODE level
        // entirely (RowDecoder's own, deliberate "doesn't fit, refuse
        // rather than guess" design - see HighPrecisionDecimal's own doc
        // comment for why BigInteger, which has no such ceiling, fixes
        // this for real instead of needing a bigger workaround).
        var hp = new HighPrecisionDecimal(
            System.Numerics.BigInteger.Parse("-1234567890123451234567891"), Scale: 10);
        var row = new Dictionary<string, object?> { ["Id"] = 1, ["Price"] = hp };
        var evt = new RowEvent("lsn1", RowEventKind.Insert, null, row, null, null, "0001:0F", 1);

        var sql = UndoSqlGenerator.Generate(evt, "dbo.Orders");

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Id] = 1 AND [Price] = -123456789012345.1234567891;", sql);
    }
}
