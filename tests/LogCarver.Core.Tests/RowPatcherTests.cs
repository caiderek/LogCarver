using LogCarver.Core;
using Xunit;

namespace LogCarver.Core.Tests;

/// <summary>
/// All hex here was captured from fn_dblog against a real test table (dbo.LogTest),
/// not reused/guessed, matching a schema captured
/// before the ADD COLUMN DDL that appears elsewhere in that same table's
/// history (see SchemaDriftTests) - this schema is what those specific
/// rows actually had at write time.
/// </summary>
public class RowPatcherTests
{
    private static readonly IReadOnlyList<ColumnSchema> LogTestSchema =
    [
        new ColumnSchema("Id", 1, LeafOffset: 4, LeafNullBit: 1, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("CreatedAt", 2, LeafOffset: 8, LeafNullBit: 2, MaxLength: 7, SystemTypeId: 42, Scale: 3),
        new ColumnSchema("Amount", 3, LeafOffset: 15, LeafNullBit: 3, MaxLength: 4, SystemTypeId: 56),
        new ColumnSchema("Note", 4, LeafOffset: -1, LeafNullBit: 4, MaxLength: 200, SystemTypeId: 167),
        new ColumnSchema("Sentinel", 5, LeafOffset: -2, LeafNullBit: 5, MaxLength: 100, SystemTypeId: 167),
    ];

    [Fact]
    public void NarrowUpdate_SymmetricLength_ForwardMatchesLiveValue_BackwardRoundTrips()
    {
        // Id=1 insert image, then an UPDATE that only touches Amount (1001 -> 9001).
        byte[] insertRow = Convert.FromHexString(
            "3000130001000000F73F6404294A0BE90300000500000200220046006E6F74652D314C50542D" +
            "6261613163613933373934393433656361623638616237376438646365643633");
        const int offset = 15;
        byte[] rlc0 = Convert.FromHexString("E903"); // 1001, LE
        byte[] rlc1 = Convert.FromHexString("2923"); // 9001, LE

        byte[] afterRow = RowPatcher.ApplyForward(insertRow, offset, rlc0, rlc1);
        var afterDecoded = RowDecoder.Decode(afterRow, LogTestSchema);
        Assert.Equal(1, afterDecoded["Id"]);
        Assert.Equal(9001, afterDecoded["Amount"]);
        Assert.Equal("note-1", afterDecoded["Note"]); // untouched by this diff

        byte[] roundTrippedBeforeRow = RowPatcher.ApplyBackward(afterRow, offset, rlc0, rlc1);
        Assert.Equal(insertRow, roundTrippedBeforeRow);
    }

    [Fact]
    public void VariableLengthGrowUpdate_AsymmetricLength_ForwardMatchesLiveValue_BackwardRoundTrips()
    {
        // Id=16 insert image, then an UPDATE that grows Note from "note-16"
        // (7 chars) to "grow-split-16-" + 170 'y's - before/after diff
        // lengths are 11 and 188 bytes respectively, not equal.
        byte[] insertRow = Convert.FromHexString(
            "300013001000000027406404294A0BF80300000500000200230047006E6F74652D31364C50542D39" +
            "31383761316632353466643432653539363135353031623731346635303162");
        const int offset = 24;
        byte[] rlc0 = Convert.FromHexString("230047006E6F74652D3136");
        byte[] rlc1 = Convert.FromHexString(
            "D400F80067726F772D73706C69742D31362D" +
            string.Concat(Enumerable.Repeat("79", 170)));

        byte[] afterRow = RowPatcher.ApplyForward(insertRow, offset, rlc0, rlc1);
        var afterDecoded = RowDecoder.Decode(afterRow, LogTestSchema);
        Assert.Equal(16, afterDecoded["Id"]);
        Assert.Equal("grow-split-16-" + new string('y', 170), afterDecoded["Note"]);

        byte[] roundTrippedBeforeRow = RowPatcher.ApplyBackward(afterRow, offset, rlc0, rlc1);
        Assert.Equal(insertRow, roundTrippedBeforeRow);

        var beforeDecoded = RowDecoder.Decode(roundTrippedBeforeRow, LogTestSchema);
        Assert.Equal("note-16", beforeDecoded["Note"]);
    }
}
