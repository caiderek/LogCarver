namespace LogCarver.Core;

/// <summary>
/// Applies a LOP_MODIFY_COLUMNS log record to a known row image.
///
/// Unlike LOP_MODIFY_ROW (one contiguous byte-range splice), SQL Server logs
/// an UPDATE whose changed bytes are not contiguous (e.g. a fixed-length
/// column plus a variable-length column, which also moves the variable-offset
/// array) as a list of edits. Format, reverse-engineered from controlled
/// UPDATEs on SQL Server 2025 (17.0) - every edit below was cross-checked
/// against the real before/after row images (the LOP_INSERT_ROWS and the
/// later LOP_DELETE_ROWS image of the same row):
///
///   n          = RowLogContents1.Length / 2
///   RLC0       = n pairs of uint16 (offset in the old row, offset in the
///                new row); the two differ once an earlier edit changed
///                the row's length
///   RLC1       = n uint16: the OLD length in bytes of each edit
///   RLC4       = for each edit, in order: the old bytes then the new bytes,
///                each block zero-padded up to a multiple of 4
///   (RLC2/RLC3 are key / lock information, not row data)
///
/// Edits are the first-to-last differing byte of a changed region, so an
/// unchanged byte can sit inside one (the two UTF-16 bytes of an unchanged
/// high byte, or a whole untouched column between two changed ones).
///
/// The new length of an edit is not stored. For every edit except the last
/// it follows from the offset pairs: the unchanged bytes between two edits
/// are the same in both rows, so
///   newLength[i] = newOffset[i+1] - newOffset[i] - (oldOffset[i+1] - oldOffset[i] - oldLength[i]).
/// The last edit's is recovered from the RLC4 remainder, which is only
/// known to within the 0-3 bytes of padding, so it is resolved by requiring
/// the patched row to be self-consistent (its length equals what its own
/// variable-offset array says). Anything that does not line up exactly -
/// old bytes differing from the known image, offsets out of range, zero or
/// several self-consistent candidates - is refused: a silently wrong row is
/// the one unacceptable failure mode here (see RowHistoryReconstructor).
/// </summary>
public static class ModifyColumnsPatcher
{
    /// <summary>
    /// Returns true and the after-row on success; otherwise false with a
    /// human-readable reason in <paramref name="failure"/> and a null row.
    /// </summary>
    public static bool TryApplyForward(
        ReadOnlySpan<byte> beforeRow,
        ReadOnlySpan<byte> rowLogContents0,
        ReadOnlySpan<byte> rowLogContents1,
        ReadOnlySpan<byte> rowLogContents4,
        out byte[]? afterRow,
        out string? failure)
    {
        afterRow = null;
        failure = null;

        int n = rowLogContents1.Length / 2;
        if (n == 0 || rowLogContents1.Length != n * 2 || rowLogContents0.Length != n * 4)
        {
            failure = "unrecognised LOP_MODIFY_COLUMNS layout (RowLog Contents 0/1 sizes do not match)";
            return false;
        }

        var offsets = new int[n];
        var newOffsets = new int[n];
        var oldLengths = new int[n];
        var newLengths = new int[n]; // the last one is filled in below
        for (int i = 0; i < n; i++)
        {
            offsets[i] = ReadU16(rowLogContents0, i * 4);
            newOffsets[i] = ReadU16(rowLogContents0, i * 4 + 2);
            oldLengths[i] = ReadU16(rowLogContents1, i * 2);

            int previousEnd = i == 0 ? 0 : offsets[i - 1] + oldLengths[i - 1];
            if (offsets[i] < previousEnd || offsets[i] + oldLengths[i] > beforeRow.Length)
            {
                failure = "LOP_MODIFY_COLUMNS edit lies outside the known row image";
                return false;
            }
        }

        if (newOffsets[0] != offsets[0])
        {
            failure = "LOP_MODIFY_COLUMNS first edit has different old/new offsets";
            return false;
        }

        for (int i = 0; i < n - 1; i++)
        {
            int unchangedGap = offsets[i + 1] - (offsets[i] + oldLengths[i]);
            newLengths[i] = newOffsets[i + 1] - newOffsets[i] - unchangedGap;
            if (newLengths[i] < 0)
            {
                failure = "LOP_MODIFY_COLUMNS offsets are inconsistent";
                return false;
            }
        }

        // Walk RLC4: old/new blocks per edit (see the type's doc comment).
        var newBlocks = new int[n]; // start of each edit's new bytes in RLC4
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            int oldPadded = Pad4(oldLengths[i]);
            if (pos + oldPadded > rowLogContents4.Length)
            {
                failure = "LOP_MODIFY_COLUMNS RowLog Contents 4 is shorter than its edits";
                return false;
            }

            if (!rowLogContents4.Slice(pos, oldLengths[i]).SequenceEqual(beforeRow.Slice(offsets[i], oldLengths[i])))
            {
                failure = "LOP_MODIFY_COLUMNS old bytes do not match the known before image";
                return false;
            }
            pos += oldPadded;
            newBlocks[i] = pos;
            if (i < n - 1)
                pos += Pad4(newLengths[i]);
        }

