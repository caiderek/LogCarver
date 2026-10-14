namespace LogCarver.Core;

/// <summary>
/// Applies a LOP_MODIFY_COLUMNS log record to a known row image, in either
/// direction.
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
/// high byte, or a whole untouched column between two changed ones). They
/// are NOT whole column values: a one-byte change to an int is logged as one
/// byte, so the record alone cannot be decoded without the rest of the row.
///
/// The new length of an edit is not stored. For every edit except the last
/// it follows from the offset pairs: the unchanged bytes between two edits
/// are the same in both rows, so
///   newLength[i] = newOffset[i+1] - newOffset[i] - (oldOffset[i+1] - oldOffset[i] - oldLength[i]).
/// The last edit's is recovered from the RLC4 remainder, which is only
/// known to within the 0-3 bytes of padding, so it is resolved by requiring
/// the resulting row to be self-consistent (its length equals what its own
/// variable-offset array says). Anything that does not line up exactly -
/// old (or, going backward, new) bytes differing from the known image,
/// offsets out of range, zero or several self-consistent candidates - is
/// refused: a silently wrong row is the one unacceptable failure mode here
/// (see RowHistoryReconstructor).
/// </summary>
public static class ModifyColumnsPatcher
{
    private sealed class Edits
    {
        public required int[] OldOffsets { get; init; }
        public required int[] NewOffsets { get; init; }
        public required int[] OldLengths { get; init; }
        /// <summary>Known for every edit except the last (filled per candidate).</summary>
        public required int[] NewLengths { get; init; }
        /// <summary>Start of each edit's old bytes in RLC4.</summary>
        public required int[] OldBlocks { get; init; }
        /// <summary>Start of each edit's new bytes in RLC4.</summary>
        public required int[] NewBlocks { get; init; }
        /// <summary>Bytes of RLC4 left for the last edit's new block (a multiple of 4).</summary>
        public required int LastNewBlockSize { get; init; }
        public int Count => OldOffsets.Length;
    }

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
        if (!TryParse(rowLogContents0, rowLogContents1, rowLogContents4, out var edits, out failure))
            return false;

        int n = edits!.Count;
        for (int i = 0; i < n; i++)
        {
            int previousEnd = i == 0 ? 0 : edits.OldOffsets[i - 1] + edits.OldLengths[i - 1];
            if (edits.OldOffsets[i] < previousEnd || edits.OldOffsets[i] + edits.OldLengths[i] > beforeRow.Length)
            {
                failure = "LOP_MODIFY_COLUMNS edit lies outside the known row image";
                return false;
            }

            if (!rowLogContents4.Slice(edits.OldBlocks[i], edits.OldLengths[i])
                    .SequenceEqual(beforeRow.Slice(edits.OldOffsets[i], edits.OldLengths[i])))
            {
                failure = "LOP_MODIFY_COLUMNS old bytes do not match the known before image";
                return false;
            }
        }

        var candidates = new List<byte[]>();
        foreach (int lastNewLength in LastLengthCandidates(edits.LastNewBlockSize))
        {
            edits.NewLengths[n - 1] = lastNewLength;
            var row = Rebuild(beforeRow, edits.OldOffsets, edits.OldLengths, edits.NewLengths, rowLogContents4, edits.NewBlocks);
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

    /// <summary>
    /// The reverse of <see cref="TryApplyForward"/>: given the row as it was
    /// AFTER the update, returns the row as it was before. Every edit's new
    /// bytes must be present in <paramref name="afterRow"/> exactly where the
    /// record says, and the result must round-trip back to
    /// <paramref name="afterRow"/> through <see cref="TryApplyForward"/>.
    /// </summary>
    public static bool TryApplyBackward(
        ReadOnlySpan<byte> afterRow,
        ReadOnlySpan<byte> rowLogContents0,
        ReadOnlySpan<byte> rowLogContents1,
        ReadOnlySpan<byte> rowLogContents4,
        out byte[]? beforeRow,
        out string? failure)
    {
        beforeRow = null;
        if (!TryParse(rowLogContents0, rowLogContents1, rowLogContents4, out var edits, out failure))
            return false;

        int n = edits!.Count;
        var candidates = new List<byte[]>();
        foreach (int lastNewLength in LastLengthCandidates(edits.LastNewBlockSize))
        {
            edits.NewLengths[n - 1] = lastNewLength;
            if (!NewBytesMatch(afterRow, edits, rowLogContents4))
                continue;

            var before = Rebuild(afterRow, edits.NewOffsets, edits.NewLengths, edits.OldLengths, rowLogContents4, edits.OldBlocks);
            if (!IsSelfConsistent(before))
                continue;

            // Round trip: applying the record forward must give back exactly
            // the row we started from.
            if (TryApplyForward(before, rowLogContents0, rowLogContents1, rowLogContents4, out var again, out _) &&
                again!.AsSpan().SequenceEqual(afterRow))
                candidates.Add(before);
        }

        if (candidates.Count != 1)
        {
            failure = candidates.Count == 0
                ? "LOP_MODIFY_COLUMNS new bytes do not match the known after image"
                : "LOP_MODIFY_COLUMNS reversed row is ambiguous - refusing to guess";
            return false;
        }

        beforeRow = candidates[0];
        return true;
    }

    private static bool NewBytesMatch(ReadOnlySpan<byte> afterRow, Edits edits, ReadOnlySpan<byte> rlc4)
    {
        int previousEnd = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            int start = edits.NewOffsets[i];
            int length = edits.NewLengths[i];
            if (start < previousEnd || start + length > afterRow.Length)
                return false;
            if (!rlc4.Slice(edits.NewBlocks[i], length).SequenceEqual(afterRow.Slice(start, length)))
                return false;
            previousEnd = start + length;
        }
        return true;
    }

