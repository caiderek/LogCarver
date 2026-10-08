using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Reads a table's physical row layout from sys.system_internals_partition_columns
/// (undocumented, but this is exactly the metadata RowDecoder needs - see
/// CLAUDE.md and 研究紀錄 第八節 for why this beats hardcoding a type map).
///
/// index_id filter: a table can have more than one row in sys.partitions -
/// index_id=0/1 for its own heap/clustered-index storage, plus one more
/// per nonclustered index. Without filtering to 0/1, a heap table with any
/// nonclustered index (e.g. one backing a NONCLUSTERED PRIMARY KEY) joins
/// in that index's own internal column layout alongside the heap's real
/// one, producing bogus SystemTypeId values for columns not covered by the
/// index - found for real on 2026-09-28 via a heap table with a
/// PRIMARY KEY NONCLUSTERED constraint (see SchemaReaderTests).
///
/// Table partitioning: sys.partitions also has one row per PARTITION for a
/// given index_id, not just one - a table with N partitions returns this
/// entire column layout N times over (once per partition_id). For an
/// ordinary partitioned table these N rows are identical (a column's
/// physical offset/type/nullability doesn't vary by partition, only which
/// physical partition a given ROW lives in does), and returning every one
/// of those duplicates used to corrupt RowDecoder's positional
/// variable-length-column matching downstream (schema.Where(LeafOffset
/// &lt; 0).OrderByDescending(LeafOffset), matched index-for-index against
/// the row's own offset array - N duplicates per column shifts that
/// mapping and decodes wrong values, not an exception).
///
/// GetTableSchemaAsync de-duplicates by ColumnId, but does NOT blindly
/// keep whichever partition's row came back first: SQL Server allows
/// per-partition DATA_COMPRESSION (ALTER TABLE ... REBUILD PARTITION = n
/// WITH (DATA_COMPRESSION = PAGE)), and a compressed partition's rows use
/// a different physical layout than an uncompressed one - a mismatch here
/// would mean silently applying one partition's layout to another
/// partition's differently-shaped rows, an even quieter version of the
/// exact bug this fix closes. Two rows for the same ColumnId that
/// disagree on anything decode-relevant throw rather than pick one
/// arbitrarily. LogCarverOffline's CaptureMetadata already refuses any
/// compressed table outright (a blunter, already-existing gate this one
/// backs up); the public/online LogCarver.Cli currently only warns on
/// compression and continues, so this is this path's only real protection
/// against mixed per-partition compression.
///
/// Non-unique clustered index: SQL Server reserves variable-length slot
/// -1 (and null-bitmap bit 1) for a hidden "uniquifier" column that
/// disambiguates duplicate clustering-key values - reserved whether or
/// not any given row actually needs one materialized (found for real
/// 2026-10-02, confirmed via sys.indexes.is_unique plus manual hex
/// decoding of real fn_dblog row bytes: a table with a non-unique
/// clustered index on a different column than its PK had its real
/// variable-length columns' own labels start at -2, not -1, and decoding
/// without accounting for the gap put each NVARCHAR column's bytes one
/// slot off from where they belonged - a plausible-looking WRONG value,
/// not an honest failure). This is invisible to sys.columns (it's not a
/// user column at all), so GetTableSchemaAsync queries sys.indexes
/// separately and, when the clustered index exists and is non-unique,
/// injects a synthetic <see cref="ColumnSchema.ReservedUniquifierSlotName"/>
/// entry at LeafOffset -1 / LeafNullBit 1 - real columns' own existing
/// LeafOffset/LeafNullBit values (already correctly reported by SQL
/// Server as -2, -3, ... / 2, 3, ...) then line up correctly against the
/// row's actual physical layout without RowDecoder needing to know
/// anything about uniquifiers itself.
/// </summary>
public static class SchemaReader
{
    private const string Sql = """
        SELECT c.name AS ColName, c.column_id AS ColumnId,
               ipc.leaf_offset AS LeafOffset, ipc.leaf_null_bit AS LeafNullBit,
               ipc.max_length AS MaxLength, ipc.system_type_id AS SystemTypeId,
               c.scale AS Scale, c.is_identity AS IsIdentity, TYPE_NAME(c.user_type_id) AS TypeName
        FROM sys.system_internals_partition_columns ipc
        JOIN sys.partitions p ON p.partition_id = ipc.partition_id
        JOIN sys.columns c ON c.object_id = p.object_id AND c.column_id = ipc.partition_column_id
        WHERE p.object_id = OBJECT_ID(@tableName) AND p.index_id IN (0, 1)
        ORDER BY c.column_id;
        """;

    private const string ClusteredIndexUniquenessSql = """
        SELECT is_unique FROM sys.indexes WHERE object_id = OBJECT_ID(@tableName) AND index_id = 1;
        """;

    /// <param name="tableName">Schema-qualified, e.g. "dbo.LogTest".</param>
    public static async Task<IReadOnlyList<ColumnSchema>> GetTableSchemaAsync(
        SqlConnection connection, string tableName, CancellationToken ct = default)
    {
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);

        // Keyed by ColumnId, not just appended - see this class's doc
        // comment for why a partitioned table returns the same column
        // multiple times over.
        var results = new Dictionary<int, ColumnSchema>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int columnId = reader.GetInt32(reader.GetOrdinal("ColumnId"));
            var candidate = new ColumnSchema(
                Name: reader.GetString(reader.GetOrdinal("ColName")),
                ColumnId: columnId,
                LeafOffset: reader.GetInt16(reader.GetOrdinal("LeafOffset")),
                LeafNullBit: reader.GetInt16(reader.GetOrdinal("LeafNullBit")),
                MaxLength: reader.GetInt16(reader.GetOrdinal("MaxLength")),
                SystemTypeId: reader.GetByte(reader.GetOrdinal("SystemTypeId")),
                Scale: reader.GetByte(reader.GetOrdinal("Scale")),
                IsIdentity: reader.GetBoolean(reader.GetOrdinal("IsIdentity")),
                TypeName: reader.GetString(reader.GetOrdinal("TypeName")));

            if (results.TryGetValue(columnId, out var existing))
            {
                // record equality compares every property - any
                // disagreement means two partitions' physical layouts for
                // this column genuinely differ (see doc comment).
                if (existing != candidate)
                    throw new NotSupportedException(
                        $"Column '{candidate.Name}' has inconsistent physical layout across '{tableName}''s partitions " +
                        "(possibly mixed per-partition DATA_COMPRESSION) - refusing rather than guessing which " +
                        "partition's layout applies to which rows.");
                continue;
            }
            results[columnId] = candidate;
        }
        await reader.CloseAsync();

        var schema = new List<ColumnSchema>(results.Values);
        if (await HasNonUniqueClusteredIndexAsync(connection, tableName, ct))
        {
            schema.Add(new ColumnSchema(
                Name: ColumnSchema.ReservedUniquifierSlotName, ColumnId: -1,
                LeafOffset: -1, LeafNullBit: 1, MaxLength: 0, SystemTypeId: 0));
        }

        return schema.OrderBy(c => c.ColumnId).ToList();
    }

    private static async Task<bool> HasNonUniqueClusteredIndexAsync(
        SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = new SqlCommand(ClusteredIndexUniquenessSql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var isUnique = await command.ExecuteScalarAsync(ct);
        // No row at all = heap (no clustered index) - the uniquifier
        // mechanism only exists for a non-unique CLUSTERED index, so a
        // heap never needs this regardless of its own PK/unique
        // constraints (those are ordinary nonclustered indexes, with
        // nothing analogous to a uniquifier).
        return isUnique is bool b && !b;
    }
}
