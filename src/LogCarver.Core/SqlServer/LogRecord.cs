namespace LogCarver.Core.SqlServer;

/// <summary>One row from fn_dblog, narrowed to the columns RowDecoder needs.</summary>
/// <param name="PossiblyCorruptedOffsetsInRowLogContents0">
/// Always empty for a record read via fn_dblog (SQL Server's own
/// log-reading code has already fixed up anything an offline byte-level
/// reader would see corrupted). An offline reader (see
/// LogCarverOffline's LogRecordAdapter/PartitionScanner) populates this
/// with the specific byte offsets - relative to the start of
/// RowLogContents0 itself, directly usable as a position into the bytes
/// RowDecoder.Decode receives - where it detected a physical artifact it
/// can flag but not repair. Deliberately per-offset rather than a single
/// bool: a whole-record flag was tried first and turned out to mark
/// ~94% of a real customer table's events, since most rows are wide
/// enough to have some byte somewhere touch the artifact even when the
/// specific bytes that became a decoded value never did - see
/// research_notes.md and RowDecoder's use of this list to flag only the
/// specific column(s) actually affected.
/// </param>
/// <param name="PossiblyCorruptedOffsetsInRowLogContents1">
/// Same as <paramref name="PossiblyCorruptedOffsetsInRowLogContents0"/>,
/// for RowLogContents1. Only RowLogContents0 is ever decoded directly
/// into column values (LOP_INSERT_ROWS/LOP_DELETE_ROWS); RowLogContents1
/// only matters for LOP_MODIFY_ROW's byte-range splice
/// (RowPatcher.ApplyForward), where precise per-column attribution
/// through the splice arithmetic isn't implemented yet - a non-empty
/// list here currently only contributes to a coarser, whole-update note
/// via RowHistoryReconstructor, not a per-column one.
/// </param>
/// <param name="RowLogContents2">
/// For LOP_INSERT_ROWS, the lock information of the inserted row (for a
/// clustered-index row 26 bytes whose last 6 are the key hash). Other
/// operations carry something else here (the key, for LOP_MODIFY_ROW).
/// See RowIdentity.
/// </param>
/// <param name="RowLogContents3">
/// For LOP_MODIFY_ROW / LOP_MODIFY_COLUMNS, the lock information. For a row in
/// a clustered index (26 bytes) its last 6 bytes are the row's key hash - the
/// same value DBCC PAGE prints as KeyHashValue - which stays the same for
/// every INSERT/UPDATE of that key no matter which page or slot the row
/// currently sits in. See RowIdentity.
/// </param>
/// <param name="RowLogContents4">
/// Only LOP_MODIFY_COLUMNS uses it: the old and new bytes of each edit
/// (see ModifyColumnsPatcher). Null when the reader does not supply it
/// (the offline scanner does not), in which case a LOP_MODIFY_COLUMNS
/// record stays undecoded and is flagged for manual review.
/// </param>
public sealed record LogRecord(
    string Lsn,
    string Operation,
    string Context,
    int? OffsetInRow,
    string? AllocUnitName,
    string? PageId,
    int? SlotId,
    string? TransactionId,
    byte[]? RowLogContents0,
    byte[]? RowLogContents1,
    IReadOnlyList<int>? PossiblyCorruptedOffsetsInRowLogContents0 = null,
    IReadOnlyList<int>? PossiblyCorruptedOffsetsInRowLogContents1 = null,
    byte[]? RowLogContents4 = null,
    byte[]? RowLogContents3 = null,
    byte[]? RowLogContents2 = null);
