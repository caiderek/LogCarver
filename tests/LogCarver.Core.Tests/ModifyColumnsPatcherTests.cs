using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// Every hex string here is a real capture from SQL Server 2025 (17.0.1000.7)
/// of a controlled UPDATE on
///   T(Id int, A int, B bigint, C nvarchar(100) NULL, D nvarchar(100) NULL,
///     E varchar(50) NULL, F int NULL)
/// starting from (n, 10, 100, N'alpha', N'bravo', 'ee1', 7): `Before` is the
/// row's LOP_INSERT_ROWS image, Rlc0/1/4 are the LOP_MODIFY_COLUMNS record,
/// and `After` is the same row's LOP_DELETE_ROWS image taken afterwards
/// (i.e. the row exactly as SQL Server itself had it after the UPDATE).
/// </summary>
public class ModifyColumnsPatcherTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    public static IEnumerable<object[]> RealCaptures() =>
    [
        // UPDATE SET A=11, C=N'alphaXX' - fixed column + longer variable column
        [
            "UPDATE A and longer C",
            "30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531",
            "080008001D001D00", "01001000",
            "0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800",
            "30001800020000000B000000640000000000000007000000070000030031003B003E0061006C007000680061005800580062007200610076006F00656531",
        ],
        // UPDATE SET A=11, C=N'al' - shorter
        [
            "UPDATE A and shorter C",
            "30001800030000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531",
            "080008001D001D00", "01001000",
            "0A0000000B0000002D0037003A0061006C0070006800610027003100340061006C000000",
            "30001800030000000B000000640000000000000007000000070000030027003100340061006C0062007200610076006F00656531",
        ],
        // UPDATE SET A=11, C=N'ALPHA' - same length; changed range is 9 of 10 bytes (padded to 12)
        [
            "UPDATE A and same-length C",
            "30001800040000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531",
            "0800080023002300", "01000900",
            "0A0000000B00000061006C00700068006100000041004C005000480041000000",
            "30001800040000000B00000064000000000000000700000007000003002D0037003A0041004C0050004800410062007200610076006F00656531",
        ],
        // UPDATE SET A=11, C=NULL - null bitmap, variable count and offset array all change
        [
            "UPDATE A and C to NULL",
            "30001800080000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531",
            "080008001A001A00", "01001300",
            "0A0000000B0000000003002D0037003A0061006C007000680061000008030023002D003000000000",
            "30001800080000000B000000640000000000000007000000070008030023002D00300062007200610076006F00656531",
        ],
        // UPDATE SET A=11, E='ee1ZZZZ' - three edits, the last a pure insertion (old length 0)
        [
            "UPDATE A and longer varchar E (insertion at row end)",
            "300018000B0000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531",
            "08000800210021003A003A00", "010001000000",
            "0A0000000B0000003A0000003E0000005A5A5A5A",
            "300018000B0000000B00000064000000000000000700000007000003002D0037003E0061006C0070006800610062007200610076006F006565315A5A5A5A",
        ],
        // UPDATE SET A=11, C=N'newval' on a row whose C and F were NULL
        [
            "UPDATE A and C from NULL to a value",
            "300018000C0000000A000000640000000000000007000000070048030023002D00300062007200610076006F00656531",
            "080008001A001A00", "01000900",
            "0A0000000B00000048030023002D0030000000004003002F0039003C006E0065007700760061006C00000000",
            "300018000C0000000B00000064000000000000000700000007004003002F0039003C006E0065007700760061006C0062007200610076006F00656531",
        ],
        // UPDATE SET A=11, B=101, C=N'alphX', E='ee1Z' - two fixed columns, then one var region spanning an untouched D
        [
            "UPDATE A, B, C, E (untouched D inside the edit)",
            "30001800180000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531",
            "0800080021002100", "05001900",
            "0A000000640000000B000000650000003A0061006C0070006800610062007200610076006F006565310000003B0061006C0070006800580062007200610076006F006565315A0000",
            "30001800180000000B00000065000000000000000700000007000003002D0037003B0061006C0070006800580062007200610076006F006565315A",
        ],
        // A different table (Id int, 7 mixed fixed/variable columns): the middle edit grows by
        // 2 bytes (9 -> 11), so the last edit's new offset (115) differs from its old one (113)
        // and is a pure insertion. Exercises the (old offset, new offset) pairs.
        [
            "three edits where an earlier edit's growth moves a later edit's offset",
            "30003D001701000011020000012CF16E000000000000334345202020202078BE540120490B650650DC31739D4FA2D22E976D228D1CBA611E6E000000000C00080803004D00590071006E006E00767676767676767676767676626262626262626262626262626262626262626262626262",
            "150015003F003F0071007300", "010009000000",
            "00A800C201F8006E080803004D005900716E006E000004004F005B007300796E570057005700006E",
            "30003D001701000011020000012CF16E000000000001334345202020202078BE540120490B650650DC31739D4FA2D22E976D228D1CBA611E6E000000000C00000004004F005B00730079006E006E00767676767676767676767676626262626262626262626262626262626262626262626262570057005700",
        ],
    ];

    [Theory]
    [MemberData(nameof(RealCaptures))]
    public void ReproducesTheRowSqlServerActuallyHadAfterTheUpdate(
        string label, string before, string rlc0, string rlc1, string rlc4, string after)
    {
        bool ok = ModifyColumnsPatcher.TryApplyForward(H(before), H(rlc0), H(rlc1), H(rlc4), out var result, out var failure);

        Assert.True(ok, $"{label}: {failure}");
        Assert.Equal(after, Convert.ToHexString(result!));
    }

    [Fact]
    public void RefusesWhenTheLoggedOldBytesDoNotMatchTheKnownImage()
    {
        // Same capture as the first case, but the known "before" image has a
        // different Amount-like byte than the log says it replaced: this is
        // exactly what applying a record to the wrong base image looks like.
        var before = H("30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531");
        before[8] = 0x0C;

        bool ok = ModifyColumnsPatcher.TryApplyForward(before, H("080008001D001D00"), H("01001000"),
            H("0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800"),
            out var result, out var failure);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Contains("do not match the known before image", failure);
    }

    [Fact]
    public void RefusesWhenTheFirstEditsOffsetsDiffer()
    {
        var before = H("30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531");

        bool ok = ModifyColumnsPatcher.TryApplyForward(before, H("080009001D001D00"), H("01001000"),
            H("0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800"),
            out _, out var failure);

        Assert.False(ok);
        Assert.Contains("first edit has different old/new offsets", failure);
    }

    [Fact]
    public void RefusesWhenEditLiesOutsideTheKnownImage()
    {
        var before = H("30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531");

        bool ok = ModifyColumnsPatcher.TryApplyForward(before, H("0800080050005000"), H("01001000"),
            H("0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800"),
            out _, out var failure);

        Assert.False(ok);
        Assert.Contains("outside the known row image", failure);
    }

    [Fact]
    public void RefusesWhenRowLogContentsSizesDoNotAgree()
    {
        var before = H("30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531");

        bool ok = ModifyColumnsPatcher.TryApplyForward(before, H("0800080050"), H("01001000"), H("0A000000"), out _, out var failure);

        Assert.False(ok);
        Assert.Contains("unrecognised", failure);
    }

    [Fact]
    public void RefusesAPatchedRowWhoseLengthDisagreesWithItsOwnOffsetArray()
    {
        // Real capture, but the last edit's new bytes are cut short: no
        // length for the padded block yields a self-consistent row, so it
        // must refuse rather than hand back a truncated row.
        var before = H("30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531");

        bool ok = ModifyColumnsPatcher.TryApplyForward(before, H("080008001D001D00"), H("01001000"),
            H("0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C00700068006100"),
            out var result, out var failure);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Contains("not self-consistent", failure);
    }

    [Fact]
    public void Reconstructor_DecodesAModifyColumnsUpdateEndToEnd()
    {
        IReadOnlyList<ColumnSchema> schema =
        [
            new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
            new ColumnSchema("A", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 4, SystemTypeId: 56),
            new ColumnSchema("B", 3, LeafOffset: 12, LeafNullBit: 3, MaxLength: 8, SystemTypeId: 127),
            new ColumnSchema("C", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 231),
            new ColumnSchema("D", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 200, SystemTypeId: 231),
            new ColumnSchema("E", 6, LeafOffset: -3, LeafNullBit: 6, MaxLength: 50, SystemTypeId: 167),
            new ColumnSchema("F", 7, LeafOffset: 20, LeafNullBit: 7, MaxLength: 4, SystemTypeId: 56),
        ];

        LogRecord[] records =
        [
            new("00000001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.T.PK", "0001:F8", 1, null,
                H("30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531"), null),
            new("00000002", "LOP_MODIFY_COLUMNS", "LCX_CLUSTERED", 0, "dbo.T.PK", "0001:F8", 1, null,
                H("080008001D001D00"), H("01001000"),
                RowLogContents4: H("0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800")),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        var update = history[1];
        Assert.Equal(RowEventKind.Update, update.Kind);
        Assert.False(update.NeedsManualReview);
        Assert.Equal(10, update.Before!["A"]);
        Assert.Equal("alpha", update.Before["C"]);
        Assert.Equal(11, update.After!["A"]);
        Assert.Equal("alphaXX", update.After["C"]);
        Assert.Equal("bravo", update.After["D"]); // untouched column carried through
        Assert.Equal("ee1", update.After["E"]);
        Assert.Equal(7, update.After["F"]);
    }

    [Fact]
    public void Reconstructor_StillRefusesWhenTheOldBytesDoNotMatch()
    {
        IReadOnlyList<ColumnSchema> schema =
        [
            new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
            new ColumnSchema("A", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 4, SystemTypeId: 56),
            new ColumnSchema("B", 3, LeafOffset: 12, LeafNullBit: 3, MaxLength: 8, SystemTypeId: 127),
            new ColumnSchema("C", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 231),
            new ColumnSchema("D", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 200, SystemTypeId: 231),
            new ColumnSchema("E", 6, LeafOffset: -3, LeafNullBit: 6, MaxLength: 50, SystemTypeId: 167),
            new ColumnSchema("F", 7, LeafOffset: 20, LeafNullBit: 7, MaxLength: 4, SystemTypeId: 56),
        ];

        // The slot's known image has A = 12, but the record says it replaced A = 10.
        var insert = H("30001800020000000C00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531");
        LogRecord[] records =
        [
            new("00000001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.T.PK", "0001:F8", 1, null, insert, null),
            new("00000002", "LOP_MODIFY_COLUMNS", "LCX_CLUSTERED", 0, "dbo.T.PK", "0001:F8", 1, null,
                H("080008001D001D00"), H("01001000"),
                RowLogContents4: H("0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800")),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns: []);

        var update = history[1];
        Assert.Null(update.Before);
        Assert.Null(update.After);
        Assert.True(update.NeedsManualReview);
        Assert.Contains("LOP_MODIFY_COLUMNS", update.Note);
    }
}
