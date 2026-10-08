namespace LogCarver.Core.SqlServer;

/// <summary>
/// Shared plumbing for UndoSqlGenerator and ReplaySqlGenerator - the two
/// are mirror images of each other (undo: after -&gt; before, replay:
/// before -&gt; after), so the statement-building and literal-formatting
/// logic lives here once instead of twice.
///
/// IDENTITY columns (found for real 2026-10-08, via an independent
/// cross-check agent testing LogCarverGuard against a table using SQL
/// Server's ordinary default IDENTITY-PK pattern - every one of this
/// project's own test fixtures up to that point happened to use an
/// explicitly-assigned PK instead, so this was invisible until an outside
/// tester hit it on the very first try): SQL Server rejects outright any
/// UPDATE that tries to SET an identity column, even to its own current
/// value (error 8102) - and separately rejects an INSERT that names an
/// identity column in its column list unless IDENTITY_INSERT is ON for
/// that table. <paramref name="identityColumnName"/> on BuildUpdate/
/// BuildInsert below handles each: an UPDATE never needs to touch it at
/// all (a row's own identity value can never actually change, so omitting
/// it from SET loses nothing), while re-INSERTing a deleted row (undoing
/// a DELETE) genuinely needs its original identity value restored, which
/// requires the IDENTITY_INSERT wrap.
/// </summary>
internal static class SqlStatementBuilder
{
    /// <param name="identityColumnName">
    /// The table's identity column, if it has one and it's present in
    /// <paramref name="values"/>, else null. When given, the INSERT is
    /// wrapped in SET IDENTITY_INSERT ON/OFF so the original value can be
    /// restored exactly, instead of SQL Server silently generating a new
    /// one (or rejecting the statement outright) - see this class's own
    /// doc comment.
    /// </param>
    public static string BuildInsert(IReadOnlyDictionary<string, object?> values, string tableName, string? identityColumnName = null)
    {
        string columns = string.Join(", ", values.Keys.Select(EscapeIdentifier));
        string literals = string.Join(", ", values.Values.Select(FormatSqlLiteral));
        string escapedTable = EscapeIdentifier(tableName);
        string insert = $"INSERT INTO {escapedTable} ({columns}) VALUES ({literals});";
        return identityColumnName is not null && values.ContainsKey(identityColumnName)
            ? $"SET IDENTITY_INSERT {escapedTable} ON; {insert} SET IDENTITY_INSERT {escapedTable} OFF;"
            : insert;
    }

    public static string BuildDelete(IReadOnlyDictionary<string, object?> matchValues, string tableName) =>
        $"DELETE FROM {EscapeIdentifier(tableName)} WHERE {BuildWhereClause(matchValues)};";