        int lastRemaining = rowLogContents4.Length - pos;
        if (lastRemaining < 0 || lastRemaining % 4 != 0)
        {
            failure = "LOP_MODIFY_COLUMNS RowLog Contents 4 size is not 4-byte aligned";
            return false;
        }

        // Candidate new lengths for the last edit: any value whose 4-byte
        // padding equals lastRemaining.
        var candidates = new List<byte[]>();
        int lo = lastRemaining == 0 ? 0 : lastRemaining - 3;
        int hi = lastRemaining;
        for (int lastNewLength = lo; lastNewLength <= hi; lastNewLength++)
        {
            newLengths[n - 1] = lastNewLength;
            var row = Build(beforeRow, offsets, oldLengths, newLengths, rowLogContents4, newBlocks);
            if (IsSelfConsistent(row))
                candidates.Add(row);
        }

        if (candidates.Count != 1)
        {
            failure = candidates.Count == 0
                ? "LOP_MODIFY_COLUMNS patched row is not self-consistent - refusing to guess"
                : "LOP_MODIFY_COLUMNS patched row is ambiguous - refusing to guess";
            return false;
        }

        afterRow = candidates[0];
        return true;
    }

    private static byte[] Build(
        ReadOnlySpan<byte> before, int[] offsets, int[] oldLengths, int[] newLengths,
        ReadOnlySpan<byte> rlc4, int[] newBlocks)
    {
        int n = offsets.Length;
        var result = new List<byte>(before.Length + 64);
        int cursor = 0;
        for (int i = 0; i < n; i++)
        {
            for (; cursor < offsets[i]; cursor++)
                result.Add(before[cursor]);

            for (int j = 0; j < newLengths[i]; j++)
                result.Add(rlc4[newBlocks[i] + j]);
            cursor = offsets[i] + oldLengths[i];
        }
        for (; cursor < before.Length; cursor++)
            result.Add(before[cursor]);
        return result.ToArray();
    }

    /// <summary>
    /// A row is self-consistent when its length is exactly what its own
    /// header says: the last variable-offset-array entry (plus the 14-byte
    /// versioning tag when status bit 0x40 is set), or - without variable
    /// columns - the fixed part plus the null bitmap.
    /// </summary>
    internal static bool IsSelfConsistent(ReadOnlySpan<byte> row)
    {
        if (row.Length < 4)
            return false;

        byte status = row[0];
        int fixedLength = ReadU16(row, 2);
        if (fixedLength < 4 || fixedLength + 2 > row.Length)
            return false;

        int columnCount = ReadU16(row, fixedLength);
        int bitmapBytes = (columnCount + 7) / 8;
        int afterBitmap = fixedLength + 2 + bitmapBytes;
        int expected;

        if ((status & 0x20) != 0)
        {
            if (afterBitmap + 2 > row.Length)
                return false;
            int variableCount = ReadU16(row, afterBitmap);
            int arrayEnd = afterBitmap + 2 + variableCount * 2;
            if (variableCount == 0)
                expected = afterBitmap + 2;
            else
            {
                if (arrayEnd > row.Length)
                    return false;
                expected = ReadU16(row, arrayEnd - 2);
            }
        }
        else
        {
            expected = afterBitmap;
        }

        if ((status & 0x40) != 0)
            expected += 14;

        return expected == row.Length;
    }

    private static int Pad4(int length) => (length + 3) & ~3;

    private static int ReadU16(ReadOnlySpan<byte> bytes, int index) => bytes[index] | (bytes[index + 1] << 8);
}
