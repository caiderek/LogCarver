namespace LogCarver.Core.SqlServer;

/// <summary>
/// Generates the forward-direction SQL for one row event (研究紀錄 功能4: 日誌
/// 回放): before -&gt; after. Applying every event's replay statement in LSN
/// order against a copy of the table that currently matches its "before"
/// state reproduces the same sequence of changes - e.g. catching up a
/// restored backup, or reproducing an incident on a test copy. Exact
/// mirror of UndoSqlGenerator (after -&gt; before); see that type for the
/// shared WHERE-clause/optimistic-concurrency reasoning, which applies
/// here identically just in the opposite direction.
/// </summary>
public static class ReplaySqlGenerator
{
    /// <param name="identityColumnName">
    /// The table's identity column, if it has one, else null - see
    /// SqlStatementBuilder's own doc comment for why this matters.
    /// </param>
    /// <returns>The replay statement, or null if the event has no usable before/after image to work from (e.g. refused by the schema-drift guard).</returns>
    /// <remarks>
    /// Deliberately does not check <see cref="RowEvent.NeedsManualReview"/> -
    /// see UndoSqlGenerator's own doc comment, mirrored here.
    /// </remarks>
    public static string? Generate(RowEvent evt, string tableName, string? identityColumnName = null) => evt.Kind switch
    {
        RowEventKind.Insert when evt.After is not null => SqlStatementBuilder.BuildInsert(evt.After, tableName, identityColumnName),
        RowEventKind.Delete when evt.Before is not null => SqlStatementBuilder.BuildDelete(evt.Before, tableName),
        RowEventKind.Update when evt.Before is not null && evt.After is not null =>
            SqlStatementBuilder.BuildUpdate(setValues: evt.After, matchValues: evt.Before, tableName, identityColumnName),
        _ => null,
    };
}
