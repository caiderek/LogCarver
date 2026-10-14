using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// Real captures from SQL Server 2025 (see ModifyColumnsPatcherTests for the
/// table): the INSERT image of row Id=2, the LOP_MODIFY_COLUMNS that changed
/// it (RLC0/1/3/4) and the image after, which is exactly what a later
/// LOP_DELETE_ROWS or the data page holds.
/// </summary>
public class RowAnchoringTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    private const string Before = "30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531";
    private const string After = "30001800020000000B000000640000000000000007000000070000030031003B003E0061006C007000680061005800580062007200610076006F00656531";
    private const string Rlc0 = "080008001D001D00";
    private const string Rlc1 = "01001000";
    private const string Rlc3 = "0101000C00007ED2CF480000010200040204000A61A06ABD401C";
    private const string Rlc4 = "0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800";

    private static readonly IReadOnlyList<ColumnSchema> Schema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("A", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("B", 3, LeafOffset: 12, LeafNullBit: 3, MaxLength: 8, SystemTypeId: 127),
        new ColumnSchema("C", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 231),
        new ColumnSchema("D", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 200, SystemTypeId: 231),
        new ColumnSchema("E", 6, LeafOffset: -3, LeafNullBit: 6, MaxLength: 50, SystemTypeId: 167),
        new ColumnSchema("F", 7, LeafOffset: 20, LeafNullBit: 7, MaxLength: 4, SystemTypeId: 56),
    ];

    private static LogRecord ModifyColumns(string lsn, string page, int slot) =>
        new(lsn, "LOP_MODIFY_COLUMNS", "LCX_CLUSTERED", 0, "dbo.T.PK", page, slot, null,
            H(Rlc0), H(Rlc1), RowLogContents4: H(Rlc4), RowLogContents3: H(Rlc3));

    [Fact]
    public void ApplyBackward_UndoesTheRecordExactly()
    {
        bool ok = ModifyColumnsPatcher.TryApplyBackward(H(After), H(Rlc0), H(Rlc1), H(Rlc4), out var before, out var failure);

        Assert.True(ok, failure);
        Assert.Equal(Before, Convert.ToHexString(before!));
    }

    [Fact]
    public void ApplyBackward_RefusesWhenTheNewBytesAreNotInTheImage()
    {
        var wrongAfter = H(After);
        wrongAfter[8] = 0x7F; // the A column byte the record says it set to 0x0B

        bool ok = ModifyColumnsPatcher.TryApplyBackward(wrongAfter, H(Rlc0), H(Rlc1), H(Rlc4), out var before, out var failure);

        Assert.False(ok);
        Assert.Null(before);
        Assert.Contains("do not match the known after image", failure);
    }

    [Fact]
    public void UndoChain_WalksBackFromTheLaterImage()
    {
        var chain = new[] { ModifyColumns("00000002", "0001:F8", 1) };

        var seed = RowImageAnchoring.UndoChain(H(After), chain);

        Assert.Equal(Before, Convert.ToHexString(seed!));
    }

    [Fact]
    public void UndoChain_GivesNothingForAnImageThatIsNotThisRows()
    {
        var chain = new[] { ModifyColumns("00000002", "0001:F8", 1) };
        var otherRow = H(After);
        otherRow[8] = 0x7F;

        Assert.Null(RowImageAnchoring.UndoChain(otherRow, chain));
    }

    [Fact]
    public void Reconstruct_StartsAnUpdateFromASeedImage()
    {
        var record = ModifyColumns("00000002", "0001:F8", 1);
        var seeds = new Dictionary<string, RowImageSeed>
        {
            [RowIdentity.Of(record)] = new RowImageSeed(H(Before), "00000002"),
        };

        var history = RowHistoryReconstructor.Reconstruct([record], Schema, ddlBoundaryLsns: [], seedImages: seeds);

        var update = Assert.Single(history);
        Assert.False(update.NeedsManualReview);
        Assert.Equal(10, update.Before!["A"]);
        Assert.Equal("alpha", update.Before["C"]);
        Assert.Equal(11, update.After!["A"]);
        Assert.Equal("alphaXX", update.After["C"]);
    }

    [Fact]
    public void Reconstruct_WithoutASeedStillReportsBeforeImageUnknown()
    {
        var history = RowHistoryReconstructor.Reconstruct([ModifyColumns("00000002", "0001:F8", 1)], Schema, ddlBoundaryLsns: []);

        var update = Assert.Single(history);
        Assert.True(update.NeedsManualReview);
        Assert.Contains("before image unknown", update.Note);
    }

    [Fact]
    public void Reconstruct_FollowsARowThatChangedSlotBetweenRecords()
    {
        // Another row was inserted ahead of it, so the same row (same key
        // hash) is logged at slot 4 although it was inserted at slot 1.
        var insert = new LogRecord("00000001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.T.PK", "0001:F8", 1, null,
            H(Before), null, RowLogContents2: H(Rlc3));
        var update = ModifyColumns("00000002", "0001:F8", 4);

        var history = RowHistoryReconstructor.Reconstruct([insert, update], Schema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        Assert.False(history[1].NeedsManualReview);
        Assert.Equal(11, history[1].After!["A"]);
        Assert.Equal("alphaXX", history[1].After["C"]);
    }

    [Fact]
    public void Reconstruct_ModifyRowWithOldBytesThatDoNotMatchIsRefused()
    {
        // The slot's known image has A = 10 but the record claims to replace
        // the 4 bytes at offset 8 with A = 99 -> 100, i.e. a different row.
        var insert = new LogRecord("00000001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.T.PK", "0001:F8", 1, null,
            H(Before), null);
        var update = new LogRecord("00000002", "LOP_MODIFY_ROW", "LCX_CLUSTERED", 8, "dbo.T.PK", "0001:F8", 1, null,
            H("63000000"), H("64000000"));

        var history = RowHistoryReconstructor.Reconstruct([insert, update], Schema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        Assert.Null(history[1].After);
        Assert.True(history[1].NeedsManualReview);
        Assert.Contains("old bytes do not match", history[1].Note);
    }

    [Fact]
    public void RowIdentity_UsesTheKeyHashOfAClusteredRowAndPageSlotForAHeap()
    {
        Assert.Equal("61A06ABD401C", RowIdentity.KeyHash(H(Rlc3)));
        // Real heap lock information (22 bytes, no key hash).
        Assert.Null(RowIdentity.KeyHash(H("0101000C0000D4AB884E00000102000402030004")));
        Assert.Null(RowIdentity.KeyHash(null));

        var clustered = ModifyColumns("00000002", "0001:F8", 1);
        var movedSlot = ModifyColumns("00000003", "0001:F9", 7);
        Assert.Equal(RowIdentity.Of(clustered), RowIdentity.Of(movedSlot));

        var heapA = new LogRecord("1", "LOP_MODIFY_ROW", "LCX_HEAP", 8, "dbo.H", "0001:20", 3, null, null, null,
            RowLogContents3: H("0101000C0000D4AB884E00000102000402030004"));
        var heapB = heapA with { SlotId = 4 };
        Assert.NotEqual(RowIdentity.Of(heapA), RowIdentity.Of(heapB));
    }

    [Fact]
    public void DataPageReader_ParsesARealDbccPageSlot()
    {
        // Lines from DBCC PAGE (print option 3, TABLERESULTS) of a real page.
        var lines = new List<DataPageReader.Line>
        {
            new("PAGE HEADER:", "Slot 0 Offset 0x60 Length 41", "Record Type", "PRIMARY_RECORD"),
            new("Slot 0 Offset 0x60 Length 41", "Memory Dump @0x0000007F462F8060", "",
                "0000000000000000:   30000c00 01000000 0a000000 04000002 001f0029  0..................)"),
            new("Slot 0 Offset 0x60 Length 41", "Memory Dump @0x0000007F462F8060", "",
                "0000000000000014:   0061006c 00700068 00610062 00720061 0076006f  .a.l.p.h.a.b.r.a.v.o"),
            new("Slot 0 Offset 0x60 Length 41", "Memory Dump @0x0000007F462F8060", "",
                "0000000000000028:   00                                            ."),
            new("Slot 0 Offset 0x60 Length 41", "Slot 0 Column 1 Offset 0x4 Length 4 Length (physical) 4", "Id", "1"),
            new("Slot 0 Offset 0x60 Length 41", "Slot 0 Offset 0x0 Length 0 Length (physical) 0", "KeyHashValue", "(8194443284a0)"),
        };

        var row = Assert.Single(DataPageReader.Parse(lines));

        Assert.Equal(0, row.SlotId);
        Assert.Equal("PRIMARY_RECORD", row.RecordType);
        Assert.Equal("8194443284A0", row.KeyHash);
        Assert.Equal(
            "30000C00" + "01000000" + "0A000000" + "04000002" + "001F0029" +
            "0061006C" + "00700068" + "00610062" + "00720061" + "0076006F" + "00",
            Convert.ToHexString(row.Bytes));
        Assert.Equal(41, row.Bytes.Length);
    }

    [Fact]
    public void DataPageReader_DropsARecordWhoseDumpIsNotItsStatedLength()
    {
        var lines = new List<DataPageReader.Line>
        {
            new("Slot 0 Offset 0x60 Length 41", "Memory Dump @0x0000007F462F8060", "",
                "0000000000000000:   30000c00 01000000 0a000000 04000002 001f0029  0..................)"),
        };

        Assert.Empty(DataPageReader.Parse(lines));
    }

    [Theory]
    [InlineData("0001:000000f8", 1, 248)]
    [InlineData("0003:00000100", 3, 256)]
    public void DataPageReader_ParsesFnDblogPageIds(string pageId, int file, int page)
    {
        Assert.True(DataPageReader.TryParsePageId(pageId, out int f, out int p));
        Assert.Equal(file, f);
        Assert.Equal(page, p);
        Assert.False(DataPageReader.TryParsePageId("garbage", out _, out _));
    }
}
