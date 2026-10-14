namespace LogCarver.Core.SqlServer;

/// <summary>
/// A row's image at a point in the log that the reader could not see the
/// INSERT for (for example an old row that is only being UPDATEd), recovered
/// from a known later image of the same row by undoing the logged changes
/// (see RowImageAnchoring). AsOfLsn is the LSN of the first logged change it
/// precedes; it plays the role of the "written at" LSN for the schema-drift
/// guard.
/// </summary>
public sealed record RowImageSeed(byte[] Bytes, string AsOfLsn);

/// <summary>
/// Which physical row a log record is about.
///
/// A clustered-index row is identified by its key hash (the last 6 bytes of
/// the 26-byte lock information - RowLog Contents 2 of an INSERT, RowLog
/// Contents 3 of an UPDATE; the same value DBCC PAGE shows as KeyHashValue). A
/// DELETE carries none. It is the same on every record of a key and
/// unaffected by page splits or by other rows being inserted into or removed
/// from the page, either of which moves a row to a different slot and used to
/// silently break the chain of a row's history. Rows with no such hash (heaps,
/// whose row ids do not move) fall back to (Page ID, Slot ID).
///
/// The hash is 48 bits: two different keys of one table can in principle share
/// it. That cannot silently produce a wrong value, because every UPDATE is
/// only applied after its logged old bytes have been verified against the
/// known image.
/// </summary>
public static class RowIdentity
{
    public static string Of(LogRecord record)
    {
        string? hash = KeyHash(LockInformation(record));
        return hash is not null
            ? $"H:{record.AllocUnitName}:{hash}"
            : $"P:{record.PageId}:{record.SlotId}";
    }

    /// <summary>Where a record keeps its lock information (see the class comment).</summary>
    public static byte[]? LockInformation(LogRecord record) =>
        record.Operation == "LOP_INSERT_ROWS" ? record.RowLogContents2 : record.RowLogContents3;

    /// <summary>Upper-case hex of the key hash, or null when the record carries none.</summary>
    public static string? KeyHash(byte[]? lockInformation) =>
        lockInformation is { Length: 26 } && lockInformation[0] == 0x01 && lockInformation[1] == 0x01 &&
        lockInformation[2] == 0x00 && lockInformation[3] == 0x0C
            ? Convert.ToHexString(lockInformation, 20, 6)
            : null;
}
