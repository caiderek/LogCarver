using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

public class RowHistoryReconstructorTests
{
    private static readonly IReadOnlyList<ColumnSchema> LogTestSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("CreatedAt", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 7, SystemTypeId: 42, Scale: 3),
        new ColumnSchema("Amount", 3, LeafOffset: 15, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 167),
        new ColumnSchema("Sentinel", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 100, SystemTypeId: 167),
    ];

    // Real hex: Id=1 insert (see RowPatcherTests for provenance), same table.
    private static readonly byte[] InsertRow = Convert.FromHexString(
        "3000130001000000F73F6404294A0BE90300000500000200220046006E6F74652D314C50542D" +
        "6261613163613933373934393433656361623638616237376438646365643633");
    private static readonly byte[] Rlc0 = Convert.FromHexString("E903"); // Amount 1001
    private static readonly byte[] Rlc1 = Convert.FromHexString("2923"); // Amount 9001

    // possiblyCorrupted translates to a boundary offset landing at row byte
    // 4 - inside LogTestSchema's "Id" column (LeafOffset 4) - so the new
    // per-column check in RowDecoder actually has something real to flag,
    // not an untested placeholder position. Update's own flag stays
    // coarse (RowHistoryReconstructor doesn't attempt per-column
    // attribution through RowPatcher's splice yet), so any non-empty
    // offset works there regardless of what it lands on.
    private static LogRecord Insert(string lsn, string pageId, int slotId, byte[] bytes, bool possiblyCorrupted = false) =>
        new(lsn, "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.LogTest.pk", pageId, slotId, null, bytes, null,
            possiblyCorrupted ? [4] : null);

    private static LogRecord Delete(string lsn, string pageId, int slotId, byte[] bytes, bool possiblyCorrupted = false) =>
        new(lsn, "LOP_DELETE_ROWS", "LCX_MARK_AS_GHOST", null, "dbo.LogTest.pk", pageId, slotId, null, bytes, null,
            possiblyCorrupted ? [4] : null);

    private static LogRecord Update(string lsn, string pageId, int slotId, int offset, byte[] rlc0, byte[] rlc1, bool possiblyCorrupted = false) =>
        new(lsn, "LOP_MODIFY_ROW", "LCX_CLUSTERED", offset, "dbo.LogTest.pk", pageId, slotId, null, rlc0, rlc1,
            possiblyCorrupted ? [0] : null);

    // Real captured LOP_MODIFY_COLUMNS RowLogContents0/1 (a column-level
    // change descriptor, NOT a byte-range splice - see the operation's own
    // case in RowHistoryReconstructor.Reconstruct). Content doesn't matter
    // for these tests since the point is that it's refused outright rather
    // than fed into RowPatcher, but using real bytes rather than made-up
    // ones costs nothing.
    private static LogRecord ModifyColumns(string lsn, string pageId, int slotId) =>
        new(lsn, "LOP_MODIFY_COLUMNS", "LCX_CLUSTERED", 0, "dbo.LogTest.pk", pageId, slotId, null,
            Convert.FromHexString("0F000F002E002E00"), Convert.FromHexString("01000100"));

    [Fact]
    public void InsertThenUpdate_ReconstructsBeforeAndAfterFromInsertImage()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            Update("00000002", "0001:0F", 3, offset: 15, Rlc0, Rlc1),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        Assert.Equal(RowEventKind.Insert, history[0].Kind);
        Assert.Equal(1001, history[0].After!["Amount"]);

        var update = history[1];
        Assert.Equal(RowEventKind.Update, update.Kind);
        Assert.Equal(1001, update.Before!["Amount"]);
        Assert.Equal(9001, update.After!["Amount"]);
        Assert.Equal("note-1", update.After!["Note"]); // untouched column carried through correctly

        Assert.False(history[0].NeedsManualReview); // clean insert, nothing to review
        Assert.False(update.NeedsManualReview); // clean update, nothing to review
    }

    [Fact]
    public void UpdateWithNoPriorInsertObserved_ReportsUnknownRatherThanGuessing()
    {
        LogRecord[] records = [Update("00000001", "0001:0F", 3, offset: 15, Rlc0, Rlc1)];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Single(history);
        Assert.Null(history[0].Before);
        Assert.Null(history[0].After);
        Assert.Contains("before image unknown", history[0].Note);
    }

    [Fact]
    public void UpdateAfterDelete_BeforeAnyNewInsertReusesTheSlot_ReportsUnknownRatherThanStaleRow()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            Delete("00000002", "0001:0F", 3, InsertRow),
            // No new INSERT reuses this slot yet. If an UPDATE record still
            // shows up against it, the deleted row's bytes must not be
            // reused as a stale "before" image.
            Update("00000003", "0001:0F", 3, offset: 15, Rlc0, Rlc1),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(3, history.Count);
        var update = history[2];
        Assert.Null(update.Before);
        Assert.Null(update.After);
        Assert.Contains("before image unknown", update.Note);
    }

    [Fact]
    public void ModifyColumns_RefusedOutright_NeverFedToRowPatcher()
    {
        // Regression test: LOP_MODIFY_COLUMNS must not be grouped with LOP_MODIFY_ROW and
        // have its RowLogContents spliced via RowPatcher, which doesn't throw - it silently
        // produces a corrupted row that decodes to garbage (observed
        // against a real captured record: a garbage Id and the other
        // columns missing entirely, with no error or Note at all).
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            ModifyColumns("00000002", "0001:0F", 3),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        var modifyColumnsEvent = history[1];
        Assert.Equal(RowEventKind.Update, modifyColumnsEvent.Kind);
        Assert.Null(modifyColumnsEvent.Before);
        Assert.Null(modifyColumnsEvent.After);
        Assert.Contains("LOP_MODIFY_COLUMNS", modifyColumnsEvent.Note);
    }

    [Fact]
    public void ModifyColumns_ForgetsTheSlotsKnownImage_SoALaterModifyRowDoesNotSpliceAgainstStaleBytes()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow),
            ModifyColumns("00000002", "0001:0F", 3),
            // This row's real current bytes are now unknown - a LOP_MODIFY_ROW
            // splice against the pre-LOP_MODIFY_COLUMNS image would silently
            // produce a plausible-looking but wrong "after" row.
            Update("00000003", "0001:0F", 3, offset: 15, Rlc0, Rlc1),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Equal(3, history.Count);
        var laterUpdate = history[2];
        Assert.Null(laterUpdate.Before);
        Assert.Null(laterUpdate.After);
        Assert.Contains("before image unknown", laterUpdate.Note);
    }

    [Fact]
    public void PossiblyCorruptedInsert_StillDecodes_ButNoteFlagsIt()
    {
        // LogRecord.PossiblyCorrupted (set by an offline reader when a
        // record's bytes crossed a physical artifact it detected but
        // can't repair) must not
        // block decoding: the values may well be exactly right, this is
        // an honesty flag, not a refusal.
        LogRecord[] records = [Insert("00000001", "0001:0F", 3, InsertRow, possiblyCorrupted: true)];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Single(history);
        Assert.Equal(1001, history[0].After!["Amount"]); // still decoded normally
        Assert.Contains("512-byte log block boundary", history[0].Note);
        // The honesty flag must reach every output surface (console/CSV/SQL/
        // JSON) as an obvious "a human needs to look at this" signal, not
        // just live in the free-text Note a reviewer could skim past - see
        // RowEvent.NeedsManualReview and RowEventExporter's NeedsManualReview
        // column/field/comment-prefix.
        Assert.True(history[0].NeedsManualReview);
    }

    [Fact]
    public void PossiblyCorruptedInsert_TaintsALaterUpdateSplicedOntoIt()
    {
        // The corruption flag must survive in `state` across records - an
        // UPDATE spliced onto a flagged base image is built from bytes
        // that were never confirmed reliable, even though the UPDATE's
        // own record is perfectly fine.
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow, possiblyCorrupted: true),
            Update("00000002", "0001:0F", 3, offset: 15, Rlc0, Rlc1, possiblyCorrupted: false),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        var update = history[1];
        Assert.Equal(9001, update.After!["Amount"]); // still decodes
        Assert.Contains("512-byte log block boundary", update.Note);
    }

    [Fact]
    public void PossiblyCorruptedUpdate_OnAnUntaintedBase_IsStillFlagged()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:0F", 3, InsertRow, possiblyCorrupted: false),
            Update("00000002", "0001:0F", 3, offset: 15, Rlc0, Rlc1, possiblyCorrupted: true),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, LogTestSchema, ddlBoundaryLsns: []);

        Assert.Contains("512-byte log block boundary", history[1].Note);
    }

    [Fact]
    public void PossiblyCorruptedInsert_NamesOnlyTheColumnWhoseBytesAreActuallyAffected()
    {
        // Row byte 4 is inside "Id" (LeafOffset 4, 4 bytes: 4-7) - not
        // inside "Amount" (LeafOffset 15) or either variable-length
        // column. Only Id should be named; a whole-record flag would
        // have named none of them specifically (or all of them).
        var record = new LogRecord("00000001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.LogTest.pk", "0001:0F", 3, null,
            InsertRow, null, PossiblyCorruptedOffsetsInRowLogContents0: [4]);

        var history = RowHistoryReconstructor.Reconstruct([record], LogTestSchema, ddlBoundaryLsns: []);

        Assert.Contains("[Id]", history[0].Note);
        Assert.DoesNotContain("Amount", history[0].Note);
        Assert.DoesNotContain("Sentinel", history[0].Note);
        Assert.Equal(1, history[0].After!["Id"]); // still decoded - a flag isn't a refusal
    }

    [Fact]
    public void PossiblyCorruptedInsert_OffsetNotOverlappingAnyColumn_ProducesNoNote()
    {
        // Row byte 0 is TagA/TagB/fixedEnd (the row's own internal
        // framing) - never decoded into any column's value. Confirms the
        // per-column check doesn't degrade back into "flag if the record
        // touched a boundary anywhere," which is exactly the over-broad
        // (~94% of a real table's events) behavior this feature replaced.
        var record = new LogRecord("00000001", "LOP_INSERT_ROWS", "LCX_CLUSTERED", null, "dbo.LogTest.pk", "0001:0F", 3, null,
            InsertRow, null, PossiblyCorruptedOffsetsInRowLogContents0: [0]);

        var history = RowHistoryReconstructor.Reconstruct([record], LogTestSchema, ddlBoundaryLsns: []);

        Assert.Null(history[0].Note);
    }
}
