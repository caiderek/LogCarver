using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class NoEventsDiagnosisTests
{
    private static readonly DateTime Oldest = new(2026, 10, 14, 15, 52, 0);
    private static readonly DateTime Newest = new(2026, 10, 14, 16, 7, 0);

    private static LogOverview Overview(
        string recovery = "FULL", bool isSa = true, params (string, int)[] top) =>
        new("SalesDb", recovery, "NOTHING", isSa, top);

    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines);

    [Fact]
    public void LogStartsAfterFrom_SaysLogHasRotated()
    {
        var text = Text(NoEventsDiagnosis.Explain(
            Overview(top: ("dbo.Customers.IX_1", 16)), "dbo.Orders", Oldest, Newest,
            from: new DateTime(2026, 10, 14, 15, 0, 0)));

        Assert.Contains("starts at 2026-10-14 15:52:00", text);
        Assert.Contains("0 rows is the correct answer", text);
    }

    [Fact]
    public void FromInsideLog_AndOtherTablesHaveDeletes_BlamesNameOrDatabase()
    {
        var text = Text(NoEventsDiagnosis.Explain(
            Overview(top: ("dbo.Customers.IX_1", 16)), "dbo.Orders", Oldest, Newest,
            from: new DateTime(2026, 10, 14, 16, 0, 0)));

        Assert.DoesNotContain("0 rows is the correct answer", text);
        Assert.Contains("not for this table", text);
        Assert.Contains("dbo.Customers.IX_1: 16 deleted row(s)", text);
    }

    [Fact]
    public void SimpleRecoveryWithoutFrom_MentionsCheckpointTruncation()
    {
        var text = Text(NoEventsDiagnosis.Explain(
            Overview(recovery: "SIMPLE"), "dbo.T", Oldest, Newest, from: null));

        Assert.Contains("SIMPLE recovery", text);
    }

    [Fact]
    public void NotSysadmin_IsFlagged()
    {
        var text = Text(NoEventsDiagnosis.Explain(
            Overview(isSa: false), "dbo.T", Oldest, Newest, from: null));

        Assert.Contains("NOT sysadmin", text);
    }

    [Fact]
    public void EmptyLog_DoesNotThrowAndSaysSo()
    {
        var text = Text(NoEventsDiagnosis.Explain(
            Overview(), "dbo.T", oldest: null, newest: null, from: null));

        Assert.Contains("no transactions with a begin time", text);
        Assert.Contains("no deleted rows for any table", text);
    }
}
