using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// The columns that identify a row of a table: its primary key, else the
/// columns of a unique clustered index. With them an UPDATE can be undone at
/// column level (see <see cref="UndoSqlGenerator"/>).
/// </summary>
public static class KeyColumnReader
{
    /// <returns>The key columns in key order, or null if the table has no such key (or it cannot be read).</returns>
    public static async Task<IReadOnlyList<string>?> GetKeyColumnsAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        try
        {
            await using var command = new SqlCommand(
                @"SELECT c.name
                  FROM sys.indexes i
                  JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE i.object_id = OBJECT_ID(@t)
                    AND i.index_id = (SELECT TOP 1 index_id FROM sys.indexes
                                      WHERE object_id = OBJECT_ID(@t) AND (is_primary_key = 1 OR (index_id = 1 AND is_unique = 1))
                                      ORDER BY is_primary_key DESC)
                  ORDER BY ic.key_ordinal;", connection);
            command.Parameters.AddWithValue("@t", tableName);
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(0));
            return columns.Count > 0 ? columns : null;
        }
        catch (SqlException)
        {
            return null; // no key known: the whole-row match is used
        }
    }
}
