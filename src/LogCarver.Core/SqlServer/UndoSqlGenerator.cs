namespace LogCarver.Core.SqlServer;

/// <summary>
/// Generates suggested SQL to reverse one row event (研究紀錄 功能3: Undo SQL
/// 產生器): after -&gt; before. This is output for a human to review before
/// running - LogCarver never executes anything itself. See
/// ReplaySqlGenerator for the forward-direction mirror of this.
///
/// The WHERE clause always matches every column of the row's most
/// recently observed state, not just a primary key (a real customer table
/// may not have a clean one). This doubles as an optimistic-concurrency
/// guard: if the row changed after LogCarver observed it, the WHERE won't
/// match and the statement becomes a safe no-op instead of silently
/// overwriting whatever changed it since.
/// </summary>
public static class UndoSqlGenerator
{
    /// <param name="identityColumnName">
    /// The table's identity column, if it has one, else null - see
    /// SqlStatementBuilder's own doc comment for why this matters (an
    /// UPDATE's undo must never try to SET it; a DELETE's undo, which
    /// re-INSERTs the row, needs it wrapped in IDENTITY_INSERT to restore
    /// the original value instead of getting a new one assigned).
    /// </param>
    /// <returns>The undo statement, or null if the event has no usable before/after image to work from (e.g. refused by the schema-drift guard).</returns>
    /// <remarks>
    /// Deliberately does NOT check <see cref="RowEvent.NeedsManualReview"/> -
    /// this is output for a human to review before running (see this
    /// class's own doc comment), and the CLI's existing "print a NEEDS
    /// MANUAL REVIEW comment above the SQL, let the human judge it" design
    /// already handles that review. An AUTOMATED caller with no human in the loop
    /// must not reuse this best-effort text as-is: it should refuse instead of
    /// executing SQL that may embed a placeholder in place of real data.
    /// </remarks>
    /// <param name="keyColumns">
    /// The table's primary (or unique clustered) key columns, if known. With
    /// them an UPDATE is undone at COLUMN level: only the columns this update
    /// changed are set back, and the row is found by its key plus those
    /// columns' values after the update. Another writer changing some other
    /// column of the row since then no longer turns the undo into a no-op,
    /// and is not overwritten either. A later change to one of the columns
    /// this update changed is still a conflict (the WHERE does not match).
    /// Without a key, or if the key itself was changed, the whole row is
    /// matched as before.
    /// </param>
    public static string? Generate(
        RowEvent evt, string tableName, string? identityColumnName = null, IReadOnlyList<string>? keyColumns = null) => evt.Kind switch
    {
        RowEventKind.Insert when evt.After is not null => SqlStatementBuilder.BuildDelete(evt.After, tableName),
        RowEventKind.Delete when evt.Before is not null => SqlStatementBuilder.BuildInsert(evt.Before, tableName, identityColumnName),
        RowEventKind.Update when evt.Before is not null && evt.After is not null =>
            ColumnLevelUpdate(evt.Before, evt.After, keyColumns) is var (setValues, matchValues)
                ? SqlStatementBuilder.BuildUpdate(setValues, matchValues, tableName, identityColumnName)
                : SqlStatementBuilder.BuildUpdate(setValues: evt.Before, matchValues: evt.After, tableName, identityColumnName),
        _ => null,
    };

    private static (Dictionary<string, object?> Set, Dictionary<string, object?> Match)? ColumnLevelUpdate(
        IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after, IReadOnlyList<string>? keyColumns)
    {
        if (keyColumns is not { Count: > 0 })
            return null;

        var changed = before.Keys.Where(c => after.ContainsKey(c) && !SameValue(before[c], after[c])).ToList();
        if (changed.Count == 0)
            return null;

        // Every key column must be known on both sides and must not itself
        // be one of the changed columns.
        foreach (var key in keyColumns)
        {
            if (!before.TryGetValue(key, out var b) || !after.TryGetValue(key, out var a) || !SameValue(b, a))
                return null;
        }

        var set = changed.ToDictionary(c => c, c => before[c]);
        var match = new Dictionary<string, object?>();
        foreach (var key in keyColumns) match[key] = after[key];
        foreach (var column in changed) match[column] = after[column];
        return (set, match);
    }

    private static bool SameValue(object? x, object? y) => (x, y) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (byte[] bx, byte[] by) => bx.AsSpan().SequenceEqual(by),
        _ => x.Equals(y),
    };
}
