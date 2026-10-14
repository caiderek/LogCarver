namespace LogCarver.Core.SqlServer;

/// <summary>
/// Record-type facts about physical row images that the reconstructor needs
/// for heap tables, where a row that outgrows its page is moved ("forwarded").
///
/// Status byte 0 bits 1-3 are the record type: 0 = primary record, 1 =
/// forwarded record (the moved row, followed by 10 bytes - 2 unknown, then
/// page u32, file u16, slot u16 of the place it came from), 2 = forwarding
/// stub (9 bytes left at the original place: status, then page u32, file u16,
/// slot u16 of where the row went). Checked against real heap logs on SQL
/// Server 2025.
/// </summary>
public static class RowFormat
{
    public static int RecordType(ReadOnlySpan<byte> row) => row.Length > 0 ? (row[0] >> 1) & 0x7 : -1;

    public static bool IsPrimaryRecord(ReadOnlySpan<byte> row) => row.Length > 4 && RecordType(row) == 0;

    public static bool IsForwardingStub(ReadOnlySpan<byte> row) => row.Length == 9 && RecordType(row) == 2;

    public static bool IsForwardedRecord(ReadOnlySpan<byte> row) => row.Length > 14 && RecordType(row) == 1;

    /// <summary>
    /// The place (fn_dblog page id and slot) a forwarded record was moved from.
    /// </summary>
    public static bool TryGetForwardedFrom(ReadOnlySpan<byte> forwardedRecord, out string pageId, out int slot)
    {
        pageId = "";
        slot = 0;
        if (!IsForwardedRecord(forwardedRecord))
            return false;

        var tail = forwardedRecord[^8..];
        uint page = BitConverter.ToUInt32(tail[..4]);
        ushort file = BitConverter.ToUInt16(tail.Slice(4, 2));
        slot = BitConverter.ToUInt16(tail.Slice(6, 2));
        pageId = $"{file:x4}:{page:x8}";
        return true;
    }
}
