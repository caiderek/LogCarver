namespace LogCarver.Core.SqlServer;

/// <summary>
/// Gives the compensation records of LOP_MODIFY_ROW the old bytes SQL Server does not log
/// for them.
///
/// When a transaction (or statement) is rolled back, SQL Server writes a compensation record
/// per undone change. For LOP_MODIFY_ROW, the one thing fn_dblog does NOT show on it is the
/// bytes it replaces: RowLog Contents 0 is an empty array and only the new bytes (Contents 1,
/// the restored value) and the span's length (Modify Size) are there. Everything downstream
/// splices a byte range as "replace Contents0.Length bytes at the offset by Contents1", so a
/// compensation record that replaced one byte was read as an INSERTION of one byte: the row
/// image grew by one byte from there on, and every later change to that row was decoded
/// against a shifted image (found for real 2026-10-18: after BEGIN TRAN; UPDATE; ROLLBACK, the
/// next same-length UPDATE of the row got an undo SQL like SET [Name] = N'other-db-val〱', with
/// no error anywhere).
///
/// What a compensation record replaces is exactly what the record it undoes wrote: the new
/// bytes (Contents 1) of the latest still-standing record of the same transaction at the same
/// page and slot. So that is what is filled in as its old bytes - but only when the pairing
/// checks out (same offset, and the compensation record restores exactly the bytes the original
/// replaced). Anything that does not check out is left as it was; nothing is guessed.
/// </summary>
public static class CompensationNormalizer
{
    /// <param name="recordsInLsnOrder">As read from fn_dblog, oldest first.</param>
    /// <returns>The same records, with the old bytes of paired LOP_MODIFY_ROW compensation records filled in.</returns>
    public static IReadOnlyList<LogRecord> FillCompensationOldBytes(IReadOnlyList<LogRecord> recordsInLsnOrder)
    {
        List<LogRecord>? result = null;
        // Per transaction: the records that changed a row and have not been compensated yet.
        var standing = new Dictionary<string, Stack<int>>();

        for (int i = 0; i < recordsInLsnOrder.Count; i++)
        {
            var record = recordsInLsnOrder[i];
            if (record.TransactionId is null || record.PageId is null || record.SlotId is null ||
                record.Operation is not ("LOP_INSERT_ROWS" or "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS" or "LOP_DELETE_ROWS"))
                continue;

            if (!standing.TryGetValue(record.TransactionId, out var stack))
                standing[record.TransactionId] = stack = new Stack<int>();

            if (!record.IsCompensation)
            {
                stack.Push(i);
                continue;
            }

            // A compensation record undoes the latest standing record, in reverse order. When it
            // is not the inverse of that one, the pairing is not understood: leave everything alone.
            if (stack.Count == 0)
                continue;
            IReadOnlyList<LogRecord> current = result ?? recordsInLsnOrder;
            var original = current[stack.Peek()];
            if (!IsInverse(original, record))
                continue;
            stack.Pop();

            if (record.Operation == "LOP_MODIFY_ROW" && original.Operation == "LOP_MODIFY_ROW" &&
                record.OffsetInRow == original.OffsetInRow &&
                IsEmpty(record.RowLogContents0) && !IsEmpty(original.RowLogContents1) &&
                SameBytes(record.RowLogContents1, original.RowLogContents0))
            {
                result ??= [.. recordsInLsnOrder];
                result[i] = record with { RowLogContents0 = original.RowLogContents1 };
            }
        }

        return result ?? recordsInLsnOrder;
    }

    private static bool IsInverse(LogRecord original, LogRecord compensation) =>
        original.PageId == compensation.PageId && original.SlotId == compensation.SlotId &&
        (original.Operation, compensation.Operation) is
            ("LOP_INSERT_ROWS", "LOP_DELETE_ROWS") or
            ("LOP_DELETE_ROWS", "LOP_INSERT_ROWS") or
            ("LOP_MODIFY_ROW", "LOP_MODIFY_ROW") or
            ("LOP_MODIFY_COLUMNS", "LOP_MODIFY_COLUMNS") or
            ("LOP_MODIFY_ROW", "LOP_MODIFY_COLUMNS") or
            ("LOP_MODIFY_COLUMNS", "LOP_MODIFY_ROW");

    private static bool IsEmpty(byte[]? bytes) => bytes is null || bytes.Length == 0;

    private static bool SameBytes(byte[]? a, byte[]? b) =>
        (a ?? []).AsSpan().SequenceEqual(b ?? []);
}
