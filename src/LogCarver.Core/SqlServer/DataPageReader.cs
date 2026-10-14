using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>One record of a data page, as DBCC PAGE (print option 3) reports it.</summary>
/// <param name="KeyHash">
/// DBCC PAGE's KeyHashValue as upper-case hex (the same 6 bytes the log
/// carries as the end of a clustered row's lock information), or null for a
/// row in a heap.
/// </param>
public sealed record PageRow(int SlotId, string RecordType, byte[] Bytes, string? KeyHash);

/// <summary>
/// Reads the raw record bytes of rows that currently sit on a data page. Used
/// only to anchor a row's history in its present state (see
/// RowImageAnchoring); every use is verified against the log afterwards.
///
/// DBCC PAGE is undocumented but long-stable. With TABLERESULTS and print
/// option 3 it returns one table row per output line; for each slot there are
/// "Memory Dump" lines (the record's bytes, 16-byte lines of 4-byte groups, an
/// ASCII rendering after them), a "Record Type" line and a "KeyHashValue"
/// line. Needs the same sysadmin rights fn_dblog does.
/// </summary>
public static partial class DataPageReader
{
    /// <summary>One output line of DBCC PAGE ... WITH TABLERESULTS.</summary>
    public readonly record struct Line(string ParentObject, string Object, string Field, string Value);

    public static async Task<IReadOnlyList<PageRow>> ReadRowsAsync(
        SqlConnection connection, string pageId, CancellationToken ct = default)
    {
        if (!TryParsePageId(pageId, out int fileId, out int page))
            return [];

        await using var command = new SqlCommand("DBCC PAGE(@db, @file, @page, 3) WITH TABLERESULTS, NO_INFOMSGS", connection);
        command.CommandTimeout = 60;
        command.Parameters.AddWithValue("@db", connection.Database);
        command.Parameters.AddWithValue("@file", fileId);
        command.Parameters.AddWithValue("@page", page);

        var lines = new List<Line>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(new Line(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return Parse(lines);
    }

    /// <summary>fn_dblog's "FFFF:PPPPPPPP" (hex file id : hex page number).</summary>
    public static bool TryParsePageId(string pageId, out int fileId, out int page)
    {
        fileId = page = 0;
        var parts = pageId.Split(':');
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fileId) &&
               int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out page);
    }

    public static IReadOnlyList<PageRow> Parse(IEnumerable<Line> lines)
    {
        var bytesBySlot = new SortedDictionary<int, List<byte>>();
        var lengthBySlot = new Dictionary<int, int>();
        var typeBySlot = new Dictionary<int, string>();
        var hashBySlot = new Dictionary<int, string>();

        foreach (var line in lines)
        {
            // Memory dump lines: ParentObject "Slot N Offset 0x.. Length L",
            // Object "Memory Dump @0x..", Value "<offset>:   <groups>  <ascii>".
            var parent = SlotRegex().Match(line.ParentObject);
            if (parent.Success && line.Object.StartsWith("Memory Dump", StringComparison.Ordinal))
            {
                int slot = int.Parse(parent.Groups[1].Value, CultureInfo.InvariantCulture);
                int length = int.Parse(parent.Groups[3].Value, CultureInfo.InvariantCulture);
                lengthBySlot[slot] = length;
                if (!bytesBySlot.TryGetValue(slot, out var buffer))
                    bytesBySlot[slot] = buffer = [];
                AppendDumpLine(line.Value, length, buffer);
                continue;
            }

            if (parent.Success && line.Field == "KeyHashValue")
            {
                int slot = int.Parse(parent.Groups[1].Value, CultureInfo.InvariantCulture);
                hashBySlot[slot] = line.Value.Trim().Trim('(', ')').ToUpperInvariant();
                continue;
            }

            var obj = SlotRegex().Match(line.Object);
            if (obj.Success && line.Field == "Record Type")
            {
                int slot = int.Parse(obj.Groups[1].Value, CultureInfo.InvariantCulture);
                typeBySlot[slot] = line.Value.Trim();
            }
        }

        var rows = new List<PageRow>();
        foreach (var (slot, buffer) in bytesBySlot)
        {
            // A record whose dump did not come out to its stated length is
            // not trusted at all.
            if (!lengthBySlot.TryGetValue(slot, out int length) || buffer.Count != length)
                continue;
            rows.Add(new PageRow(
                slot,
                typeBySlot.GetValueOrDefault(slot, ""),
                buffer.ToArray(),
                hashBySlot.GetValueOrDefault(slot)));
        }
        return rows;
    }

    private static void AppendDumpLine(string value, int recordLength, List<byte> buffer)
    {
        int colon = value.IndexOf(':');
        if (colon <= 0 ||
            !long.TryParse(value.AsSpan(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long lineOffset))
            return;

        // The ASCII rendering after the hex groups can itself look like hex,
        // so stop as soon as this line's expected byte count is reached.
        int expected = (int)Math.Min(20, recordLength - lineOffset);
        int got = 0;
        foreach (var token in value[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (got >= expected || token.Length % 2 != 0 || token.Length > 8 || !IsHex(token))
                break;
            for (int i = 0; i < token.Length && got < expected; i += 2, got++)
                buffer.Add(byte.Parse(token.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
    }

    private static bool IsHex(string text)
    {
        foreach (char c in text)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    [GeneratedRegex(@"^Slot (\d+) Offset 0x([0-9a-fA-F]+) Length (\d+)$")]
    private static partial Regex SlotRegex();
}
