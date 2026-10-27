using LogCarver.Core;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Shared plumbing for UndoSqlGenerator and ReplaySqlGenerator - the two
/// are mirror images of each other (undo: after -&gt; before, replay:
/// before -&gt; after), so the statement-building and literal-formatting
/// logic lives here once instead of twice.
///
/// IDENTITY columns: SQL Server rejects outright any
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

    // Comparing a DATETIME2(7)-typed literal directly against a plain DATETIME column
    // is unreliable: `@datetimeCol = CONVERT(DATETIME2(7), @matchingLiteral)`
    // can return NO MATCH even when an explicit CAST of the column to
    // DATETIME2(7) first provably equals the same literal. Explicitly converting
    // BOTH sides to the same type removes the ambiguity. A plain string/int/etc.
    // column is unaffected - only a DateTime value's own column reference needs the wrap.
    private static string BuildWhereClause(IReadOnlyDictionary<string, object?> row) =>
        string.Join(" AND ", row.Select(kv => kv.Value switch
        {
            null => $"{EscapeIdentifier(kv.Key)} IS NULL",
            DateTime => $"CONVERT(DATETIME2(7), {EscapeIdentifier(kv.Key)}) = {FormatSqlLiteral(kv.Value)}",
            // geography/geometry reject a plain "=" outright - confirmed
            // via a direct sqlcmd probe: SQL error 403, "The equal to
            // operator is not valid for data type geography." .STEquals()
            // is the documented comparison method for these two types;
            // hierarchyid (the third type sharing system_type_id 240) DOES
            // support "=" and isn't special-cased here - only found to be
            // a problem for geography/geometry specifically.
            SqlClrBinaryValue clr when clr.TypeName is "geography" or "geometry" =>
                $"{EscapeIdentifier(kv.Key)}.STEquals({FormatSqlLiteral(kv.Value)}) = 1",
            _ => $"{EscapeIdentifier(kv.Key)} = {FormatSqlLiteral(kv.Value)}",
        }));

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
    // came out as "[[dbo]]].[[Orders]]]". Stripping one pre-existing layer of brackets (and
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
        // BIGINT columns: without this case a bigint value would hit the
        // NotSupportedException below and crash the whole --undo/--replay/
        // --export sql run, losing every other row too.
        long l => l.ToString(),
        // A DATETIME2(7) column (SYSDATETIME() has sub-millisecond precision almost
        // every time) formatted to only 3 fractional digits would make the undo
        // WHERE clause's literal never equal the real stored value - every row
        // misses the match and UndoSqlGenerator's optimistic-concurrency guard
        // false-positives as "the data changed". .NET DateTime's own tick
        // resolution is 100ns, exactly DATETIME2(7)'s max precision, so 7
        // fractional digits round-trips losslessly.
        //
        // Lower-precision DATETIME/SMALLDATETIME columns cannot take a bare
        // 7-fractional-digit string literal ('...12:00:00.0000000'): SQL Server
        // raises error 241 ("Conversion failed when converting date and/or time
        // from character string"), because CONVERT accepts up to 3 digits for
        // DATETIME's own string-literal grammar. So the literal is wrapped in an
        // explicit CONVERT(DATETIME2(7), '...'): DATETIME2(7)'s string grammar
        // accepts all 7 digits, and converting a DATETIME2 value (not a raw string)
        // to DATETIME/SMALLDATETIME is an ordinary precision-narrowing conversion
        // that never raises a string-parse error.
        DateTime dt => $"CONVERT(DATETIME2(7), '{dt:yyyy-MM-dd HH:mm:ss.fffffff}')",
        // decimal.ToString() never uses scientific notation (unlike
        // double/float), so this always produces a plain SQL Server
        // decimal/numeric literal - InvariantCulture avoids a comma
        // decimal separator on a non-US locale. Without this case a decimal/numeric
        // value would crash the whole --undo/--replay/--export sql run with
        // "Cannot format a SQL literal for value of type System.Decimal" - every
        // other column in every other row would be lost too, not just this value.
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // HighPrecisionDecimal.ToString() already produces a plain decimal
        // literal (no scientific notation) - see that type's own doc
        // comment for why DECIMAL(29-38 digits) needs it instead of
        // System.Decimal. Needed alongside the DecodeDecimal 4-group case: without it a
        // DECIMAL(38,10) column would raise the same NotSupportedException as every other
        // missing literal case (see RowDecoder's own doc comment).
        HighPrecisionDecimal hp => hp.ToString(),
        string s => $"N'{s.Replace("'", "''")}'",
        // SQL Server accepts a UNIQUEIDENTIFIER literal as a plain quoted
        // string in its canonical "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
        // form, which Guid.ToString()'s default format already produces.
        // A GUID primary key is an entirely ordinary schema choice; without this case
        // GUID-keyed tables would have no undo capability.
        Guid g => $"'{g}'",
        // T-SQL has no boolean literal - BIT columns are written/compared
        // as 1/0. Without this case a BIT column would crash undo generation with
        // this same NotSupportedException.
        bool b => b ? "1" : "0",
        // TINYINT (System.Byte) and SMALLINT (System.Int16) are both extremely
        // ordinary column types (flags, status codes, small counters) and need an
        // undo literal too.
        byte tiny => tiny.ToString(),
        short small => small.ToString(),
        // Same round: VARBINARY/BINARY decode to a bare byte[] (see
        // RowDecoder.DecodeVariableLengthValue's own doc comment for why
        // this didn't exist before) - SQL Server's 0x-prefixed hex literal
        // is the standard binary literal form, valid directly in both a
        // VALUES list and a WHERE clause's plain "=" comparison.
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        // CLR UDTs (geography/geometry/hierarchyid - see SqlClrBinaryValue's
        // own doc comment for why system_type_id alone can't tell them
        // apart) need an explicit CONVERT back from their binary
        // serialization - confirmed empirically via a direct sqlcmd probe
        // that CONVERT(GEOGRAPHY, <same bytes CAST from the type produced>)
        // round-trips exactly, whereas a bare 0x literal assigned directly
        // is not a defined conversion for these types.
        SqlClrBinaryValue clr => $"CONVERT([{clr.TypeName}], 0x{Convert.ToHexString(clr.Bytes)})",
        _ => throw new NotSupportedException($"Cannot format a SQL literal for value of type {value.GetType()}."),
    };
}
