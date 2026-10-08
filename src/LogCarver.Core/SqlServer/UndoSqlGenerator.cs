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
    public static string? Generate(RowEvent evt, string tableName, string? identityColumnName = null) => evt.Kind switch
    {
        RowEventKind.Insert when evt.After is not null => SqlStatementBuilder.BuildDelete(evt.After, tableName),
        RowEventKind.Delete when evt.Before is not null => SqlStatementBuilder.BuildInsert(evt.Before, tableName, identityColumnName),
        RowEventKind.Update when evt.Before is not null && evt.After is not null =>
            SqlStatementBuilder.BuildUpdate(setValues: evt.Before, matchValues: evt.After, tableName, identityColumnName),
        _ => null,
    };
}
