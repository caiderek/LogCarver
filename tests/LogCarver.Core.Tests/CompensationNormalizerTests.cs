using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class CompensationNormalizerTests
{
    private const string Page = "0001:000000f8";

    private static LogRecord Modify(string lsn, string tx, int offset, string oldHex, string newHex, int slot = 0, string page = Page) =>
        new(lsn, "LOP_MODIFY_ROW", "LCX_CLUSTERED", offset, "dbo.T.PK", page, slot, tx,
            Convert.FromHexString(oldHex), Convert.FromHexString(newHex));

    private static LogRecord Compensation(string lsn, string tx, int offset, string restoredHex, int slot = 0, string page = Page) =>
        new(lsn, "LOP_MODIFY_ROW", "LCX_CLUSTERED", offset, "dbo.T.PK", page, slot, tx,
            [], Convert.FromHexString(restoredHex), IsCompensation: true);

    private static string Hex(byte[]? bytes) => bytes is null ? "(null)" : Convert.ToHexString(bytes);

    [Fact]
    public void ACompensationRecord_GetsTheBytesTheRolledBackChangeWrote_AsItsOldBytes()
    {
        // The real shape from fn_dblog: change 0x30 -> 0x31, then the compensation record that
        // puts 0x30 back, whose old-bytes column is an empty array.
        LogRecord[] records =
        [
            Modify("A:1", "tx1", 31, "30", "31"),
            Compensation("A:2", "tx1", 31, "30"),
        ];

        var result = CompensationNormalizer.FillCompensationOldBytes(records);

        Assert.Equal("31", Hex(result[1].RowLogContents0)); // what it replaces
        Assert.Equal("30", Hex(result[1].RowLogContents1)); // what it puts back
        Assert.Equal("30", Hex(result[0].RowLogContents0)); // the original is untouched
        Assert.Empty(records[1].RowLogContents0!);          // and the input was not modified in place
    }

    [Fact]
    public void SeveralRolledBackChanges_AreEachPairedWithTheirOwn_InReverseOrder()
    {
        LogRecord[] records =
        [
            Modify("A:1", "tx1", 20, "AA", "BB"),
            Modify("A:2", "tx1", 30, "CC", "DD"),
            Compensation("A:3", "tx1", 30, "CC"), // undoes the second
            Compensation("A:4", "tx1", 20, "AA"), // then the first
        ];

        var result = CompensationNormalizer.FillCompensationOldBytes(records);

        Assert.Equal("DD", Hex(result[2].RowLogContents0));
        Assert.Equal("BB", Hex(result[3].RowLogContents0));
    }

    [Fact]
    public void OtherTransactionsInBetween_DoNotConfuseThePairing()
    {
        LogRecord[] records =
        [
            Modify("A:1", "tx1", 31, "30", "31"),
            Modify("A:2", "tx2", 45, "11", "22", slot: 3),   // somebody else, another row
            Compensation("A:3", "tx1", 31, "30"),
        ];

        var result = CompensationNormalizer.FillCompensationOldBytes(records);

        Assert.Equal("31", Hex(result[2].RowLogContents0));
        Assert.Equal("11", Hex(result[1].RowLogContents0)); // not touched
    }

    [Fact]
    public void ACompensationRecordThatDoesNotLineUp_IsLeftAlone()
    {
        // Different offset, different bytes restored, other row, or nothing to undo: not understood, not guessed.
        LogRecord[] differentOffset = [Modify("A:1", "tx1", 31, "30", "31"), Compensation("A:2", "tx1", 33, "30")];
        LogRecord[] differentBytes = [Modify("A:1", "tx1", 31, "30", "31"), Compensation("A:2", "tx1", 31, "99")];
        LogRecord[] otherSlot = [Modify("A:1", "tx1", 31, "30", "31"), Compensation("A:2", "tx1", 31, "30", slot: 1)];
        LogRecord[] nothingToUndo = [Compensation("A:1", "tx1", 31, "30")];

        foreach (var records in new[] { differentOffset, differentBytes, otherSlot, nothingToUndo })
        {
            var result = CompensationNormalizer.FillCompensationOldBytes(records);
            Assert.Empty(result[^1].RowLogContents0!);
        }
    }

    [Fact]
    public void NothingToFill_ReturnsTheSameList()
    {
        LogRecord[] records = [Modify("A:1", "tx1", 31, "30", "31"), Modify("A:2", "tx1", 31, "31", "32")];
        Assert.Same(records, CompensationNormalizer.FillCompensationOldBytes(records));
    }

    // ---- The real sequence that produced a wrong undo SQL (2026-10-18) -------------------------

    private static readonly IReadOnlyList<ColumnSchema> NameTableSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Name", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 100, SystemTypeId: 231),
    ];

    // Real fn_dblog bytes of: INSERT 'seed-initial'; UPDATE -> 'pcross-v0'; BEGIN TRAN; UPDATE -> 'pcross-v1'; ROLLBACK;
    // UPDATE -> 'other-db-val1' (the row grows); UPDATE -> 'other-db-val2'.
    private static LogRecord[] RealSequence() =>
    [
        new("L:01", "LOP_INSERT_ROWS", "LCX_CLUSTERED", 0, "dbo.T.PK", Page, 0, "txInsert",
            Convert.FromHexString("30000800010000000200000100270073006500650064002D0069006E0069007400690061006C00"), []),
        Modify("L:02", "txA", 13, "270073006500650064002D0069006E0069007400690061006C", "21007000630072006F00730073002D00760030"),
        Modify("L:03", "txRolledBack", 31, "30", "31"),
        Compensation("L:04", "txRolledBack", 31, "30"),
        Modify("L:05", "txB", 13, "21007000630072006F00730073002D00760030", "29006F0074006800650072002D00640062002D00760061006C0031"),
        Modify("L:06", "txC", 39, "31", "32"),
    ];

    [Fact]
    public void TheNextUpdateAfterARollback_IsDecodedAgainstTheRightRowImage()
    {
        var records = CompensationNormalizer.FillCompensationOldBytes(RealSequence());

        var history = RowHistoryReconstructor.Reconstruct(records, NameTableSchema, ddlBoundaryLsns: []);

        var last = history[^1];
        Assert.Equal(RowEventKind.Update, last.Kind);
        Assert.Null(last.Note);
        Assert.Equal("other-db-val1", last.Before!["Name"]);
        Assert.Equal("other-db-val2", last.After!["Name"]);
        Assert.DoesNotContain(history, e => e.TransactionId == "txRolledBack"); // the rolled-back change reports nothing
    }

    [Fact]
    public void WithoutTheNormalizer_TheSameSequenceIsDecodedWrong_SoTheTestAboveReallyDetectsTheBug()
    {
        var history = RowHistoryReconstructor.Reconstruct(RealSequence(), NameTableSchema, ddlBoundaryLsns: []);

        var last = history[^1];
        bool decodedCorrectly = last.Note is null &&
            Equals(last.Before?["Name"], "other-db-val1") && Equals(last.After?["Name"], "other-db-val2");
        Assert.False(decodedCorrectly);
    }
}