    /// <param name="setValues">The state to write.</param>
    /// <param name="matchValues">
    /// The state to match in WHERE - every column, not just a primary key
    /// (a real table may not have a clean one). This also serves as an
    /// optimistic-concurrency guard: if the row has since changed away
    /// from this state, the WHERE won't match and the statement becomes a
    /// safe no-op instead of silently overwriting it.
    /// </param>
    /// <param name="identityColumnName">
    /// The table's identity column, if it has one, else null. Excluded
    /// from the SET clause entirely (never from WHERE, where matching on
    /// it is still valid) - see this class's own doc comment for why an
    /// UPDATE never needs to, and in fact cannot, touch this column.
    /// </param>
    public static string BuildUpdate(
        IReadOnlyDictionary<string, object?> setValues, IReadOnlyDictionary<string, object?> matchValues, string tableName,
        string? identityColumnName = null)
    {
        string setClause = string.Join(", ", setValues
            .Where(kv => kv.Key != identityColumnName)
            .Select(kv => $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}"));
        return $"UPDATE {EscapeIdentifier(tableName)} SET {setClause} WHERE {BuildWhereClause(matchValues)};";
    }

    private static string BuildWhereClause(IReadOnlyDictionary<string, object?> row) =>
        string.Join(" AND ", row.Select(kv => kv.Value is null
            ? $"{EscapeIdentifier(kv.Key)} IS NULL"
            : $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}"));

    // Bracket-quotes each dot-separated part of an identifier (schema.table
    // or a bare column name) and doubles up any embedded "]" - the SQL
    // Server quoted-identifier escaping rule. Column names come from
    // sys.columns metadata rather than a free-text source, but nothing
    // stops a table from having one that contains SQL syntax characters,
    // and this text is meant to be reviewed and run by a human rather than
    // executed by LogCarver itself - an unescaped identifier would let such
    // a column name inject extra statements into what looks like a plain
    // suggestion.
    //
    // The table name specifically can arrive here ALREADY bracket-quoted -
    // the CLI's tableName argument is whatever the caller typed, and typing
    // "[dbo].[Orders]" is completely ordinary (it's exactly what SSMS's own
    // "Script Table as" produces). Re-escaping an already-bracketed part
    // without accounting for that doubled every bracket - "[dbo].[Orders]"
    // came out as "[[dbo]]].[[Orders]]]", found via real customer testing
    // 2026-09-30. Stripping one pre-existing layer of brackets (and
    // reversing their "]]" -> "]" escaping) before re-escaping makes this
    // idempotent: an already-bracketed part round-trips to the same
    // brackets, and an unbracketed part is escaped exactly as before.
    private static string EscapeIdentifier(string name) =>
        string.Join(".", name.Split('.').Select(part =>
        {
            string raw = part.Length >= 2 && part[0] == '[' && part[^1] == ']'
                ? part[1..^1].Replace("]]", "]")
                : part;
            return $"[{raw.Replace("]", "]]")}]";
        }));

    private static string FormatSqlLiteral(object? value) => value switch
    {
        null => "NULL",
        int i => i.ToString(),
        // Added alongside BIGINT column decoding in RowDecoder - missed
        // here would repeat exactly the DECIMAL/NUMERIC gap above: a real
        // customer table ([LOG].[JobRun]'s bigint JobID) hitting the
        // NotSupportedException below outright crashes the whole
        // --undo/--replay/--export sql run, losing every other row too.
        long l => l.ToString(),
        // Found 2026-10-08 via LogCarverGuard's Rocky9 stress test: a
        // DATETIME2(7)-precision column (SYSDATETIME() has sub-millisecond
        // precision almost every time) formatted to only 3 fractional
        // digits here means the undo WHERE clause's literal never equals
        // the real stored value - every single row misses the match and
        // UndoSqlGenerator's optimistic-concurrency guard false-positives
        // as "the data changed", when it never did. .NET DateTime's own
        // tick resolution is 100ns, exactly DATETIME2(7)'s own max
        // precision, so 7 fractional digits round-trips losslessly; lower-
        // precision DATETIME/SMALLDATETIME columns just get trailing
        // zeros, which the implicit conversion on comparison still matches
        // correctly.
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'",
        // decimal.ToString() never uses scientific notation (unlike
        // double/float), so this always produces a plain SQL Server
        // decimal/numeric literal - InvariantCulture avoids a comma
        // decimal separator on a non-US locale. Added alongside DECIMAL/
        // NUMERIC column decoding in RowDecoder; missed here until a real
        // customer table (DECISION.StockDecisionDaily's ConfidenceScore/
        // EvidenceCompleteness/TargetPrice) crashed the whole
        // --undo/--replay/--export sql run outright with "Cannot format a
        // SQL literal for value of type System.Decimal" - every other
        // column in every other row was lost too, not just this value.
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => $"N'{s.Replace("'", "''")}'",
        // T-SQL has no boolean literal - BIT columns are written/compared
        // as 1/0. Same missing-case pattern as bigint/decimal above: found
        // 2026-10-02 via LogCarverGuard stress testing against a BIT
        // column, where it crashed undo generation outright with this same
        // NotSupportedException (caught further up the stack now, so it no
        // longer loses the whole audit record - see GuardedSqlExecutor's
        // UndoGenerationError - but the underlying gap is still real and
        // worth fixing at the source).
        bool b => b ? "1" : "0",
        _ => throw new NotSupportedException($"Cannot format a SQL literal for value of type {value.GetType()}."),
    };
}
