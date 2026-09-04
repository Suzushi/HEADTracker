namespace HeadTracker.Core.Protocol;

/// <summary>
/// Lookup of the connecting game in the "facetracknoir supported games.csv"
/// database. Port of the legacy CSV::getGameData: semicolon separated columns,
/// double quotes protect embedded separators, column 6 is the international
/// game id, column 7 an 11-byte hex string fuzzed into the protocol table.
/// </summary>
public static class GameDatabase
{
    public readonly record struct GameEntry(string Name, byte[] Table);

    public static bool TryGetGame(int id, string csvPath, out GameEntry entry)
    {
        entry = default;
        var table = new byte[8];

        if (!File.Exists(csvPath))
        {
            return false;
        }

        var idStr = id.ToString();
        foreach (var columns in ReadRows(csvPath))
        {
            if (columns.Count != 8)
            {
                return false; // legacy aborts on the first malformed line
            }

            if (!string.Equals(columns[6], idStr, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var proto = columns[3];
            var name = columns[1];
            var hexId = columns[7];

            if (proto != "V160" && hexId.Length == 22 && TryParseTable(hexId, table))
            {
                // table filled
            }

            entry = new GameEntry(name, table);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The legacy sscanf reads 11 bytes in the order
    /// fuzz[2], fuzz[0], tmp[3], tmp[2], tmp[1], tmp[0], tmp[7], tmp[6], tmp[5], tmp[4], fuzz[1]
    /// and the protocol table is tmp[0..7].
    /// </summary>
    private static bool TryParseTable(string hexId, byte[] table)
    {
        Span<byte> bytes = stackalloc byte[11];
        for (var i = 0; i < 11; i++)
        {
            if (!byte.TryParse(hexId.AsSpan(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        table[3] = bytes[2];
        table[2] = bytes[3];
        table[1] = bytes[4];
        table[0] = bytes[5];
        table[7] = bytes[6];
        table[6] = bytes[7];
        table[5] = bytes[8];
        table[4] = bytes[9];
        return true;
    }

    private static IEnumerable<IReadOnlyList<string>> ReadRows(string csvPath)
    {
        foreach (var rawLine in File.ReadLines(csvPath))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            yield return SplitLine(line);
        }
    }

    private static IReadOnlyList<string> SplitLine(string line)
    {
        var columns = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ';' && !inQuotes)
            {
                columns.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        columns.Add(current.ToString());
        return columns;
    }
}
