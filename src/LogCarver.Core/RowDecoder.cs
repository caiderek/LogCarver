using System.Text;

namespace LogCarver.Core;

/// <summary>
/// Decodes a SQL Server data row image (the raw bytes captured in
/// fn_dblog's "RowLog Contents 0/1", or read directly from a data page)
/// back into column values.
///
/// Row layout (verified byte-for-byte against a real INSERT record - see
/// SQL Server Log Parser 研究紀錄.txt 第八節):
///   byte 0      TagA (bit 0x10 = has null bitmap, bit 0x20 = has variable-length columns)
///   byte 1      TagB
///   byte 2-3    end offset of the fixed-length region (uint16 LE, includes the 4-byte header)
///   byte 4..    fixed-length columns, positioned by ColumnSchema.LeafOffset
///   +2 bytes    column count (the row's OWN count at write time - may be
///               fewer than the current schema's column count if columns
///               were added since; see the schema-drift warning below)
///   +ceil(colCount/8) bytes   null bitmap (bit = LeafNullBit - 1, 1 = NULL)
///   if has variable-length columns:
///     +2 bytes                variable-length column count (also the row's own count)
///     +N*2 bytes               cumulative end-offset array (uint16 LE)
///     ...                      variable-length data
///
/// IMPORTANT - schema drift: this decoder trusts the row's own embedded
/// column count, not the schema's column count, when sizing the null
/// bitmap and variable-length offset array. That prevents it from reading
/// past the end of an old row. It does NOT protect against decoding a
/// fixed-length column that was added after the row was written - a
/// caller must independently confirm the row's LSN does not predate a
/// schema-changing DDL (see the DDL boundary detection notes in the
/// research log) before trusting any fixed-length column's value.
///
/// Off-row/complex columns (e.g. varchar(max)/nvarchar(max) stored off-row):
/// the variable-length offset array marks these with bit 0x8000 on their
/// cumulative end-offset entry. Their in-row bytes are only a root pointer
/// into a separate LOB tree page/log-record format this decoder does not
/// parse - the column is decoded as the literal string
/// "&lt;off-row value, not decoded&gt;" rather than the real content, but the
/// rest of the row (every other column) still decodes normally. Before this
/// was handled, treating the pointer bytes as plain length-prefixed text
/// threw and silently dropped the entire row.
///
/// Row buffer shorter than its own offset array implies: the row's
/// embedded variable-length offset-array entry can point past the end of
/// the bytes this decoder actually received. One confirmed cause: fn_dblog's
/// own [RowLog Contents 0]/[RowLog Contents 1] columns truncate at a fixed
/// ~8000-byte cap (verified byte-for-byte - a row whose true size should be
/// ~8039 bytes per its own header/offset-array math arrived here at exactly
/// 8000). Also observed via the offline (direct-.ldf-read) path on a row
/// with no large column at all, so there is at least one other, not yet
/// identified cause - this decoder has no way to know which source (fn_dblog
/// vs. raw .ldf bytes) produced its input, so it never guesses a cause.
/// Decoded as "&lt;value's byte range exceeds what was captured for this
/// row - not decoded, see NeedsManualReview&gt;" for just that column (every
/// other column in the row still decodes normally) and flagged
/// NeedsManualReview, same remediation shape as the off-row case above -
/// strictly better than the prior behavior, where reading past the buffer
/// threw and took the entire row down via the outer catch's "likely a
/// compressed row" guess.
/// </summary>
public static class RowDecoder
{
    private const int TypeInt = 56;
    private const int TypeBigInt = 127;
    private const int TypeDate = 40;
    private const int TypeDateTime2 = 42;
    private const int TypeDecimal = 106;
    private const int TypeNumeric = 108;
    private const int TypeChar = 175;
    private const int TypeNVarChar = 231;
    private const int TypeNChar = 239;
    private const int TypeTinyInt = 48;
    private const int TypeSmallInt = 52;
    private const int TypeBit = 104;
    private const int TypeUniqueIdentifier = 36;
    private const int TypeMoney = 60;
    private const int TypeSmallMoney = 122;
    private const int TypeDateTime = 61;
    private const int TypeSmallDateTime = 58;
    private const int TypeFloat = 62;
    private const int TypeReal = 59;

