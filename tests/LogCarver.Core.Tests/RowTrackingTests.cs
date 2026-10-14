using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// Row tracking and anchoring edge cases found by an independent review:
/// every one is a way for a change to be attached to the wrong row, or for a
/// rebuilt before-image to be taken from a row that is not the changed one.
/// Row images are the real SQL Server 2025 captures used in RowAnchoringTests.
/// </summary>
public class RowTrackingTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    private const string Before = "30001800020000000A00000064000000000000000700000007000003002D0037003A0061006C0070006800610062007200610076006F00656531";
    private const string After = "30001800020000000B000000640000000000000007000000070000030031003B003E0061006C007000680061005800580062007200610076006F00656531";
    private const string Rlc0 = "080008001D001D00";
    private const string Rlc1 = "01001000";
    private const string Rlc4 = "0A0000000B0000002D0037003A0061006C0070006800610031003B003E0061006C0070006800610058005800";
    private const string HashLockInfo = "0101000C00007ED2CF480000010200040204000A61A06ABD401C";   // key hash 61A06ABD401C
    private const string OtherHashLockInfo = "0101000C00007ED2CF480000010200040204000A8194443284A0"; // key hash 8194443284A0
    private const string HeapLockInfo = "0101000C0000D4AB884E00000102000402030004";              // 20 bytes, no hash

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

    // ---- helpers -----------------------------------------------------------
    private static LogRecord Insert(string lsn, string page, int slot, string rowHex, string? lockInfoHex = null, string tx = "T1",
        string context = "LCX_CLUSTERED", bool compensation = false) =>
        new(lsn, "LOP_INSERT_ROWS", context, null, "dbo.T.PK", page, slot, tx, H(rowHex), null,
            RowLogContents2: lockInfoHex is null ? null : H(lockInfoHex), IsCompensation: compensation);

    private static LogRecord Delete(string lsn, string page, int slot, string rowHex, string tx = "T1",
        string context = "LCX_MARK_AS_GHOST", bool compensation = false) =>
        new(lsn, "LOP_DELETE_ROWS", context, null, "dbo.T.PK", page, slot, tx, H(rowHex), null, IsCompensation: compensation);

    private static LogRecord ModifyColumns(string lsn, string page, int slot, string? lockInfoHex = HashLockInfo, string tx = "T1",
        string context = "LCX_CLUSTERED") =>
        new(lsn, "LOP_MODIFY_COLUMNS", context, 0, "dbo.T.PK", page, slot, tx, H(Rlc0), H(Rlc1),
            RowLogContents4: H(Rlc4), RowLogContents3: lockInfoHex is null ? null : H(lockInfoHex));

    private static LogRecord ModifyRow(string lsn, string page, int slot, string oldHex, string newHex, string? lockInfoHex,
        string tx = "T1", string context = "LCX_HEAP", bool compensation = false) =>
        new(lsn, "LOP_MODIFY_ROW", context, 8, "dbo.T", page, slot, tx, H(oldHex), H(newHex),
            RowLogContents3: lockInfoHex is null ? null : H(lockInfoHex), IsCompensation: compensation);

    private static string WithA(string rowHex, byte a)
    {
        var bytes = H(rowHex);
        bytes[8] = a;
        return Convert.ToHexString(bytes);
    }

    private sealed class FakeSource(
        Dictionary<string, IReadOnlyList<PageRow>> pages, Dictionary<string, string>? locations = null) : IAnchorSource
    {
        public List<string> PagesRead { get; } = [];

        public Task<IReadOnlyList<PageRow>?> ReadPageAsync(string pageId, CancellationToken ct)
        {
            PagesRead.Add(pageId);
            return Task.FromResult<IReadOnlyList<PageRow>?>(pages.TryGetValue(pageId, out var rows) ? rows : null);
        }

        public Task<IReadOnlyDictionary<string, string>> LocateByKeyHashAsync(IReadOnlyList<string> hashes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(locations ?? []);
    }

    private static PageRow Row(int slot, string rowHex, string? hash, string type = "PRIMARY_RECORD") =>
        new(slot, type, H(rowHex), hash);

    private static async Task<IReadOnlyDictionary<string, RowImageSeed>> SeedsAsync(
        FakeSource source, IReadOnlyList<LogRecord> records, string? onlyTransaction = null) =>
        await RowImageAnchoring.BuildSeedsAsync(source, records, default, onlyTransaction);

    // ---- anchoring: what counts as evidence of identity ----------------------
    [Fact]
    public async Task Anchor_ClusteredRecordWithoutAKeyHash_NeverTakesTheRowAtItsSlot()
    {
        // The slot may have shifted since: another row's image, which
        // happens to carry the same new bytes, must not become the seed.
        var source = new FakeSource(new() { ["0001:F8"] = [Row(1, After, "AAAAAAAAAAAA")] });

        var seeds = await SeedsAsync(source, [ModifyColumns("00000002", "0001:F8", 1, lockInfoHex: null)]);

        Assert.Empty(seeds);
    }

    [Fact]
    public async Task Anchor_HashedChain_TakesTheRowWithThatHashNotTheOneAtTheSlot()
    {
        var source = new FakeSource(new()
        {
            // slot 1 is now a different row that also verifies; the right row moved to slot 5
            ["0001:F8"] = [Row(1, After, "8194443284A0"), Row(5, After, "61A06ABD401C")],
        });

        var seeds = await SeedsAsync(source, [ModifyColumns("00000002", "0001:F8", 1)]);

        var seed = Assert.Single(seeds.Values);
        Assert.Equal(Before, Convert.ToHexString(seed.Bytes));
    }

    [Fact]
    public async Task Anchor_HeapRow_UsesItsSlotOnlyWhileNothingWasInsertedOrDeletedAtThatSlot()
    {
        string after = WithA(Before, 0x0B);
        var update = ModifyRow("00000002", "0001:F8", 3, "0A000000", "0B000000", HeapLockInfo);
        var source = new FakeSource(new() { ["0001:F8"] = [Row(3, after, null)] });

        var untouched = await SeedsAsync(source, [update]);
        Assert.Equal(Before, Convert.ToHexString(Assert.Single(untouched.Values).Bytes));

        // An INSERT at some other slot of the page says nothing about this one.
        var elsewhere = await SeedsAsync(source,
            [update, Insert("00000003", "0001:F8", 9, Before, tx: "T2", context: "LCX_HEAP")]);
        Assert.Single(elsewhere);

        // A later INSERT at the same slot: the slot could mean another row now.
        var reused = await SeedsAsync(source,
            [update, Insert("00000003", "0001:F8", 3, Before, tx: "T2", context: "LCX_HEAP")]);
        Assert.Empty(reused);
    }

    [Fact]
    public async Task Anchor_TwoSourcesThatBothVerify_GiveNoSeed()
    {
        // The row exists AND a DELETE of the same page and slot verifies the
        // chain too: the key may have been deleted and inserted again.
        var source = new FakeSource(new() { ["0001:F8"] = [Row(1, After, "61A06ABD401C")] });
        var records = new List<LogRecord>
        {
            ModifyColumns("00000002", "0001:F8", 1),
            Delete("00000003", "0001:F8", 1, After, tx: "T2"),
        };

        Assert.Empty(await SeedsAsync(source, records));
    }

    [Fact]
    public async Task Anchor_DeleteImage_IsOnlyAcceptedFromTheSamePageAndSlotWithNothingBetween()
    {
        var source = new FakeSource(new());   // row not on any page: it was deleted
        var update = ModifyColumns("00000002", "0001:F8", 1);

        var same = await SeedsAsync(source, [update, Delete("00000003", "0001:F8", 1, After, tx: "T2")]);
        Assert.Equal(Before, Convert.ToHexString(Assert.Single(same.Values).Bytes));

        var otherPage = await SeedsAsync(source, [update, Delete("00000003", "0001:F9", 1, After, tx: "T2")]);
        Assert.Empty(otherPage);

        var otherSlot = await SeedsAsync(source, [update, Delete("00000003", "0001:F8", 4, After, tx: "T2")]);
        Assert.Empty(otherSlot);

        var intervening = await SeedsAsync(source,
        [
            update,
            Insert("00000003", "0001:F8", 0, Before, OtherHashLockInfo, tx: "T3"),   // pushes slot 1 up
            Delete("00000004", "0001:F8", 1, After, tx: "T2"),
        ]);
        Assert.Empty(intervening);

        // An insert ABOVE the slot cannot move it.
        var above = await SeedsAsync(source,
        [
            update,
            Insert("00000003", "0001:F8", 6, Before, OtherHashLockInfo, tx: "T3"),
            Delete("00000004", "0001:F8", 1, After, tx: "T2"),
        ]);
        Assert.Single(above);
    }

    [Fact]
    public async Task Anchor_ReInsertOnAnotherPage_IsTheOnlySourceAndTheLiveRowIsNotConsulted()
    {
        // Same key (same hash) inserted again after the changes, with an image
        // that is not the one the chain produced: no seed, and the live row
        // (another incarnation) must not be consulted.
        string otherIncarnation = WithA(After, 0x0B);
        var source = new FakeSource(new() { ["0001:F9"] = [Row(0, After, "61A06ABD401C")] });
        var records = new List<LogRecord>
        {
            ModifyColumns("00000002", "0001:F8", 1),
            Insert("00000003", "0001:F9", 0, otherIncarnation, HashLockInfo, tx: "T2"),
        };

        // otherIncarnation == After here, so the re-insert verifies as a move:
        // exactly one source (the re-insert) - the live row is not a second.
        var seeds = await SeedsAsync(source, records);
        Assert.Equal(Before, Convert.ToHexString(Assert.Single(seeds.Values).Bytes));
        Assert.DoesNotContain("0001:F9", source.PagesRead);
        Assert.DoesNotContain("0001:F8", source.PagesRead);
    }

    [Fact]
    public async Task Anchor_ReInsertOnTheSamePage_IsNotAnImageSource()
    {
        // The same key inserted again on the chain's own page is a delete and
        // a re-insert (or another row), not a page split moving the row.
        var source = new FakeSource(new());
        var records = new List<LogRecord>
        {
            ModifyColumns("00000002", "0001:F8", 1),
            Insert("00000003", "0001:F8", 4, After, HashLockInfo, tx: "T2"),
        };

        Assert.Empty(await SeedsAsync(source, records));
    }

    [Fact]
    public async Task Anchor_ReInsertAfterTheKeyWasDeleted_IsNotAnImageSource()
    {
        // The DELETE's own image does not verify (so it is no source either);
        // the later re-insert on another page must not be taken as a move.
        var source = new FakeSource(new());
        var records = new List<LogRecord>
        {
            ModifyColumns("00000002", "0001:F8", 1),
            Delete("00000003", "0001:F8", 1, WithA(After, 0x7F), tx: "T2"),
            Insert("00000004", "0001:F9", 0, After, HashLockInfo, tx: "T3"),
        };

        Assert.Empty(await SeedsAsync(source, records));
    }

    [Fact]
    public void Tracking_RollbackThatIsNotTheInverseOfTheLatestChange_CancelsNothingAndIsFlagged()
    {
        // The compensation is at a slot nothing was changed at: it cannot be
        // matched to the record it undoes, so both inserts stay, flagged.
        LogRecord[] records =
        [
            Insert("00000001", "0001:F8", 1, Before, HashLockInfo, tx: "T1"),
            Insert("00000002", "0001:F8", 2, WithA(Before, 0x63), OtherHashLockInfo, tx: "T1"),
            Delete("00000003", "0001:F8", 5, WithA(Before, 0x63), tx: "T1", compensation: true),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count);
        Assert.All(history, e =>
        {
            Assert.True(e.NeedsManualReview);
            Assert.Contains("rolled back", e.Note);
        });
    }

    [Fact]
    public void Tracking_SeedIsNotUsedWhenADdlBoundaryPrecedesIt()
    {
        var update = ModifyColumns("00000010", "0001:F8", 1);
        var seeds = new Dictionary<string, RowImageSeed>
        {
            [$"H:dbo.T.PK:61A06ABD401C"] = new RowImageSeed(H(Before), "00000010"),
        };

        var history = RowHistoryReconstructor.Reconstruct([update], Schema, ddlBoundaryLsns: ["00000005"], seedImages: seeds);

        var only = Assert.Single(history);
        Assert.Null(only.After);
        Assert.Contains("before image unknown", only.Note);
    }

    [Fact]
    public async Task Anchor_HeapRow_IsNotTakenFromThePageWhenTheLogWasReadOnlyUpToACommit()
    {
        // Past the bound the slot may have been emptied and given to another
        // row; the page still verifies (same bytes) but proves nothing.
        string after = WithA(Before, 0x0B);
        var update = ModifyRow("00000002", "0001:F8", 3, "0A000000", "0B000000", HeapLockInfo);
        var source = new FakeSource(new() { ["0001:F8"] = [Row(3, after, null)] });

        var unbounded = await RowImageAnchoring.BuildSeedsAsync(source, [update], default, null, logWasBounded: false);
        Assert.Single(unbounded);

        var bounded = await RowImageAnchoring.BuildSeedsAsync(source, [update], default, null, logWasBounded: true);
        Assert.Empty(bounded);
    }

    [Fact]
    public async Task Anchor_OneDeleteImageThatVerifiesForTwoChains_IsGivenToNeither()
    {
        // Two rows (two hashes) changed on the same page and slot over time;
        // a single DELETE image verifies for both chains. Which one it belongs
        // to is not known, so no seed - and never "whichever came first".
        var source = new FakeSource(new());
        var records = new List<LogRecord>
        {
            ModifyColumns("00000002", "0001:F8", 1),
            ModifyColumns("00000003", "0001:F8", 1, lockInfoHex: OtherHashLockInfo, tx: "T2"),
            Delete("00000004", "0001:F8", 1, After, tx: "T3"),
        };

        Assert.Empty(await SeedsAsync(source, records));
    }

    [Fact]
    public async Task Anchor_SharedDeleteImage_IsStillAmbiguousWhenOnlyOneTransactionIsAsked()
    {
        // Guard anchors one transaction's rows; another transaction's chain
        // that the same DELETE image verifies for still makes it ambiguous.
        var source = new FakeSource(new());
        var records = new List<LogRecord>
        {
            ModifyColumns("00000002", "0001:F8", 1),
            ModifyColumns("00000003", "0001:F8", 1, lockInfoHex: OtherHashLockInfo, tx: "T2"),
            Delete("00000004", "0001:F8", 1, After, tx: "T3"),
        };

        Assert.Empty(await SeedsAsync(source, records, onlyTransaction: "T1"));
    }

    [Fact]
    public async Task Anchor_BoundedLog_UsesTheLivePageOnlyWhenNothingAfterTheBoundCouldBeTheRow()
    {
        var source = new FakeSource(new() { ["0001:F8"] = [Row(1, After, "61A06ABD401C")] });
        var chain = new List<LogRecord> { ModifyColumns("00000002", "0001:F8", 1) };

        async Task<int> Count(IReadOnlyList<LogRecord>? later) =>
            (await RowImageAnchoring.BuildSeedsAsync(source, chain, default, null, logWasBounded: true, recordsAfterBound: later)).Count;

        Assert.Equal(0, await Count(null));                          // unknown: no seed
        Assert.Equal(1, await Count([]));                            // nothing happened since
        Assert.Equal(1, await Count([                                // another row's change
            ModifyColumns("00000009", "0001:F8", 7, lockInfoHex: OtherHashLockInfo, tx: "T9")]));
        Assert.Equal(0, await Count([                                // this key, again
            ModifyColumns("00000009", "0001:F8", 1, tx: "T9")]));
        Assert.Equal(0, await Count([                                // key re-inserted
            Insert("00000009", "0001:F9", 0, After, HashLockInfo, tx: "T9")]));
        Assert.Equal(0, await Count([                                // could be anyone's
            ModifyColumns("00000009", "0001:F8", 7, lockInfoHex: null, tx: "T9")]));
    }

    [Fact]
    public async Task Anchor_BoundedLog_HeapRowNeedsAQuietPage()
    {
        string after = WithA(Before, 0x0B);
        var update = ModifyRow("00000002", "0001:F8", 3, "0A000000", "0B000000", HeapLockInfo);
        var source = new FakeSource(new() { ["0001:F8"] = [Row(3, after, null)] });

        var quiet = await RowImageAnchoring.BuildSeedsAsync(source, [update], default, null, true, []);
        Assert.Single(quiet);

        var busy = await RowImageAnchoring.BuildSeedsAsync(source, [update], default, null, true,
            [Delete("00000009", "0001:F8", 3, after, tx: "T9", context: "LCX_HEAP")]);
        Assert.Empty(busy);
    }

    [Fact]
    public async Task Anchor_OnlyTheRequestedTransactionsRowsAreAnchored()
    {
        var source = new FakeSource(new() { ["0001:F8"] = [Row(1, After, "61A06ABD401C")] });
        var records = new List<LogRecord> { ModifyColumns("00000002", "0001:F8", 1, tx: "T-other") };

        Assert.Empty(await SeedsAsync(source, records, onlyTransaction: "T-mine"));
        Assert.Empty(source.PagesRead);
        Assert.Single(await SeedsAsync(source, records, onlyTransaction: "T-other"));
    }

    // ---- tracking: slot shifts, rollbacks, moves --------------------------------
    [Fact]
    public void Tracking_FollowsARowWhoseSlotWasPushedByAnInsert()
    {
        // A (no hash) at slot 1; B inserted at slot 1 pushes A to slot 2. A's
        // UPDATE carries the hash and is logged at slot 2.
        var insertA = Insert("00000001", "0001:F8", 1, Before);
        var insertB = Insert("00000002", "0001:F8", 1, WithA(Before, 0x63), OtherHashLockInfo);
        var updateA = ModifyColumns("00000003", "0001:F8", 2);

        var history = RowHistoryReconstructor.Reconstruct([insertA, insertB, updateA], Schema, ddlBoundaryLsns: []);

        var update = history.Single(e => e.Kind == RowEventKind.Update);
        Assert.False(update.NeedsManualReview);
        Assert.Equal(10, update.Before!["A"]);
        Assert.Equal(11, update.After!["A"]);
    }

    [Fact]
    public void Tracking_UpdateWhoseOldBytesAreNotTheRowAtItsSlot_IsRefusedNotApplied()
    {
        // X (hash 8194443284A0) was updated but its INSERT is not in the log;
        // slot 1 is now B's. The record's old bytes are not B's.
        var insertB = Insert("00000001", "0001:F8", 1, Before);
        var updateX = new LogRecord("00000002", "LOP_MODIFY_ROW", "LCX_CLUSTERED", 8, "dbo.T.PK", "0001:F8", 1, "T2",
            H("63000000"), H("64000000"), RowLogContents3: H(OtherHashLockInfo));

        var history = RowHistoryReconstructor.Reconstruct([insertB, updateX], Schema, ddlBoundaryLsns: []);

        var update = history.Single(e => e.Kind == RowEventKind.Update);
        Assert.Null(update.After);
        Assert.True(update.NeedsManualReview);
    }

    [Fact]
    public void Tracking_RolledBackInsert_ReportsNothing()
    {
        // INSERT then its compensation (a ghost DELETE) in the same transaction.
        LogRecord[] records =
        [
            Insert("00000001", "0001:F8", 1, Before, HashLockInfo, tx: "T1"),
            Delete("00000002", "0001:F8", 1, Before, tx: "T1", compensation: true),
        ];

        Assert.Empty(RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []));
    }

    [Fact]
    public void Tracking_RolledBackUpdate_ReportsNothingAndLeavesTheRowAsItWas()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:F8", 1, Before, HashLockInfo, tx: "T0"),
            ModifyRow("00000002", "0001:F8", 1, "0A000000", "0B000000", null, tx: "T1", context: "LCX_CLUSTERED"),
            // rollback: the same span with old and new swapped
            ModifyRow("00000003", "0001:F8", 1, "0B000000", "0A000000", null, tx: "T1", context: "LCX_CLUSTERED", compensation: true),
            // a later real update must start from the restored image
            ModifyRow("00000004", "0001:F8", 1, "0A000000", "0C000000", null, tx: "T2", context: "LCX_CLUSTERED"),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        Assert.Equal([RowEventKind.Insert, RowEventKind.Update], history.Select(e => e.Kind).ToArray());
        Assert.Equal("00000004", history[1].Lsn);
        Assert.Equal(10, history[1].Before!["A"]);
        Assert.Equal(12, history[1].After!["A"]);
    }

    [Fact]
    public void Tracking_ARollbackOfOneStatementKeepsTheEarlierStatementsOfTheTransaction()
    {
        // BEGIN; INSERT row 7; SAVE; INSERT row 8; ROLLBACK TO SAVE; COMMIT
        LogRecord[] records =
        [
            Insert("00000001", "0001:F8", 1, Before, HashLockInfo, tx: "T1"),
            Insert("00000002", "0001:F8", 2, WithA(Before, 0x63), OtherHashLockInfo, tx: "T1"),
            Delete("00000003", "0001:F8", 2, WithA(Before, 0x63), tx: "T1", compensation: true),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        var only = Assert.Single(history);
        Assert.Equal(RowEventKind.Insert, only.Kind);
        Assert.Equal("00000001", only.Lsn);
    }

    [Fact]
    public void Tracking_ReInsertOnTheSamePage_IsStillAnInsert()
    {
        // Only a re-insert on a DIFFERENT page (a page split) is a move.
        LogRecord[] records =
        [
            Insert("00000001", "0001:F8", 1, Before, HashLockInfo, tx: "T1"),
            Insert("00000002", "0001:F8", 4, Before, HashLockInfo, tx: "T2"),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        Assert.Equal(2, history.Count(e => e.Kind == RowEventKind.Insert));
    }

    [Fact]
    public void Tracking_DeleteThenInsertOfIdenticalContent_IsADeleteAndAnInsert()
    {
        LogRecord[] records =
        [
            Insert("00000001", "0001:F8", 1, Before, HashLockInfo, tx: "T1"),
            Delete("00000002", "0001:F8", 1, Before, tx: "T2"),
            Insert("00000003", "0001:F9", 0, Before, HashLockInfo, tx: "T3"),
        ];

        var history = RowHistoryReconstructor.Reconstruct(records, Schema, ddlBoundaryLsns: []);

        Assert.Equal(
            [RowEventKind.Insert, RowEventKind.Delete, RowEventKind.Insert],
            history.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void Tracking_BeforeImageRebuiltFromTheCurrentRow_IsFlagged()
    {
        var update = ModifyColumns("00000002", "0001:F8", 1);
        var seeds = new Dictionary<string, RowImageSeed>
        {
            [$"H:dbo.T.PK:61A06ABD401C"] = new RowImageSeed(H(Before), "00000002"),
        };

        var withSeed = RowHistoryReconstructor.Reconstruct([update], Schema, ddlBoundaryLsns: [], seedImages: seeds);
        Assert.True(Assert.Single(withSeed).BeforeRebuiltFromCurrentRow);

        var fromLog = RowHistoryReconstructor.Reconstruct(
            [Insert("00000001", "0001:F8", 1, Before, HashLockInfo), update], Schema, ddlBoundaryLsns: []);
        Assert.False(fromLog.Single(e => e.Kind == RowEventKind.Update).BeforeRebuiltFromCurrentRow);
    }
}
