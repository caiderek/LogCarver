using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// Heap forwarding as logged by SQL Server 2025 (controlled experiment on a
/// heap whose page was full): when a row no longer fits, the log shows
///   1. LOP_INSERT_ROWS of a forwarded record - the row's OLD image with
///      status type 1 and a 10-byte pointer back to the original place
///   2. LOP_MODIFY_ROW of that forwarded record (the real change)
///   3. LOP_MODIFY_ROW at the original place: the whole old row -> a 9-byte
///      forwarding stub (status 0x04, page u32, file u16, slot u16).
/// Deleting the row later deletes the forwarded record and the stub.
/// The row bytes below are the real row image from the other anchoring tests.
/// </summary>
public class HeapForwardingTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    private const string Row = "30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531";
    private static readonly byte[] Stub = H("049901000001000000"); // real: -> page 0x199, file 1, slot 0

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

    /// <summary>The row as a forwarded record that came from page 0x198, slot 1.</summary>
    private static byte[] Forwarded(byte[] row, byte newA)
    {
        var bytes = new byte[row.Length + 10];
        row.CopyTo(bytes, 0);
        bytes[0] = 0x32;     // status type 1 (forwarded) + null bitmap + variable columns
        bytes[8] = newA;     // A column low byte
        // 2 bytes, then page u32 = 0x198, file u16 = 1, slot u16 = 1
        bytes[row.Length + 2] = 0x98; bytes[row.Length + 3] = 0x01;
        bytes[row.Length + 6] = 0x01;
        bytes[row.Length + 8] = 0x01;
        return bytes;
    }

    private static LogRecord Rec(string lsn, string op, string page, int slot, int? offset, byte[]? r0, byte[]? r1) =>
        new(lsn, op, "LCX_HEAP", offset, "dbo.H", page, slot, null, r0, r1);

    [Fact]
    public void RowFormat_RecognisesStubsAndForwardedRecords()
    {
        Assert.True(RowFormat.IsForwardingStub(Stub));
        Assert.False(RowFormat.IsPrimaryRecord(Stub));
        Assert.True(RowFormat.IsPrimaryRecord(H(Row)));

        var forwarded = Forwarded(H(Row), 0x0A);
        Assert.True(RowFormat.IsForwardedRecord(forwarded));
        Assert.Equal("P:0001:00000198:1", RowFormat.ForwardedFromKey(forwarded));
        Assert.Null(RowFormat.ForwardedFromKey(H(Row)));
    }

    [Fact]
    public void ForwardedRecordsAreSelfConsistent()
    {
        Assert.True(ModifyColumnsPatcher.IsSelfConsistent(H(Row)));
        Assert.True(ModifyColumnsPatcher.IsSelfConsistent(Forwarded(H(Row), 0x0A)));
    }

    [Fact]
    public void ForwardingAnUpdatedRow_ReportsOneUpdateAndNoPhantomInsertOrDelete()
    {
        var oldRow = H(Row);
        var oldAsForwarded = Forwarded(oldRow, 0x0A);
        var newAsForwarded = Forwarded(oldRow, 0x0B); // A: 10 -> 11

        LogRecord[] records =
        [
            Rec("00000001", "LOP_INSERT_ROWS", "0001:00000198", 1, null, oldRow, null),
            // the move: forwarded copy of the old image, the real change, the stub
            Rec("00000002", "LOP_INSERT_ROWS", "0001:00000199", 0, null, oldAsForwarded, null),
            Rec("00000003", "LOP_MODIFY_ROW", "0001:00000199", 0, 0, oldAsForwarded, newAsForwarded),
            Rec("00000004", "LOP_MODIFY_ROW", "0001:00000198", 1, 0, oldRow, Stub),
            // the row is deleted: the forwarded record and the stub
            Rec("00000005", "LOP_DELETE_ROWS", "0001:00000199", 0, null, newAsForwarded, null),
            Rec("00000006", "LOP_DELETE_ROWS", "0001:00000198", 1, null, Stub, null),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        Assert.Equal(
            [RowEventKind.Insert, RowEventKind.Update, RowEventKind.Delete],
            history.Select(e => e.Kind).ToArray());

        var update = history[1];
        Assert.False(update.NeedsManualReview);
        Assert.Equal(10, update.Before!["A"]);
        Assert.Equal(11, update.After!["A"]);

        var delete = history[2];
        Assert.False(delete.NeedsManualReview);
        Assert.Equal(11, delete.Before!["A"]);
    }

    [Fact]
    public void MovingTheRowBackIsNotADelete()
    {
        var oldRow = H(Row);
        var fwd = Forwarded(oldRow, 0x0A);
        var backHome = H(Row);
        backHome[8] = 0x0C; // A -> 12

        LogRecord[] records =
        [
            Rec("00000001", "LOP_INSERT_ROWS", "0001:00000198", 1, null, oldRow, null),
            Rec("00000002", "LOP_INSERT_ROWS", "0001:00000199", 0, null, fwd, null),
            Rec("00000003", "LOP_MODIFY_ROW", "0001:00000198", 1, 0, oldRow, Stub),
            // later: the row gets its place back (pointer -> row), forwarded record removed
            Rec("00000004", "LOP_MODIFY_ROW", "0001:00000198", 1, 0, Stub, backHome),
            Rec("00000005", "LOP_DELETE_ROWS", "0001:00000199", 0, null, fwd, null),
            // and is then updated in place: it must be known again
            Rec("00000006", "LOP_MODIFY_ROW", "0001:00000198", 1, 8, H("0C000000"), H("0D000000")),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        Assert.Equal([RowEventKind.Insert, RowEventKind.Update], history.Select(e => e.Kind).ToArray());
        Assert.Equal(12, history[1].Before!["A"]);
        Assert.Equal(13, history[1].After!["A"]);
    }

    [Fact]
    public void Anchoring_UsesTheImageLoggedWhenARowBecomesAPointer()
    {
        // An old row (no INSERT in the log) is updated in place and, in the
        // same statement, forwarded. Its last logged state is a pointer, so
        // the only complete image is the one in the pointer-conversion record.
        var home = H(Row);
        var afterInPlace = H(Row);
        afterInPlace[8] = 0x0B;

        var inPlace = Rec("00000001", "LOP_MODIFY_ROW", "0001:00000198", 1, 8, H("0A000000"), H("0B000000"));
        var toStub = Rec("00000002", "LOP_MODIFY_ROW", "0001:00000198", 1, 0, afterInPlace, Stub);

        var seeds = RowImageAnchoring.UndoChain(afterInPlace, [inPlace]);

        Assert.Equal(Row, Convert.ToHexString(seeds!));
        Assert.True(RowFormat.IsForwardingStub(toStub.RowLogContents1!));
    }
}