    /// <summary>
    /// Decodes a row, first refusing (via <see cref="SchemaDriftException"/>)
    /// if <paramref name="recordLsn"/> predates any LSN in
    /// <paramref name="ddlBoundaryLsns"/>. Boundaries come from
    /// <c>LogCarver.Core.SqlServer.DdlBoundaryReader</c>; this overload
    /// takes plain LSN strings to keep the decoder itself independent of
    /// any SQL Server connectivity type.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Decode(
        ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema,
        string recordLsn, IReadOnlyList<string> ddlBoundaryLsns)
    {
        foreach (var boundaryLsn in ddlBoundaryLsns)
        {
            if (string.CompareOrdinal(recordLsn, boundaryLsn) < 0)
                throw new SchemaDriftException(recordLsn, boundaryLsn);
        }
        return Decode(row, schema);
    }

    /// <summary>
    /// Same as the four-argument overload, but additionally flags which
    /// specific columns' own bytes overlap <paramref name="possiblyCorruptedOffsets"/>
    /// (offsets into <paramref name="row"/> itself - e.g. from
    /// LogRecord.PossiblyCorruptedOffsetsInRowLogContents0). Only that
    /// column's value is suspect, not the whole row - every other column
    /// decodes and is trusted normally. Empty when nothing is flagged.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Decode(
        ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema,
        string recordLsn, IReadOnlyList<string> ddlBoundaryLsns,
        IReadOnlyList<int> possiblyCorruptedOffsets, out IReadOnlyList<string> possiblyCorruptedColumns)
    {
        foreach (var boundaryLsn in ddlBoundaryLsns)
        {
            if (string.CompareOrdinal(recordLsn, boundaryLsn) < 0)
                throw new SchemaDriftException(recordLsn, boundaryLsn);
        }
        return Decode(row, schema, possiblyCorruptedOffsets, out possiblyCorruptedColumns);
    }

    public static IReadOnlyDictionary<string, object?> Decode(ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema) =>
        Decode(row, schema, [], out _);

    public static IReadOnlyDictionary<string, object?> Decode(
        ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<int> possiblyCorruptedOffsets, out IReadOnlyList<string> possiblyCorruptedColumns)
    {
        try
        {
            var (values, corrupted) = DecodeCore(row, schema, possiblyCorruptedOffsets);
            possiblyCorruptedColumns = corrupted;
            return values;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Off-row LOB values no longer reach this catch (DecodeCore
            // detects the 0x8000 "complex column" offset-array flag and
            // marks just that column as not decoded - see the var-column
            // loop below), so a genuinely unsupported row format now means
            // something else, most likely a compressed row that reached
            // here despite the Cli's schema-level compression check. Either
            // way, surface it as a known-unsupported row format rather than
            // a raw indexing exception.
            throw new UnsupportedRowFormatException(
                "Row bytes don't fit the supported layout - likely a compressed row, which this decoder doesn't handle yet.",
                ex);
        }
    }

    private static bool OverlapsAny(IReadOnlyList<int> possiblyCorruptedOffsets, int start, int length)
    {
        if (length <= 0) return false;
        foreach (int offset in possiblyCorruptedOffsets)
        {
            if (offset >= start && offset < start + length) return true;
        }
        return false;
    }

    private static (IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string> PossiblyCorruptedColumns) DecodeCore(
        ReadOnlySpan<byte> row, IReadOnlyList<ColumnSchema> schema, IReadOnlyList<int> possiblyCorruptedOffsets)
    {
        byte tagA = row[0];
        bool hasVarCols = (tagA & 0x20) != 0;
        int fixedEnd = ReadUInt16LE(row, 2);

        var result = new Dictionary<string, object?>();
        HashSet<string>? possiblyCorruptedColumns = null;
        void FlagIfOverlapping(string columnName, int start, int length)
        {
            if (OverlapsAny(possiblyCorruptedOffsets, start, length))
                (possiblyCorruptedColumns ??= []).Add(columnName);
        }

        // colCount/nullBitmapStart only need fixedEnd (read directly from
        // the row's own header bytes above) - never any fixed column's
        // decoded value - so they're safe to compute before touching any
        // column, which is what makes the null check below possible.
        int colCount = ReadUInt16LE(row, fixedEnd);
        int nullBitmapBytes = (colCount + 7) / 8;
        int nullBitmapStart = fixedEnd + 2;
        int afterNullBitmap = nullBitmapStart + nullBitmapBytes;

        // Null check happens BEFORE decoding, not after (2026-09-30 fix - a
        // real customer row crashed here): a NULL fixed-length column's
        // bytes are reserved but meaningless, and an earlier version of
        // this loop decoded every fixed column unconditionally, then
        // overwrote NULL ones with null afterward - reasoning that garbage
        // bytes decoded into a value nobody keeps is harmless. That holds
        // for TypeInt/TypeBigInt (BitConverter never throws on any bit
        // pattern) but not for TypeDateTime2: DecodeDateTime2 calls
        // DateTime.AddDays on whatever day-count the garbage bytes happen
        // to contain, which threw ArgumentOutOfRangeException for a real
        // NULL EndTime column ([LOG].[JobRun]) whose reserved bytes decoded
        // to a day count outside DateTime's valid range - crashing the
        // entire row (every other column lost too) before the null
        // overwrite below ever ran. Checking null first and skipping the
        // decode switch entirely for a NULL column closes this for every
        // current and future fixed-length type, not just DATETIME2.
        // bit columns are packed, up to 8 per byte, sharing the same
        // LeafOffset - verified byte-for-byte against a real two-bit-column
        // row: both columns reported the identical LeafOffset, and their
        // values occupied bit 0 and bit 1 (LSB-first) of that one byte, in
        // ColumnId order. Precomputed once so the per-column loop below can
        // treat a bit column like any other fixed-offset column.
        Dictionary<string, int>? bitIndexByColumn = null;
        {
            var bitGroups = schema.Where(c => c.LeafOffset >= 0 && c.SystemTypeId == TypeBit)
                                   .GroupBy(c => c.LeafOffset);
            foreach (var group in bitGroups)
            {
                int i = 0;
                foreach (var bitCol in group.OrderBy(c => c.ColumnId))
                    (bitIndexByColumn ??= [])[bitCol.Name] = i++;
            }
        }

        foreach (var col in schema)
        {
            if (col.LeafOffset < 0) continue;
            bool isNull = IsColNull(row, nullBitmapStart, col.LeafNullBit);
            if (isNull)
            {
                result[col.Name] = null;
                continue;
            }
            result[col.Name] = col.SystemTypeId switch
            {
                TypeInt => BitConverter.ToInt32(row.Slice(col.LeafOffset, 4)),
                TypeBigInt => BitConverter.ToInt64(row.Slice(col.LeafOffset, 8)),
                TypeSmallInt => BitConverter.ToInt16(row.Slice(col.LeafOffset, 2)),
                TypeTinyInt => row[col.LeafOffset],
                TypeBit => ((row[col.LeafOffset] >> bitIndexByColumn![col.Name]) & 1) != 0,
                TypeUniqueIdentifier => new Guid(row.Slice(col.LeafOffset, 16)),
                TypeDate => DecodeDate(row, col.LeafOffset),
                TypeDateTime2 => DecodeDateTime2(row, col.LeafOffset, col.MaxLength, col.Scale),
                TypeDateTime => DecodeDateTime(row, col.LeafOffset),
                TypeSmallDateTime => DecodeSmallDateTime(row, col.LeafOffset),
                TypeDecimal or TypeNumeric => DecodeDecimal(row, col.LeafOffset, col.MaxLength, col.Scale),
                TypeMoney => BitConverter.ToInt64(row.Slice(col.LeafOffset, 8)) / 10000m,
                TypeSmallMoney => BitConverter.ToInt32(row.Slice(col.LeafOffset, 4)) / 10000m,
                TypeFloat => BitConverter.ToDouble(row.Slice(col.LeafOffset, 8)),
                TypeReal => BitConverter.ToSingle(row.Slice(col.LeafOffset, 4)),
                // char/nchar are fixed-length in-row, always stored padded
                // with spaces (0x20 / U+0020) out to the declared length -
                // decoded as-is, without trimming, to match what a live
                // SELECT of the column actually returns.
                TypeChar => Windows1252GetString(row.Slice(col.LeafOffset, col.MaxLength)),
                TypeNChar => Encoding.Unicode.GetString(row.Slice(col.LeafOffset, col.MaxLength)),
                _ => throw new NotSupportedException(
                    $"Column '{col.Name}': system_type_id {col.SystemTypeId} is not implemented yet."),
            };
            // The overlap check stays gated on non-null too - a NULL
            // column's reserved bytes are never actually read to produce
            // its value (we just skipped decoding them above), so a
            // boundary artifact landing there can't have corrupted
            // anything; flagging it anyway would reintroduce a smaller-
            // scale version of the same over-broad "flagged but nothing
            // that mattered was actually touched" problem the per-column
            // redesign exists to eliminate (see research_notes.md's "94%"
            // finding, at the whole-row level instead of one column).
            // TypeInt is always 4 bytes, TypeBigInt always 8, and TypeDate
            // always 3, regardless of the schema's own declared MaxLength
            // for any of them (none ever varies) - every other fixed-length
            // type here reads exactly col.MaxLength bytes, so that's the
            // right byte count to check for overlap in every other case.
            int fixedByteLength = col.SystemTypeId switch { TypeInt => 4, TypeBigInt => 8, TypeDate => 3, _ => col.MaxLength };
            FlagIfOverlapping(col.Name, col.LeafOffset, fixedByteLength);
        }

        if (hasVarCols)
        {
            // -1 = 1st variable-length column, -2 = 2nd, ... so ordering by
            // LeafOffset descending gives declaration order.
            var varCols = schema.Where(c => c.LeafOffset < 0)
                                 .OrderByDescending(c => c.LeafOffset)
                                 .ToList();
            int varColCount = ReadUInt16LE(row, afterNullBitmap);
            int offsetArrayStart = afterNullBitmap + 2;
            int dataStart = offsetArrayStart + varColCount * 2;

            int prevEnd = dataStart;
            for (int i = 0; i < varColCount; i++)
            {
                // The reserved-uniquifier sentinel (see SchemaReader's doc
                // comment) occupies its real position in varCols - LeafOffset
                // -1 sorts it first - so the physical slot numbering lines up
                // correctly for every REAL column after it, but it's never
                // itself something to decode or write a value for. Treating
                // it as "no column known for this slot" (same as the
                // i >= varCols.Count case below) is enough: prevEnd still
                // chains across it unconditionally further down, which is
                // all a slot with no real column needs to contribute.
                ColumnSchema? col = i < varCols.Count && varCols[i].Name != ColumnSchema.ReservedUniquifierSlotName
                    ? varCols[i] : null;
                int offsetEntryPos = offsetArrayStart + i * 2;

                // The row buffer this decoder receives can be shorter than
                // what its own embedded offset array implies. Confirmed
                // cause #1: fn_dblog's [RowLog Contents 0]/[RowLog Contents 1]
                // columns truncate at a fixed ~8000-byte cap (verified
                // byte-for-byte against a row whose un-truncated size should
                // be ~8039 bytes per its own header/offset-array math, but
                // arrived here at exactly 8000). There may be other causes
                // not yet identified - this decoder does not know which
                // source (online fn_dblog read vs. offline raw-.ldf scan)
                // produced its input, so the message below stays generic
                // and the column is flagged for manual review rather than
                // asserting a specific root cause. Either way, this is
                // strictly better than the old behavior: reading past here
                // used to throw and take the entire row down via the outer
                // catch's "likely a compressed row" guess.
                if (offsetEntryPos + 2 > row.Length)
                {
                    if (col is not null)
                    {
                        result[col.Name] = "<value's byte range exceeds what was captured for this row - not decoded, see NeedsManualReview>";
                        (possiblyCorruptedColumns ??= []).Add(col.Name);
                    }
                    continue;
                }
                int rawEndOffset = ReadUInt16LE(row, offsetEntryPos);

                // Bit 0x8000 on an offset-array entry is SQL Server's
                // "complex column" flag - it means this column's in-row
                // bytes are a pointer/root structure (e.g. an off-row LOB
                // root, 16 bytes observed, size independent of the LOB's
                // actual content length - verified byte-for-byte against a
                // real SQL Server instance with sp_tableoption 'large value
                // types out of row'), not length-prefixed character data.
                // A real cumulative offset can never reach 0x8000 (32768)
                // since SQL Server's max in-row record size is ~8060 bytes,
                // so this flag is unambiguous. The bit is a general row-
                // format mechanism (could in principle also mark a sparse
                // column-set representation), not exclusively LOB.
                bool isComplexColumn = (rawEndOffset & 0x8000) != 0;
                int endOffset = rawEndOffset & 0x7FFF;

                if (col is not null)
                {
                    // The offset-array entry itself is a real 2-byte value
                    // this decoder reads and trusts - flagging it isn't
                    // optional polish. This is exactly the mechanism behind
                    // the original real bug this whole feature exists for:
                    // DecisionFlag's entry (not its data bytes) was the one
                    // corrupted, computing a wildly wrong length that read
                    // 63 bytes into two unrelated downstream columns.
                    FlagIfOverlapping(col.Name, offsetEntryPos, 2);

                    if (isComplexColumn)
                    {
                        // Off-row/complex value: only a root pointer lives
                        // in-row, the actual content is in a separate LOB
                        // tree page/log-record format this decoder does not
                        // parse. Surface that honestly instead of slicing
                        // pointer bytes as if they were text (which used to
                        // compute a bogus length running past the row and
                        // crash, taking every other column in the row down
                        // with it - see the outer catch in Decode). Never
                        // observed alongside a genuinely NULL value for this
                        // column - a NULL LOB column has no off-row content
                        // to point to, so SQL Server either drops the whole
                        // variable-length section (no other var column has a
                        // value) or omits/zero-lengths its own offset-array
                        // entry via the ordinary null-bitmap path below,
                        // verified against real captured INSERT/UPDATE rows.
                        result[col.Name] = "<off-row value, not decoded>";
                    }
                    else if (prevEnd > row.Length || endOffset > row.Length)
                    {
                        // Same as the offset-entry-out-of-bounds case above,
                        // just caught one level later: the offset-array entry
                        // itself was still in bounds, but the data it points
                        // at (or the chained start position from a prior
                        // out-of-bounds column) runs past the bytes actually
                        // delivered to this decoder.
                        result[col.Name] = "<value's byte range exceeds what was captured for this row - not decoded, see NeedsManualReview>";
                        (possiblyCorruptedColumns ??= []).Add(col.Name);
                    }
                    else
                    {
                        bool isNull = IsColNull(row, nullBitmapStart, col.LeafNullBit);
                        int len = endOffset - prevEnd;
                        result[col.Name] = isNull
                            ? null
                            : len > 0
                                ? DecodeVarCharBytes(row.Slice(prevEnd, len), col.SystemTypeId)
                                : string.Empty; // len==0 and not null = legitimate empty string, not NULL
                        if (!isNull) FlagIfOverlapping(col.Name, prevEnd, len);
                    }
                }
                // Always chain from the masked offset - later columns'
                // offsets are cumulative from this position regardless of
                // whether this column itself was off-row.
                prevEnd = endOffset;
            }

            // Trailing variable-length columns omitted entirely from the row
            // (SQL Server drops the offset-array entry for a NULL column
            // that's last in declaration order) - verified in 研究紀錄 第九節.
            for (int i = varColCount; i < varCols.Count; i++)
            {
                if (varCols[i].Name != ColumnSchema.ReservedUniquifierSlotName)
                    result[varCols[i].Name] = null;
            }
        }

        return (result, (IReadOnlyList<string>?)possiblyCorruptedColumns?.ToList() ?? []);
    }

    private static int ReadUInt16LE(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.ToUInt16(bytes.Slice(offset, 2));

    private static bool IsColNull(ReadOnlySpan<byte> row, int nullBitmapStart, int nullBit)
    {
        if (nullBit <= 0) return false;
        int bitIndex = nullBit - 1;
        int byteIdx = bitIndex / 8;
        int bitInByte = bitIndex % 8;
        byte byteVal = row[nullBitmapStart + byteIdx];
        return ((byteVal >> bitInByte) & 1) != 0;
    }

    /// <summary>
    /// DATE is stored in-row as the same 3-byte little-endian "days since
    /// 0001-01-01" value as the date part of DATETIME2 (see
    /// <see cref="DecodeDateTime2"/>) - just without the trailing time
    /// portion, since DATE has no time component at all.
    /// </summary>
    private static DateTime DecodeDate(ReadOnlySpan<byte> row, int offset)
    {
        Span<byte> dateBuf = stackalloc byte[4];
        row.Slice(offset, 3).CopyTo(dateBuf);
        uint days = BitConverter.ToUInt32(dateBuf);
        return new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(days);
    }

    /// <summary>
    /// Legacy DATETIME (8 bytes), unrelated to DATETIME2's encoding -
    /// verified byte-for-byte against a real captured row: the FIRST 4
    /// bytes are a little-endian signed int counting 1/300-second ticks
    /// since midnight (SQL Server's well-known ~3.33ms rounding), the LAST
    /// 4 bytes are a little-endian signed int counting days since
    /// 1900-01-01 (negative for dates before 1900) - the sub-day component
    /// comes first, same ordering DATE/DATETIME2 use for their own day
    /// count relative to the time part.
    /// </summary>
    private static DateTime DecodeDateTime(ReadOnlySpan<byte> row, int offset)
    {
        int ticks = BitConverter.ToInt32(row.Slice(offset, 4));
        int days = BitConverter.ToInt32(row.Slice(offset + 4, 4));
        // Multiply before dividing - TicksPerSecond (10,000,000) isn't evenly
        // divisible by 300, so dividing first truncates to 33333 instead of
        // 33333.33 and accumulates a real, visible drift (~0.5s off at
        // ~14.8M ticks, caught by this test's own assertion).
        //
        // Round, don't truncate, that division (found for real 2026-10-08,
        // via an independent cross-check agent's testing turning up an
        // intermittent, 100ns-level restore mismatch on plain DATETIME
        // columns): 10,000,000/300 isn't an integer, so plain integer
        // division (which truncates toward zero) silently rounds DOWN
        // instead of to the nearest 100ns unit for most tick values - e.g.
        // 11 ticks should widen to SQL Server's own 366,667 .NET ticks
        // (confirmed via CAST(... AS DATETIME2(7)) against a real server),
        // but 11L * 10_000_000 / 300 truncates to 366,666, one 100ns unit
        // short. Invisible at millisecond-display precision (rounds the
        // same either way), but a WHERE clause comparing at full DATETIME2
        // precision - which 2026-10-08's own DATETIME2-precision fix now
        // does, deliberately - needs the exact value, not a value that's
        // merely millisecond-equivalent. Adding half the divisor before
        // truncating is the standard round-to-nearest trick; valid here
        // because a DATETIME's time-tick field is always non-negative.
        long preciseTicks = ((long)ticks * TimeSpan.TicksPerSecond + 150) / 300;
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .AddDays(days)
            .AddTicks(preciseTicks);
    }

    /// <summary>
    /// SMALLDATETIME (4 bytes) - verified byte-for-byte against a real
    /// captured row: FIRST 2 bytes are a little-endian uint16 counting
    /// minutes since midnight (no seconds - SMALLDATETIME's whole-minute
    /// precision), LAST 2 bytes are a little-endian uint16 counting days
    /// since 1900-01-01. Same sub-day-component-first ordering as DATETIME.
    /// </summary>
    private static DateTime DecodeSmallDateTime(ReadOnlySpan<byte> row, int offset)
    {
        ushort minutes = BitConverter.ToUInt16(row.Slice(offset, 2));
        ushort days = BitConverter.ToUInt16(row.Slice(offset + 2, 2));
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .AddDays(days)
            .AddMinutes(minutes);
    }

    /// <summary>
    /// DATETIME2(n)'s in-row time part is a little-endian integer counting
    /// units of 10^-n seconds since midnight - NOT always milliseconds, a
    /// mistake this decoder used to make for scale 4 (0.1ms units, not 1ms).
    /// Storage width for the time part depends on the declared scale, not
    /// just a single fixed width: scale 0-2 -&gt; 3 bytes, 3-4 -&gt; 4 bytes,
    /// 5-7 -&gt; 5 bytes, always followed by the same 3-byte day count
    /// DATE/DecodeDate use. Verified byte-for-byte against a real SQL
    /// Server instance at every scale 0-7 (e.g. scale 1's stored 452961
    /// means 45296.1s = 12:34:56.1; scale 7's stored 452961234567 means
    /// 45296.1234567s = 12:34:56.1234567) - previously only scale 3/4 (both
    /// sharing the 4-byte time width) had been checked, and only scale 3
    /// happened to be correct by coincidence of matching plain milliseconds.
    /// Real customer impact: DATETIME2 columns declared with no explicit
    /// scale default to DATETIME2(7) (8-byte storage, 5-byte time part) -
    /// e.g. DECISION.StockDecisionDaily.CreateDate - which this decoder
    /// used to refuse outright (`NotSupportedException`), taking the whole
    /// row down via RowHistoryReconstructor.TryDecode regardless of any
    /// other column's decodability.
    /// </summary>
    private static DateTime DecodeDateTime2(ReadOnlySpan<byte> row, int offset, int length, int scale)
    {
        int expectedTimeLen = scale switch
        {
            >= 0 and <= 2 => 3,
            3 or 4 => 4,
            >= 5 and <= 7 => 5,
            _ => throw new NotSupportedException($"DATETIME2 scale {scale} is out of the valid 0-7 range; refusing to decode."),
        };
        int timeLen = length - 3;
        if (timeLen != expectedTimeLen)
        {
            // The declared scale and the row's actual storage length
            // disagree - could mean a schema-drift scenario (scale changed
            // via ALTER COLUMN) this decoder doesn't specifically guard
            // for DATETIME2 the way it does for added/dropped columns.
            // Refuse rather than guess which one to trust.
            //
            // NOTE this only catches a scale change that crosses a byte-
            // width bucket (0-2 / 3-4 / 5-7). A scale change WITHIN the
            // same bucket (e.g. DATETIME2(3) ALTERed to DATETIME2(4), both
            // 4-byte time parts) is invisible here: the length check
            // passes, but an old row written under the old scale would be
            // decoded using the current (wrong) scale's tick unit -
            // exactly the silent off-by-a-power-of-10 bug this function
            // exists to prevent, just reachable through stale schema
            // metadata instead of a decode-logic gap. Not guarded against;
            // would need the same DDL-boundary-LSN mechanism callers use
            // for added/dropped columns, extended to cover ALTER COLUMN's
            // precision changes specifically - not yet built.
            throw new NotSupportedException(
                $"DATETIME2 storage length {length} doesn't match scale {scale}'s expected time-part width ({expectedTimeLen} bytes); refusing to decode.");
        }

        Span<byte> timeBuf = stackalloc byte[8];
        row.Slice(offset, timeLen).CopyTo(timeBuf);
        ulong rawTimeUnits = BitConverter.ToUInt64(timeBuf); // units of 10^-scale seconds since midnight

        Span<byte> dateBuf = stackalloc byte[4];
        row.Slice(offset + timeLen, 3).CopyTo(dateBuf);
        uint days = BitConverter.ToUInt32(dateBuf); // days since 0001-01-01

        // .NET DateTime ticks are 100ns (10^-7s) units - scale to that
        // resolution regardless of the column's own declared scale.
        long ticksPerUnit = (long)Math.Pow(10, 7 - scale);

        return new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .AddDays(days)
            .AddTicks((long)rawTimeUnits * ticksPerUnit);
    }

    /// <summary>
    /// DECIMAL/NUMERIC storage (verified byte-for-byte against real
    /// captured fn_dblog output for both a positive and a negative value):
    /// 1 sign byte (1 = positive, 0 = negative - the opposite of what you'd
    /// guess) followed by 4/8/12/16 bytes holding the unscaled magnitude as
    /// 1-4 little-endian uint32 "digit groups", least-significant group
    /// first, combined as one big base-2^32 integer. The actual value is
    /// that integer divided by 10^Scale.
    /// </summary>
    private static decimal DecodeDecimal(ReadOnlySpan<byte> row, int offset, int length, int scale)
    {
        int groupCount = (length - 1) / 4;
        if (length != 1 + groupCount * 4 || groupCount is < 1 or > 3)
        {
            // groupCount==4 (17-byte storage, precision 29-38) needs a
            // 128-bit magnitude, which does not fit System.Decimal's
            // 96-bit mantissa in general - refuse rather than truncate or
            // overflow silently. Not validated against real data at any
            // length outside 1-3 groups (precision 1-28).
            throw new NotSupportedException(
                $"DECIMAL/NUMERIC storage length {length} is not supported (precision outside 1-28); refusing to decode.");
        }

        bool positive = row[offset] != 0;
        Span<int> groups = stackalloc int[3];
        for (int i = 0; i < groupCount; i++)
            groups[i] = (int)BitConverter.ToUInt32(row.Slice(offset + 1 + i * 4, 4));

        return new decimal(groups[0], groups[1], groups[2], !positive, (byte)scale);
    }

    private static readonly Encoding Windows1252Encoding = CreateWindows1252();

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static string Windows1252GetString(ReadOnlySpan<byte> bytes) =>
        Windows1252Encoding.GetString(bytes);

    /// <summary>
    /// nvarchar stores UTF-16LE (2 bytes/char) in-row, unlike varchar's
    /// single-byte encoding. Decoding one as the other silently produces
    /// plausible-looking garbage (e.g. "first" -> "f i r s t", each letter
    /// followed by a stray null byte read as a control character) with no
    /// error - caught via a real nvarchar column while verifying the
    /// snapshot feature.
    /// </summary>
    private static string DecodeVarCharBytes(ReadOnlySpan<byte> bytes, int systemTypeId) =>
        systemTypeId == TypeNVarChar
            ? Encoding.Unicode.GetString(bytes)
            : Windows1252GetString(bytes);
}