    private static bool TryParse(
        ReadOnlySpan<byte> rlc0, ReadOnlySpan<byte> rlc1, ReadOnlySpan<byte> rlc4,
        out Edits? edits, out string? failure)
    {
        edits = null;
        failure = null;

        int n = rlc1.Length / 2;
        if (n == 0 || rlc1.Length != n * 2 || rlc0.Length != n * 4)
        {
            failure = "unrecognised LOP_MODIFY_COLUMNS layout (RowLog Contents 0/1 sizes do not match)";
            return false;
        }

        var oldOffsets = new int[n];
        var newOffsets = new int[n];
        var oldLengths = new int[n];
        var newLengths = new int[n]; // the last one is filled in per candidate
        for (int i = 0; i < n; i++)
        {
            oldOffsets[i] = ReadU16(rlc0, i * 4);
            newOffsets[i] = ReadU16(rlc0, i * 4 + 2);
            oldLengths[i] = ReadU16(rlc1, i * 2);
        }

        if (newOffsets[0] != oldOffsets[0])
        {
            failure = "LOP_MODIFY_COLUMNS first edit has different old/new offsets";
            return false;
        }

        for (int i = 0; i < n - 1; i++)
        {
            int unchangedGap = oldOffsets[i + 1] - (oldOffsets[i] + oldLengths[i]);
            newLengths[i] = newOffsets[i + 1] - newOffsets[i] - unchangedGap;
            if (unchangedGap < 0 || newLengths[i] < 0)
            {
                failure = "LOP_MODIFY_COLUMNS offsets are inconsistent";
                return false;
            }
        }

        // Walk RLC4: old/new blocks per edit.
        var oldBlocks = new int[n];
        var newBlocks = new int[n];
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            oldBlocks[i] = pos;
            pos += Pad4(oldLengths[i]);
            newBlocks[i] = pos;
            if (i < n - 1)
                pos += Pad4(newLengths[i]);
            if (pos > rlc4.Length)
            {
                failure = "LOP_MODIFY_COLUMNS RowLog Contents 4 is shorter than its edits";
                return false;
            }
        }

        int remaining = rlc4.Length - pos;
        if (remaining % 4 != 0)
        {
            failure = "LOP_MODIFY_COLUMNS RowLog Contents 4 size is not 4-byte aligned";
            return false;
        }

        edits = new Edits
        {
            OldOffsets = oldOffsets, NewOffsets = newOffsets, OldLengths = oldLengths, NewLengths = newLengths,
            OldBlocks = oldBlocks, NewBlocks = newBlocks, LastNewBlockSize = remaining,
        };
        return true;
    }

    /// <summary>Lengths whose 4-byte padding equals the block size.</summary>
    private static IEnumerable<int> LastLengthCandidates(int blockSize)
    {
        int low = blockSize == 0 ? 0 : blockSize - 3;
        for (int length = low; length <= blockSize; length++)
            yield return length;
    }

    /// <summary>
    /// Replaces each [offsets[i], offsets[i] + replacedLengths[i]) region of
    /// <paramref name="source"/> with insertedLengths[i] bytes taken from rlc4.
    /// </summary>
    private static byte[] Rebuild(
        ReadOnlySpan<byte> source, int[] offsets, int[] replacedLengths, int[] insertedLengths,
        ReadOnlySpan<byte> rlc4, int[] insertedBlockStarts)
    {
        var result = new List<byte>(source.Length + 64);
        int cursor = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            for (; cursor < offsets[i]; cursor++)
                result.Add(source[cursor]);
            for (int j = 0; j < insertedLengths[i]; j++)
                result.Add(rlc4[insertedBlockStarts[i] + j]);
            cursor = offsets[i] + replacedLengths[i];
        }
        for (; cursor < source.Length; cursor++)
            result.Add(source[cursor]);
        return result.ToArray();
    }

    /// <summary>
    /// A row is self-consistent when its length is exactly what its own
    /// header says: the last variable-offset-array entry (plus the 14-byte
    /// versioning tag when status bit 0x40 is set), or - without variable
    /// columns - the fixed part plus the null bitmap.
    /// </summary>
    public static bool IsSelfConsistent(ReadOnlySpan<byte> row)
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

        // A forwarded heap record (status type 1) is followed by the 10-byte
        // pointer back to where it came from.
        if (((status >> 1) & 0x7) == 1)
            expected += 10;

        return expected == row.Length;
    }

    private static int Pad4(int length) => (length + 3) & ~3;

    private static int ReadU16(ReadOnlySpan<byte> bytes, int index) => bytes[index] | (bytes[index + 1] << 8);
}
