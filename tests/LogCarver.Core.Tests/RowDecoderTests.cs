using LogCarver.Core;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// These test rows are not synthetic unless noted - the hex was captured
/// from fn_dblog's RowLog Contents 0 against a real SQL Server 2025
/// instance during the research phase (SQL Server Log Parser
/// 研究紀錄.txt 第八、九節) and cross-checked byte-for-byte by hand
/// before being used here as a regression baseline.
/// </summary>
public class RowDecoderTests
{
    // dbo.LogTest: Id INT, CreatedAt DATETIME2(3), Amount INT, Note VARCHAR(200), Sentinel VARCHAR(100)
    private static readonly IReadOnlyList<ColumnSchema> LogTestSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("CreatedAt", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 7, SystemTypeId: 42, Scale: 3),
        new ColumnSchema("Amount", 3, LeafOffset: 15, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 167),
        new ColumnSchema("Sentinel", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 100, SystemTypeId: 167),
    ];

    // dbo.LogTestTrailNull: Id INT, ColA VARCHAR(50) NOT NULL, ColB VARCHAR(50) NULL
    private static readonly IReadOnlyList<ColumnSchema> TrailNullSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("ColA", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 50, SystemTypeId: 167),
        new ColumnSchema("ColB", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: 50, SystemTypeId: 167),
    ];

    // dbo.T: Id INT, Name NVARCHAR(50)
    private static readonly IReadOnlyList<ColumnSchema> NvarcharSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Name", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 100, SystemTypeId: 231),
    ];

    // dbo.T2: Id INT, C CHAR(5), NC NCHAR(5)
    private static readonly IReadOnlyList<ColumnSchema> CharSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("C", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 5, SystemTypeId: 175),
        new ColumnSchema("NC", 3, LeafOffset: 13, LeafNullBit: 3, MaxLength: 10, SystemTypeId: 239),
    ];

    // dbo.T3: Id INT, D DATE
    private static readonly IReadOnlyList<ColumnSchema> DateSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("D", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 3, SystemTypeId: 40),
    ];

    // dbo.Dt2ScaleTest: Id INT, D0 DATETIME2(0), D7 DATETIME2(7), D7default DATETIME2
    // (no explicit scale declared, which defaults to DATETIME2(7) - the real
    // shape of DECISION.StockDecisionDaily.CreateDate).
    private static readonly IReadOnlyList<ColumnSchema> Dt2ScaleTestSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("D0", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 6, SystemTypeId: 42, Scale: 0),
        new ColumnSchema("D7", 3, LeafOffset: 14, LeafNullBit: 3, MaxLength: 8, SystemTypeId: 42, Scale: 7),
        new ColumnSchema("D7default", 4, LeafOffset: 22, LeafNullBit: 4, MaxLength: 8, SystemTypeId: 42, Scale: 7),
    ];

    // dbo.Dt2ScaleTest2: Id INT, D1 DATETIME2(1), D3 DATETIME2(3), D4 DATETIME2(4),
    // D5 DATETIME2(5), D6 DATETIME2(6) - covers every remaining scale not in
    // Dt2ScaleTestSchema, in particular scale 4 (previously mis-decoded as if
    // its raw ticks were plain milliseconds, same as scale 3's - they share
    // the same 4-byte time-part width but not the same tick unit).
    private static readonly IReadOnlyList<ColumnSchema> Dt2ScaleTest2Schema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("D1", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 6, SystemTypeId: 42, Scale: 1),
        new ColumnSchema("D3", 3, LeafOffset: 14, LeafNullBit: 3, MaxLength: 7, SystemTypeId: 42, Scale: 3),
        new ColumnSchema("D4", 4, LeafOffset: 21, LeafNullBit: 4, MaxLength: 7, SystemTypeId: 42, Scale: 4),
        new ColumnSchema("D5", 5, LeafOffset: 28, LeafNullBit: 5, MaxLength: 8, SystemTypeId: 42, Scale: 5),
        new ColumnSchema("D6", 6, LeafOffset: 36, LeafNullBit: 6, MaxLength: 8, SystemTypeId: 42, Scale: 6),
    ];

    // dbo.DecimalTest: Id INT, Small DECIMAL(5,2), Big DECIMAL(18,4)
    private static readonly IReadOnlyList<ColumnSchema> DecimalSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Small", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 5, SystemTypeId: 106, Scale: 2),
        new ColumnSchema("Big", 3, LeafOffset: 13, LeafNullBit: 3, MaxLength: 9, SystemTypeId: 106, Scale: 4),
    ];

    // dbo.Simple2: Id INT (PK NONCLUSTERED), ColA NVARCHAR(20) NOT NULL,
    // Sortable DATETIME2 (CLUSTERED, non-unique), ColB NVARCHAR(20) NULL.
    // Real bug found 2026-10-02 via LogCarverGuard stress testing: a
    // non-unique clustered index reserves variable-length slot -1 (and
    // null-bitmap bit 1) for SQL Server's hidden uniquifier column, so
    // ColA/ColB's own real labels start at -2/-3, not -1/-2 - decoding
    // without the synthetic ReservedUniquifierSlotName entry SchemaReader
    // now injects put each NVARCHAR's bytes one slot off from where they
    // belonged (ColA decoded as "" instead of its real value).
    private static readonly IReadOnlyList<ColumnSchema> NonUniqueClusteredSchema =
    [
        new ColumnSchema(ColumnSchema.ReservedUniquifierSlotName, -1, LeafOffset: -1, LeafNullBit: 1, MaxLength: 0, SystemTypeId: 0),
        new ColumnSchema("Id", 1, LeafOffset: 12, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("ColA", 2, LeafOffset: -2, LeafNullBit: 4, MaxLength: 40, SystemTypeId: 231),
        new ColumnSchema("Sortable", 3, LeafOffset: 4, LeafNullBit: 2, MaxLength: 8, SystemTypeId: 42, Scale: 7),
        new ColumnSchema("ColB", 4, LeafOffset: -3, LeafNullBit: 5, MaxLength: 40, SystemTypeId: 231),
    ];

    // dbo.T3: same shape as Simple2 above but with a 3rd, always-non-NULL
    // NVARCHAR column (ColC) - confirms the reserved slot's position holds
    // with no trailing-NULL omission in play at all (every real column
    // present), not just the 2-real-column/1-omitted case above.
    private static readonly IReadOnlyList<ColumnSchema> NonUniqueClusteredThreeVarColSchema =
    [
        new ColumnSchema(ColumnSchema.ReservedUniquifierSlotName, -1, LeafOffset: -1, LeafNullBit: 1, MaxLength: 0, SystemTypeId: 0),
        new ColumnSchema("Id", 1, LeafOffset: 12, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("ColA", 2, LeafOffset: -2, LeafNullBit: 4, MaxLength: 20, SystemTypeId: 231),
        new ColumnSchema("Sortable", 3, LeafOffset: 4, LeafNullBit: 2, MaxLength: 8, SystemTypeId: 42, Scale: 7),
        new ColumnSchema("ColB", 4, LeafOffset: -3, LeafNullBit: 5, MaxLength: 20, SystemTypeId: 231),
        new ColumnSchema("ColC", 5, LeafOffset: -4, LeafNullBit: 6, MaxLength: 20, SystemTypeId: 231),
    ];

    // dbo.LobOffRowTest: Id INT, Note NVARCHAR(MAX), with
    // `EXEC sp_tableoption 'dbo.LobOffRowTest', 'large value types out of row', 1`
    // forcing Note off-row regardless of its actual content length.
    private static readonly IReadOnlyList<ColumnSchema> LobOffRowSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 231),
    ];

    // dbo.LobNotLastTest: Id INT, Note NVARCHAR(MAX) (off-row, forced), Tag NVARCHAR(20)
    // (in-row) - Note is declared before Tag, so it is NOT the last variable-length
    // column, exercising offset-chaining through a masked complex-column entry.
    private static readonly IReadOnlyList<ColumnSchema> LobNotLastSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 231),
        new ColumnSchema("Tag", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: 40, SystemTypeId: 231),
    ];

    // dbo.LobTwoOffRowTest: Id INT, NoteA NVARCHAR(MAX), NoteB NVARCHAR(MAX), both
    // forced off-row - two consecutive complex-column entries in the same row.
    private static readonly IReadOnlyList<ColumnSchema> LobTwoOffRowSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("NoteA", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 231),
        new ColumnSchema("NoteB", 3, LeafOffset: -2, LeafNullBit: 3, MaxLength: -1, SystemTypeId: 231),
    ];

    [Fact]
    public void Decode_RealInsertRow_MatchesGroundTruth()
    {
        byte[] row = Convert.FromHexString(
            "3000130001000000F73F6404294A0BE90300000500000200220046006E6F74652D314C50542D" +
            "6261613163613933373934393433656361623638616237376438646365643633");

        var result = RowDecoder.Decode(row, LogTestSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(1001, result["Amount"]);
        Assert.Equal("note-1", result["Note"]);
        Assert.Equal("LPT-baa1ca93794943ecab68ab77d8dced63", result["Sentinel"]);
        Assert.IsType<DateTime>(result["CreatedAt"]);
        Assert.Equal(2026, ((DateTime)result["CreatedAt"]!).Year); // exact ground truth for this field wasn't captured during research
    }

    [Fact]
    public void Decode_TrailingVariableColumnPresent_DecodesNormally()
    {
        byte[] row = Convert.FromHexString(
            "30000800010000000300000200190027006861732D626F7468747261696C696E672D76616C7565");

        var result = RowDecoder.Decode(row, TrailNullSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("has-both", result["ColA"]);
        Assert.Equal("trailing-value", result["ColB"]);
    }

    [Fact]
    public void Decode_TrailingVariableColumnOmitted_DecodesAsNull()
    {
        // ColB is NULL and is the last variable-length column - SQL Server
        // drops its offset-array entry entirely rather than recording a
        // zero-length entry (verified empirically, 研究紀錄 第九節).
        byte[] row = Convert.FromHexString(
            "300008000200000003000401001C00747261696C696E672D6E756C6C");

        var result = RowDecoder.Decode(row, TrailNullSchema);

        Assert.Equal(2, result["Id"]);
        Assert.Equal("trailing-null", result["ColA"]);
        Assert.Null(result["ColB"]);
    }

    [Fact]
    public void Decode_EmptyStringVariableColumn_IsNotConfusedWithNull()
    {
        // Synthetic row (not captured from a real instance): Id=42, A="hi", B="".
        // A zero-length, non-NULL variable column is a real, valid SQL Server
        // value distinct from NULL - the original PowerShell prototype this
        // decoder was ported from conflated the two; this locks in the fix.
        byte[] row = Convert.FromHexString("300008002A0000000300000200130013006869");

        var result = RowDecoder.Decode(row, TrailNullSchema);

        Assert.Equal(42, result["Id"]);
        Assert.Equal("hi", result["ColA"]);
        Assert.Equal(string.Empty, result["ColB"]);
        Assert.NotNull(result["ColB"]);
    }

    [Fact]
    public void Decode_NvarcharColumn_DecodesAsUtf16_NotWindows1252()
    {
        // Real captured row for INSERT INTO dbo.T (Id, Name) VALUES (1, N'hi').
        // nvarchar is stored in-row as UTF-16LE (2 bytes/char), unlike
        // varchar's single byte/char - decoding it as Windows-1252 silently
        // produced "h\0i\0" (each letter followed by a stray control
        // character) with no error. Caught while verifying the snapshot
        // feature against a real nvarchar column.
        byte[] row = Convert.FromHexString("30000800010000000200000100130068006900");

        var result = RowDecoder.Decode(row, NvarcharSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("hi", result["Name"]);
    }

    [Fact]
    public void Decode_CharAndNcharColumns_AreFixedLengthAndSpacePadded()
    {
        // Real captured row for INSERT INTO dbo.T2 (Id, C, NC) VALUES (1, 'ab', N'xy').
        // char/nchar are fixed-length in-row (unlike varchar/nvarchar), always
        // stored padded with spaces out to the declared length - char with
        // single-byte 0x20, nchar with UTF-16 U+0020.
        byte[] row = Convert.FromHexString("1000170001000000616220202078007900200020002000030000");

        var result = RowDecoder.Decode(row, CharSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("ab   ", result["C"]);
        Assert.Equal("xy   ", result["NC"]);
    }

    [Fact]
    public void Decode_DateColumn_DecodesAsDaysSinceYearOne()
    {
        // Synthetic row (not captured from a real instance): Id=42, D=2026-01-15.
        // DATE (system_type_id 40) was entirely unimplemented until this test -
        // every row from a table with a DATE column failed to decode at all
        // (real customer impact: StockDecisionDaily/StockPriceDaily-style
        // tables with a plain date column), which in turn made
        // RowEventExporter.ToSql silently produce an empty file for every
        // event in such a table since UndoSqlGenerator/ReplaySqlGenerator
        // both require a non-null Before/After image to work from.
        //
        // 2026-01-15 is 739630 days after 0001-01-01 (independently computed
        // via .NET's own DateTime subtraction, not via this decoder), stored
        // as the same 3-byte little-endian day count DATETIME2's date part
        // already uses: 739630 = 0x000B492E -> bytes 2E 49 0B.
        byte[] row = Convert.FromHexString("10000B002A0000002E490B020000");

        var result = RowDecoder.Decode(row, DateSchema);

        Assert.Equal(42, result["Id"]);
        Assert.Equal(new DateTime(2026, 1, 15), result["D"]);
    }

    [Fact]
    public void Decode_PositiveDecimalColumns_MatchesGroundTruth()
    {
        // Real captured row for INSERT INTO dbo.DecimalTest (Id, Small, Big)
        // VALUES (1, 123.45, 123456789012.3456), against a real SQL Server
        // instance. DECIMAL/NUMERIC (system_type_id 106/108) was entirely
        // unimplemented until this test - every row from a table with any
        // decimal/numeric column failed to decode at all (real customer
        // impact: DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice columns, found the same day as
        // the DATE gap above - fixing DATE alone was not enough for that
        // table).
        byte[] row = Convert.FromHexString(
            "1000160001000000013930000001C0BA8A3CD5620400030000");

        var result = RowDecoder.Decode(row, DecimalSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(123.45m, result["Small"]);
        Assert.Equal(123456789012.3456m, result["Big"]);
    }

    [Fact]
    public void Decode_NegativeDecimalColumns_SignByteIsInverted()
    {
        // Real captured row for INSERT INTO dbo.DecimalTest (Id, Small, Big)
        // VALUES (2, -123.45, -123456789012.3456) - same magnitude bytes as
        // the positive-value test above, only the sign byte differs (0x00
        // here vs 0x01 there), confirming SQL Server's convention is
        // 1 = positive, 0 = negative (the opposite of the usual sign-bit
        // convention, easy to get backwards without a real captured
        // negative example to check against).
        byte[] row = Convert.FromHexString(
            "1000160002000000003930000000C0BA8A3CD5620400030000");

        var result = RowDecoder.Decode(row, DecimalSchema);

        Assert.Equal(2, result["Id"]);
        Assert.Equal(-123.45m, result["Small"]);
        Assert.Equal(-123456789012.3456m, result["Big"]);
    }

    [Fact]
    public void Decode_OffRowLobColumn_IsMarkedNotDecoded_OtherColumnsUnaffected()
    {
        // Real captured row for INSERT INTO dbo.LobOffRowTest (Id, Note)
        // VALUES (1, N'OFFROW-SENTINEL-1234567890') after sp_tableoption
        // forced Note off-row. The offset-array entry for Note is 0x801F -
        // bit 0x8000 is SQL Server's "complex column" flag marking an
        // off-row/pointer structure (16 bytes, independent of the LOB's
        // actual content length - also verified against a 5000-char value
        // producing the identical shape), not length-prefixed text.
        //
        // Before this fix, treating 0x801F as a plain cumulative offset
        // computed a length that ran past the end of the row and threw,
        // which the outer catch in Decode wrapped as
        // UnsupportedRowFormatException and discarded the ENTIRE row -
        // including the perfectly decodable Id column. Real customer
        // impact: DECISION.StockDecisionDaily's free-text reason/note
        // columns (nvarchar(max)) caused every single row to be silently
        // dropped from Undo/Replay/SQL export (matched 1589601 events, 0
        // bytes of output).
        byte[] row = Convert.FromHexString(
            "30000800010000000200BC01001F8000009165000000008802000001000000");

        var result = RowDecoder.Decode(row, LobOffRowSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("<off-row value, not decoded>", result["Note"]);
    }

    [Fact]
    public void Decode_OffRowLobColumn_IsFlaggedAsPossiblyCorrupted()
    {
        // Same real captured row as Decode_OffRowLobColumn_IsMarkedNotDecoded_OtherColumnsUnaffected.
        // Real bug, found 2026-10-08 via an independent cross-check agent:
        // this flagging was missing for the off-row case even though the
        // adjacent out-of-bounds case already did it - see RowDecoder's own
        // doc comment and TransactionUndoAssembler.ProcessTableAsync, the
        // automated caller that actually depends on this flag existing to
        // avoid executing the placeholder text as if it were real data.
        byte[] row = Convert.FromHexString(
            "30000800010000000200BC01001F8000009165000000008802000001000000");

        RowDecoder.Decode(row, LobOffRowSchema, [], out var possiblyCorruptedColumns);

        Assert.Contains("Note", possiblyCorruptedColumns);
    }

    [Fact]
    public void Decode_OffRowLobColumn_NotLastVariableColumn_ChainsOffsetCorrectly()
    {
        // Real captured row for INSERT INTO dbo.LobNotLastTest (Note, Tag)
        // VALUES (N'OFFROW-NOT-LAST-SENTINEL', N'tag-value') - Note (off-row)
        // is declared BEFORE Tag (in-row), unlike the single-column test
        // above. Confirms the masked offset chains correctly into a
        // following real column: without unconditionally using the masked
        // value as prevEnd, Tag's length would be computed from the raw
        // (unmasked, ~32768-biased) offset and misdecode or throw.
        byte[] row = Convert.FromHexString(
            "3000080001000000030000020021803300000094650000000098020000010000007400610067002D00760061006C0075006500");

        var result = RowDecoder.Decode(row, LobNotLastSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("<off-row value, not decoded>", result["Note"]);
        Assert.Equal("tag-value", result["Tag"]);
    }

    [Fact]
    public void Decode_TwoOffRowLobColumns_BothMarkedNotDecoded()
    {
        // Real captured row for INSERT INTO dbo.LobTwoOffRowTest (NoteA, NoteB)
        // VALUES (N'FIRST-OFFROW-SENTINEL', N'SECOND-OFFROW-SENTINEL'), both
        // forced off-row - two consecutive complex-column offset-array
        // entries in the same row, confirming the masked offset chains
        // correctly from one off-row column into the next.
        byte[] row = Convert.FromHexString(
            "30000800010000000300000200218031800000956500000000A8020000010000000000966500000000A802000001000100");

        var result = RowDecoder.Decode(row, LobTwoOffRowSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("<off-row value, not decoded>", result["NoteA"]);
        Assert.Equal("<off-row value, not decoded>", result["NoteB"]);
    }

    [Fact]
    public void Decode_DateTime2Scale0And7_MatchesGroundTruth()
    {
        // Real captured row for INSERT INTO dbo.Dt2ScaleTest (D0, D7, D7default)
        // VALUES ('2026-09-30 12:34:56', '2026-09-30 12:34:56.1234567',
        // '2026-09-30 12:34:56.1234567') - D7default has no explicit scale,
        // which SQL Server defaults to 7 (8-byte storage, 5-byte time part).
        // Before this fix, any DATETIME2 column at its default scale (no
        // explicit precision - very common in real schemas) refused to
        // decode at all, taking the whole row down via
        // RowHistoryReconstructor.TryDecode. Real customer impact:
        // DECISION.StockDecisionDaily.CreateDate is exactly this shape.
        byte[] row = Convert.FromHexString(
            "10001E0001000000F0B000304A0B87EE977669304A0B87EE977669304A0B040000");

        var result = RowDecoder.Decode(row, Dt2ScaleTestSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56), result["D0"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1234567), result["D7"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1234567), result["D7default"]);
    }

    [Fact]
    public void Decode_DateTime2AllRemainingScales_MatchesGroundTruth()
    {
        // Real captured row for INSERT INTO dbo.Dt2ScaleTest2 (D1, D3, D4, D5, D6)
        // VALUES ('2026-09-30 12:34:56.1', '...56.123', '...56.1234',
        // '...56.12345', '...56.123456'). D3 and D4 share the same 4-byte
        // time-part storage width but NOT the same tick unit (D3's raw value
        // happens to equal plain milliseconds; D4's does not - 0.1ms units)
        // - this is the specific case the old code got wrong by assuming
        // "4-byte time part" always meant milliseconds.
        byte[] row = Convert.FromHexString(
            "10002C000100000061E906304A0BFB29B302304A0BD2A3FF1A304A0B3966FC0D01304A0B40FEDB8B0A304A0B060000");

        var result = RowDecoder.Decode(row, Dt2ScaleTest2Schema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1000000), result["D1"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1230000), result["D3"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1234000), result["D4"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1234500), result["D5"]);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 34, 56).AddTicks(1234560), result["D6"]);
    }

    [Fact]
    public void Decode_DateTime2ScaleDoesNotMatchStorageLength_ThrowsInsteadOfMisdecoding()
    {
        // Same real captured Dt2ScaleTest row as above, but D0's schema
        // claims Scale: 7 (expects a 5-byte time part, 8-byte storage)
        // when the actual in-row data is genuinely scale 0 (3-byte time
        // part, 6-byte storage, per its real MaxLength here). This is the
        // validation path guarding against a scale/length mismatch -
        // refuse rather than silently apply the wrong tick unit.
        byte[] row = Convert.FromHexString(
            "10001E0001000000F0B000304A0B87EE977669304A0B87EE977669304A0B040000");

        IReadOnlyList<ColumnSchema> badScaleSchema =
        [
            new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
            new ColumnSchema("D0", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 6, SystemTypeId: 42, Scale: 7),
        ];

        var ex = Assert.Throws<NotSupportedException>(() => RowDecoder.Decode(row, badScaleSchema));
        Assert.Contains("doesn't match scale", ex.Message);
    }

    [Fact]
    public void Decode_DateTime2ScaleOutOfRange_Throws()
    {
        byte[] row = Convert.FromHexString(
            "10001E0001000000F0B000304A0B87EE977669304A0B87EE977669304A0B040000");

        IReadOnlyList<ColumnSchema> outOfRangeScaleSchema =
        [
            new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
            new ColumnSchema("D0", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 6, SystemTypeId: 42, Scale: 8),
        ];

        var ex = Assert.Throws<NotSupportedException>(() => RowDecoder.Decode(row, outOfRangeScaleSchema));
        Assert.Contains("out of the valid 0-7 range", ex.Message);
    }

    // Byte layout of Decode_RealInsertRow_MatchesGroundTruth's row, hand-
    // verified for these tests specifically: Id fixed [4,8), CreatedAt
    // fixed [8,15), Amount fixed [15,19), Note's own offset-array entry
    // [24,26) with its data at [28,34), Sentinel's entry [26,28) with data
    // at [34,70). Row bytes 0-3 are TagA/TagB/fixedEnd - never decoded
    // into any column.
    private static readonly byte[] RealInsertRowForCorruptionTests = Convert.FromHexString(
        "3000130001000000F73F6404294A0BE90300000500000200220046006E6F74652D314C50542D" +
        "6261613163613933373934393433656361623638616237376438646365643633");

    [Fact]
    public void Decode_OffsetOverlappingAFixedColumn_FlagsOnlyThatColumn()
    {
        var result = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema, [15], out var corrupted);

        Assert.Equal(["Amount"], corrupted);
        Assert.Equal(1001, result["Amount"]); // still decoded - a flag isn't a refusal
    }

    [Fact]
    public void Decode_OffsetOverlappingAVariableColumnsData_FlagsThatColumn()
    {
        // Byte 30 is inside Note's actual character data [28,34), not its
        // offset-array entry.
        var result = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema, [30], out var corrupted);

        Assert.Equal(["Note"], corrupted);
        Assert.Equal("note-1", result["Note"]);
    }

    [Fact]
    public void Decode_OffsetOverlappingAVariableColumnsOffsetArrayEntry_FlagsThatColumn()
    {
        // Byte 24 is Note's own 2-byte cumulative-offset entry, not its
        // data - this is the exact mechanism of the real bug this feature
        // exists for (DECISION.StockDecisionDaily.DecisionFlag: the
        // ENTRY, not the character data, was the corrupted byte - see
        // research_notes.md). The entry's actual value here is fine (this
        // is a synthetic offset choice against real row bytes, not a real
        // corrupted record), so the decoded value is still correct - the
        // point is that this position is treated as belonging to Note.
        var result = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema, [24], out var corrupted);

        Assert.Equal(["Note"], corrupted);
        Assert.Equal("note-1", result["Note"]);
    }

    [Fact]
    public void Decode_OffsetInRowHeaderBytes_FlagsNothing()
    {
        // Bytes 0-3 (TagA/TagB/fixedEnd) are never decoded into any
        // column value - confirms the check doesn't degrade into "flag if
        // anything anywhere in the row touched a boundary," which is
        // exactly the over-broad signal (~94% of a real table's events)
        // this per-column feature replaced.
        var result = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema, [0, 1, 2, 3], out var corrupted);

        Assert.Empty(corrupted);
        Assert.Equal(1001, result["Amount"]);
    }

    [Fact]
    public void Decode_OffsetsOverlappingTwoDifferentColumns_FlagsBoth()
    {
        var result = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema, [15, 30], out var corrupted);

        Assert.Equal(2, corrupted.Count);
        Assert.Contains("Amount", corrupted);
        Assert.Contains("Note", corrupted);
    }

    [Fact]
    public void Decode_NoPossiblyCorruptedOffsetsGiven_BehavesExactlyLikeThePlainOverload()
    {
        var withEmptyList = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema, [], out var corrupted);
        var plain = RowDecoder.Decode(RealInsertRowForCorruptionTests, LogTestSchema);

        Assert.Empty(corrupted);
        Assert.Equal(plain["Amount"], withEmptyList["Amount"]);
        Assert.Equal(plain["Note"], withEmptyList["Note"]);
    }

    // Synthetic (not a real captured row, unlike the rest of this file) -
    // hand-built specifically to put a boundary offset inside a NULL fixed-
    // length column's reserved-but-unread byte range. Two INT columns, no
    // variable-length section: tagA=0x10 (null bitmap only), fixedEnd=12,
    // Id=42 (bytes 4-7, not null), OptionalAmount's reserved bytes are
    // 0xFFFFFFFF (bytes 8-11, deliberately garbage-looking to prove they're
    // never actually read into the decoded value), colCount=2 (bytes
    // 12-13), null bitmap byte 0x02 (bit1 set -> OptionalAmount is NULL).
    private static readonly byte[] RowWithNullFixedColumn = Convert.FromHexString("10000C002A000000FFFFFFFF020002");

    private static readonly IReadOnlyList<ColumnSchema> TwoIntColumnsSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("OptionalAmount", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 4, SystemTypeId: 56),
    ];

    [Fact]
    public void Decode_OffsetInsideANullFixedColumnsReservedBytes_DoesNotFlagIt()
    {
        // Regression test: a NULL fixed-length column's bytes are never
        // read to produce its value (the null bitmap alone decides it's
        // null) - a boundary artifact landing in that reserved range
        // can't have corrupted anything, so flagging it would silently
        // reintroduce a smaller-scale version of the same over-broad
        // "flagged but nothing that mattered was actually touched"
        // problem the whole per-column redesign exists to fix (see
        // research_notes.md's "94%" finding, at the whole-row level
        // instead of one column).
        var result = RowDecoder.Decode(RowWithNullFixedColumn, TwoIntColumnsSchema, [8, 9, 10, 11], out var corrupted);

        Assert.Null(result["OptionalAmount"]);
        Assert.Empty(corrupted);
    }

    [Fact]
    public void Decode_OffsetInsideANonNullFixedColumn_StillFlagsIt()
    {
        // Same row, offset inside Id's range instead (Id is NOT null) -
        // confirms the fix above didn't accidentally suppress flagging
        // for fixed columns in general, only genuinely-null ones.
        var result = RowDecoder.Decode(RowWithNullFixedColumn, TwoIntColumnsSchema, [4], out var corrupted);

        Assert.Equal(42, result["Id"]);
        Assert.Equal(["Id"], corrupted);
    }

    // Regression test for a real gap found via customer testing on
    // 2026-09-30: a bigint column (system_type_id 127, e.g. an IDENTITY
    // primary key like [LOG].[JobRun]'s JobID) hit the "not implemented
    // yet" NotSupportedException, which discards the entire row - every
    // other column lost too, not just this one - exactly the failure mode
    // this decoder exists to avoid for a type this common.
    private static readonly IReadOnlyList<ColumnSchema> BigIntColumnSchema =
    [
        new ColumnSchema("JobId", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 8, SystemTypeId: 127),
    ];

    [Fact]
    public void Decode_BigIntColumn_DecodesAsInt64NotJustTheLow32Bits()
    {
        // tagA=0x10 (null bitmap only, no var-length columns), fixedEnd=12,
        // JobId = 5,000,000,000 (bytes 4-11, little-endian) - deliberately
        // larger than Int32.MaxValue (~2.1 billion) to prove this reads the
        // full 8 bytes as Int64 rather than silently truncating to 32 bits,
        // colCount=1 (bytes 12-13), null bitmap byte 0x00 (not null).
        byte[] row = Convert.FromHexString("10000C0000F2052A01000000010000");

        var result = RowDecoder.Decode(row, BigIntColumnSchema);

        Assert.Equal(5_000_000_000L, result["JobId"]);
    }

    // dbo/[LOG].[JobRun]: JobID BIGINT, JobName VARCHAR(100), StartTime
    // DATETIME2(7), EndTime DATETIME2(7) NULL, Status VARCHAR(20),
    // CreateDate DATETIME2(7). Real RowLog Contents 0 captured against a
    // real customer's SQL Server for an INSERT of (1, "1", 2026-09-09
    // 00:00:00.0000000, NULL, "RUNNING", 2026-09-30 20:19:45.3810173),
    // cross-checked byte-for-byte by hand before being used as this
    // regression's ground truth.
    private static readonly IReadOnlyList<ColumnSchema> JobRunSchema =
    [
        new ColumnSchema("JobID", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 8, SystemTypeId: 127),
        new ColumnSchema("JobName", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: 100, SystemTypeId: 167),
        new ColumnSchema("StartTime", 3, LeafOffset: 12, LeafNullBit: 3, MaxLength: 8, SystemTypeId: 42, Scale: 7),
        new ColumnSchema("EndTime", 4, LeafOffset: 20, LeafNullBit: 4, MaxLength: 8, SystemTypeId: 42, Scale: 7),
        new ColumnSchema("Status", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 20, SystemTypeId: 167),
        new ColumnSchema("CreateDate", 6, LeafOffset: 28, LeafNullBit: 6, MaxLength: 8, SystemTypeId: 42, Scale: 7),
    ];

    [Fact]
    public void Decode_NullDateTime2FixedColumn_DoesNotCrashTheWholeRow()
    {
        // Regression test for a real gap found via customer testing on
        // 2026-09-30, immediately after the BigInt fix above let decoding
        // get this far: a NULL fixed-length column's reserved bytes used
        // to be decoded unconditionally (then overwritten with null
        // afterward) - harmless for TypeInt/TypeBigInt (BitConverter never
        // throws on any bit pattern), but DecodeDateTime2 calls
        // DateTime.AddDays on whatever day-count those reserved bytes
        // happen to contain. This row's real, actual EndTime=NULL column
        // reserved bytes decoded to a day count outside DateTime's valid
        // range, throwing ArgumentOutOfRangeException and losing every
        // other column in the row - misreported to the customer as "likely
        // a compressed row" (the decoder's generic fallback for any
        // indexing/range exception), which was flatly wrong: the table has
        // no compression at all, confirmed via sys.partitions.
        byte[] row = Convert.FromHexString(
            "30002400010000000000000000000000001B4A0B5702000000FC663FFDE9E265AA304A0B06000802002E0035003152554E4E494E47");

        var result = RowDecoder.Decode(row, JobRunSchema);

        Assert.Equal(1L, result["JobID"]);
        Assert.Equal("1", result["JobName"]);
        Assert.Equal(new DateTime(2026, 9, 9, 0, 0, 0), result["StartTime"]);
        Assert.Null(result["EndTime"]);
        Assert.Equal("RUNNING", result["Status"]);
        Assert.Equal(new DateTime(2026, 9, 30, 20, 19, 45, 381), ((DateTime)result["CreateDate"]!), TimeSpan.FromMilliseconds(1));
    }

    // dbo.NewTypesTest: Id INT, TinyIntCol TINYINT, SmallIntCol SMALLINT,
    // BitCol1 BIT, BitCol2 BIT, GuidCol UNIQUEIDENTIFIER, MoneyCol MONEY,
    // SmallMoneyCol SMALLMONEY, DateTimeCol DATETIME, SmallDateTimeCol
    // SMALLDATETIME, FloatCol FLOAT, RealCol REAL - added to close the gap
    // found via a real customer table (DECISION.FUNDAMENTAL.MonthlyRevenue,
    // RevenueYear SMALLINT) where every single row was lost, flagged
    // NeedsManualReview with "system_type_id 52 is not implemented yet."
    // BitCol1/BitCol2 share one LeafOffset (bit-packing, verified real: both
    // columns reported the identical leaf_offset from
    // sys.system_internals_partition_columns, distinguished only by bit
    // position within that byte).
    private static readonly IReadOnlyList<ColumnSchema> NewTypesSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("TinyIntCol", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 1, SystemTypeId: 48),
        new ColumnSchema("SmallIntCol", 3, LeafOffset: 9, LeafNullBit: 3, MaxLength: 2, SystemTypeId: 52),
        new ColumnSchema("BitCol1", 4, LeafOffset: 11, LeafNullBit: 4, MaxLength: 1, SystemTypeId: 104),
        new ColumnSchema("BitCol2", 5, LeafOffset: 11, LeafNullBit: 5, MaxLength: 1, SystemTypeId: 104),
        new ColumnSchema("GuidCol", 6, LeafOffset: 12, LeafNullBit: 6, MaxLength: 16, SystemTypeId: 36),
        new ColumnSchema("MoneyCol", 7, LeafOffset: 28, LeafNullBit: 7, MaxLength: 8, SystemTypeId: 60, Scale: 4),
        new ColumnSchema("SmallMoneyCol", 8, LeafOffset: 36, LeafNullBit: 8, MaxLength: 4, SystemTypeId: 122, Scale: 4),
        new ColumnSchema("DateTimeCol", 9, LeafOffset: 40, LeafNullBit: 9, MaxLength: 8, SystemTypeId: 61),
        new ColumnSchema("SmallDateTimeCol", 10, LeafOffset: 48, LeafNullBit: 10, MaxLength: 4, SystemTypeId: 58),
        new ColumnSchema("FloatCol", 11, LeafOffset: 52, LeafNullBit: 11, MaxLength: 8, SystemTypeId: 62),
        new ColumnSchema("RealCol", 12, LeafOffset: 60, LeafNullBit: 12, MaxLength: 4, SystemTypeId: 59),
    ];

    [Fact]
    public void Decode_NewlySupportedFixedTypes_MatchGroundTruth()
    {
        // Captured from fn_dblog against a real SQL Server 2025 instance;
        // inserted values: TinyIntCol=200, SmallIntCol=-12345, BitCol1=1,
        // BitCol2=0, GuidCol='12345678-1234-5678-9ABC-123456789ABC',
        // MoneyCol=123456.7890, SmallMoneyCol=-321.12,
        // DateTimeCol='2026-05-17 13:45:30.123',
        // SmallDateTimeCol='2026-05-17 13:45:00', FloatCol=3.14159265358979,
        // RealCol=2.71828 - every field hand-verified byte-for-byte before
        // being used here as a regression baseline.
        byte[] row = Convert.FromHexString(
            "1000400001000000C8C7CFD578563412341278569ABC123456789ABCD2029649000000004000CFFF1DBBE2004DB4000039034DB4112D4454FB2109404DF82D400C000000");

        var result = RowDecoder.Decode(row, NewTypesSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal((byte)200, result["TinyIntCol"]);
        Assert.Equal((short)-12345, result["SmallIntCol"]);
        Assert.Equal(true, result["BitCol1"]);
        Assert.Equal(false, result["BitCol2"]);
        Assert.Equal(Guid.Parse("12345678-1234-5678-9ABC-123456789ABC"), result["GuidCol"]);
        Assert.Equal(123456.7890m, result["MoneyCol"]);
        Assert.Equal(-321.1200m, result["SmallMoneyCol"]);
        Assert.Equal(new DateTime(2026, 5, 17, 13, 45, 30, 123), (DateTime)result["DateTimeCol"]!, TimeSpan.FromMilliseconds(4));
        Assert.Equal(new DateTime(2026, 5, 17, 13, 45, 0), result["SmallDateTimeCol"]);
        Assert.Equal(3.14159265358979, (double)result["FloatCol"]!, 1e-12);
        Assert.Equal(2.71828f, (float)result["RealCol"]!, 1e-5f);
    }

    // dbo.DecPrecisionProbe: Id INT, Val DECIMAL(38,10) - 17-byte storage
    // (precision 29-38, 4 base-2^32 groups), the one precision bucket
    // DecodeDecimal used to refuse outright (its 128-bit magnitude doesn't
    // fit System.Decimal's 96-bit one). Found missing for real 2026-10-08
    // via an independent cross-check agent hitting a real DECIMAL(38,10)
    // column - undo generation was silently unavailable for it.
    private static readonly IReadOnlyList<ColumnSchema> DecPrecisionSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Val", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 17, SystemTypeId: 106, Scale: 10),
    ];

    [Fact]
    public void Decode_Decimal38Precision_UsesBigIntegerNotSystemDecimal()
    {
        // Captured from fn_dblog against a real SQL Server 2025 instance -
        // a negative value right at the edge of what System.Decimal's own
        // 96-bit mantissa could have held, hand-verified group-by-group
        // (same base-2^32, least-significant-group-first, sign-byte
        // convention already established for the 1-3 group case) before
        // being used here as a regression baseline.
        byte[] row = Convert.FromHexString("100019000100000000d3c6d0f242a6360f6e05010000000000020000");

        var result = RowDecoder.Decode(row, DecPrecisionSchema);

        Assert.Equal(1, result["Id"]);
        var val = Assert.IsType<HighPrecisionDecimal>(result["Val"]);
        Assert.Equal("-123456789012345.1234567891", val.ToString());
    }

    [Fact]
    public void Decode_Decimal38Precision_PositiveNearMaxValue()
    {
        // Sibling capture, same table: a positive value near the actual
        // maximum DECIMAL(38,10) can hold (28 nines before the point).
        byte[] row = Convert.FromHexString("100019000200000001ffffffff9f36f400d946dad510ee8507020000");

        var result = RowDecoder.Decode(row, DecPrecisionSchema);

        Assert.Equal(2, result["Id"]);
        var val = Assert.IsType<HighPrecisionDecimal>(result["Val"]);
        Assert.Equal("999999999999999999999999999.9999999999", val.ToString());
    }

    // dbo.CrossCheck_Blob2: Id INT IDENTITY, Blob VARBINARY(MAX) - real
    // capture from an independent cross-check agent's fourth testing round,
    // 2026-10-08: every variable-length column used to decode as text
    // unconditionally (see RowDecoder.DecodeVariableLengthValue's own doc
    // comment), so VARBINARY's raw bytes came out as garbled Windows-1252
    // text instead of a byte[] - undo SQL built from it could never
    // actually execute.
    private static readonly IReadOnlyList<ColumnSchema> VarBinarySchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Blob", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 165, TypeName: "varbinary"),
    ];

    [Fact]
    public void Decode_VarBinary_DecodesAsRawBytes_NotText()
    {
        byte[] row = Convert.FromHexString("3000080001000000020000010015000102030405ff");

        var result = RowDecoder.Decode(row, VarBinarySchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0xFF }, result["Blob"]);
    }

    // dbo.CrossCheck_Geo: Id INT, Loc GEOGRAPHY - same round. sys.columns
    // reports system_type_id 240 (SQL Server's generic "CLR user-defined
    // type" marker - geography/geometry/hierarchyid all share it) for this
    // column, but SchemaReader's own query reads SystemTypeId from
    // sys.system_internals_partition_columns, which - confirmed via a
    // direct comparison against a real geography column - collapses CLR
    // UDTs down to 165 ("varbinary"), the SAME value a real VARBINARY
    // column reports. 165 here matches what SchemaReader would actually
    // produce for this column, not sys.columns' own 240 - TYPE_NAME(user_
    // type_id) is what actually distinguishes it from plain varbinary (see
    // ColumnSchema.TypeName's own doc comment). On-disk bytes are exactly
    // the type's own binary serialization (confirmed via a direct
    // CAST(... AS VARBINARY(MAX)) comparison against a real
    // geography::Point value - byte-for-byte identical to what fn_dblog
    // captured here).
    private static readonly IReadOnlyList<ColumnSchema> GeographySchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Loc", 2, LeafOffset: -1, LeafNullBit: 2, MaxLength: -1, SystemTypeId: 165, TypeName: "geography"),
    ];

    [Fact]
    public void Decode_Geography_DecodesAsTaggedClrBinaryValue_NotText()
    {
        // Real captured row for INSERT INTO dbo.CrossCheck_Geo (Id, Loc)
        // VALUES (1, geography::Point(25.03, 121.56, 4326)).
        byte[] row = Convert.FromHexString(
            "300008000100000002000001002500e6100000010c48e17a14ae073940a4703d0ad7635e40");

        var result = RowDecoder.Decode(row, GeographySchema);

        Assert.Equal(1, result["Id"]);
        var loc = Assert.IsType<SqlClrBinaryValue>(result["Loc"]);
        Assert.Equal("geography", loc.TypeName);
        Assert.Equal(
            Convert.FromHexString("e6100000010c48e17a14ae073940a4703d0ad7635e40"),
            loc.Bytes);
    }

    [Fact]
    public void NonUniqueClusteredIndex_ReservedSlotShiftsRealColumns_StillDecodesCorrectly()
    {
        // Real captured row for INSERT INTO dbo.Simple2 (Id, ColA, Sortable, ColB)
        // VALUES (1, 'XYZ', '2026-01-01', NULL) - confirmed via manual
        // byte-by-byte hex decoding against sys.system_internals_partition_columns'
        // own reported LeafOffset/LeafNullBit values (see NonUniqueClusteredSchema's
        // comment). ColB is NULL and NOT trailing-omitted here (unlike the
        // unique-clustered-index case) because the reserved uniquifier slot
        // sits after it in the full conceptual ordering - it's no longer
        // genuinely "last".
        byte[] row = Convert.FromHexString(
            "30001000000000000020490B01000000050010020019001F00580059005A00");

        var result = RowDecoder.Decode(row, NonUniqueClusteredSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("XYZ", result["ColA"]);
        Assert.Null(result["ColB"]);
        Assert.False(result.ContainsKey(ColumnSchema.ReservedUniquifierSlotName));
    }

    [Fact]
    public void NonUniqueClusteredIndex_ThreeNonNullVariableColumns_AllDecodeToTheRightColumn()
    {
        // Real captured row for INSERT INTO dbo.T3 (Id, ColA, Sortable, ColB, ColC)
        // VALUES (1, 'AAA', '2026-01-01', 'BBB', 'CCC') - every variable-length
        // column present and non-NULL, so nothing is trailing-omitted; this is
        // what pins down that the reserved slot is always first (not last, and
        // not reversing the real columns' own relative order) rather than just
        // "whichever single slot happens to be empty" in the 2-column case above.
        byte[] row = Convert.FromHexString(
            "30001000000000000020490B0100000006000004001D00230029002F00410041004100420042004200430043004300");

        var result = RowDecoder.Decode(row, NonUniqueClusteredThreeVarColSchema);

        Assert.Equal(1, result["Id"]);
        Assert.Equal("AAA", result["ColA"]);
        Assert.Equal("BBB", result["ColB"]);
        Assert.Equal("CCC", result["ColC"]);
    }
}
