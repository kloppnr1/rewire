using System.Text;
using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Reads a report-consumer2-duplicate CSV back as a list of points for fix-consumer2-duplicate.
/// Only the scope comes from the file; the cleanup still reads every point fresh from Settl.
/// </summary>
public static class Consumer2ReportInput
{
    public static IReadOnlyList<Guid> ReadDuplicateSettlIds(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ReadDuplicateSettlIds(reader, path);
    }

    internal static IReadOnlyList<Guid> ReadDuplicateSettlIds(TextReader reader, string source)
    {
        var header = reader.ReadLine() is { } first ? Cells(first) : [];
        if (!header.SequenceEqual(ReportConsumer2DuplicateRunner.Header, StringComparer.Ordinal))
            throw new InvalidDataException(
                $"{source} is not a current report-consumer2-duplicate CSV: the header does not match. Re-run the report.");
        var settlId = Array.IndexOf(ReportConsumer2DuplicateRunner.Header, "SettlId");
        var duplicate = Array.IndexOf(ReportConsumer2DuplicateRunner.Header, "DuplicateConsumer2");
        var ids = new List<Guid>();
        var line = 1;
        while (reader.ReadLine() is { } row)
        {
            line++;
            if (row.Length == 0) continue;
            var cells = Cells(row);
            if (cells.Length != header.Length)
                throw new InvalidDataException($"{source} line {line}: expected {header.Length} columns, found {cells.Length}.");
            if (cells[duplicate] != "Yes") continue;
            if (!Guid.TryParse(cells[settlId], out var id))
                throw new InvalidDataException($"{source} line {line}: SettlId is not a GUID.");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Splits one report row: ';'-separated, every cell quoted, embedded quotes doubled.</summary>
    private static string[] Cells(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < row.Length && row[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells.ToArray();
    }
}
