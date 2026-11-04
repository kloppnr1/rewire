using System.Globalization;
using System.Text;

namespace DataMigration.OpsCli.Services;

/// <summary>An ownership issue someone has looked at and accepted, from the handled-issues list.</summary>
internal sealed record HandledOwnershipIssue(
    string Reason,
    string SourceAccount,
    string MeteringPoint,
    string SettlCustomers,
    string Note,
    DateOnly HandledDate);

internal sealed record HandledIssueSplit(
    IReadOnlyList<SourceAccountOwnershipIssue> New,
    IReadOnlyList<(SourceAccountOwnershipIssue Issue, HandledOwnershipIssue Handled)> Handled,
    IReadOnlyList<HandledOwnershipIssue> NoLongerSeen);

/// <summary>
/// The list of ownership issues already handled, so a run reports only new problems. It changes
/// reporting only: handled issues are still omitted from the state exactly as before.
/// An issue is handled when Reason, SourceAccount, MeteringPoint and SettlCustomers all match exactly.
/// </summary>
internal static class HandledOwnershipIssues
{
    internal const string DefaultPath = @"C:\Services\DataMigration\data\handled-ownership-issues.csv";
    internal const string Header = "Reason;SourceAccount;MeteringPoint;SettlCustomers;Note;HandledDate";
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd-MM-yyyy"];

    /// <summary>
    /// The explicit path, which must exist; otherwise the default path when it exists; otherwise null.
    /// </summary>
    internal static string? ResolvePath(string? explicitPath, string defaultPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath
                : throw new InvalidOperationException($"Handled-issues file not found: {explicitPath}");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    internal static IReadOnlyList<HandledOwnershipIssue> Load(string path)
    {
        var lines = ReadText(File.ReadAllBytes(path))
            .Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (lines.Length == 0 || lines[0] != Header)
            throw Malformed(path, 1, $"the header must be exactly '{Header}'");
        var rows = new List<HandledOwnershipIssue>();
        var lineByKey = new Dictionary<(string, string, string, string), int>();
        for (var i = 1; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            if (lines[i].Length == 0) continue;
            var fields = SplitLine(lines[i]) ?? throw Malformed(path, lineNumber, "a quoted field is not closed");
            if (fields.Length != 6)
                throw Malformed(path, lineNumber, $"expected 6 fields, found {fields.Length}");
            if (fields[0].Trim().Length == 0)
                throw Malformed(path, lineNumber, "Reason is empty");
            if (fields[2].Contains("E+", StringComparison.OrdinalIgnoreCase))
                throw Malformed(path, lineNumber,
                    $"MeteringPoint '{fields[2]}' is in scientific notation; Excel rewrote the number");
            if (!DateOnly.TryParseExact(fields[5].Trim(), DateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var handledDate))
                throw Malformed(path, lineNumber, $"HandledDate '{fields[5]}' is not a date (yyyy-MM-dd)");
            var key = (fields[0], fields[1], fields[2], fields[3]);
            if (lineByKey.TryGetValue(key, out var firstLine))
                throw Malformed(path, lineNumber, $"same issue as line {firstLine}");
            lineByKey[key] = lineNumber;
            rows.Add(new HandledOwnershipIssue(fields[0], fields[1], fields[2], fields[3], fields[4], handledDate));
        }
        return rows;
    }

    internal static HandledIssueSplit Split(IReadOnlyList<SourceAccountOwnershipIssue> issues,
        IReadOnlyList<HandledOwnershipIssue> handled)
    {
        var byKey = handled.ToDictionary(h => (h.Reason, h.SourceAccount, h.MeteringPoint, h.SettlCustomers));
        var seen = new HashSet<(string, string, string, string)>();
        var newIssues = new List<SourceAccountOwnershipIssue>();
        var handledIssues = new List<(SourceAccountOwnershipIssue, HandledOwnershipIssue)>();
        foreach (var issue in issues)
        {
            var key = (issue.Reason, issue.SourceAccount, issue.MeteringPoint, issue.SettlCustomers);
            if (byKey.TryGetValue(key, out var match))
            {
                handledIssues.Add((issue, match));
                seen.Add(key);
            }
            else newIssues.Add(issue);
        }
        return new HandledIssueSplit(newIssues, handledIssues,
            [.. handled.Where(h => !seen.Contains((h.Reason, h.SourceAccount, h.MeteringPoint, h.SettlCustomers)))]);
    }

    internal static string BuildHandledCsv(
        IReadOnlyList<(SourceAccountOwnershipIssue Issue, HandledOwnershipIssue Handled)> handled)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Reason;SourceAccount;MeteringPoint;SettlCustomers;CosDate;Note;HandledDate");
        foreach (var (issue, entry) in handled
            .OrderBy(h => h.Issue.Reason, StringComparer.Ordinal)
            .ThenBy(h => h.Issue.SourceAccount, StringComparer.Ordinal)
            .ThenBy(h => h.Issue.MeteringPoint, StringComparer.Ordinal))
        {
            csv.Append(SourceAccountOwnership.Csv(issue.Reason)).Append(';')
                .Append(SourceAccountOwnership.Csv(issue.SourceAccount)).Append(';')
                .Append(SourceAccountOwnership.Csv(issue.MeteringPoint)).Append(';')
                .Append(SourceAccountOwnership.Csv(issue.SettlCustomers)).Append(';')
                .Append(issue.CosDate?.ToString("yyyy-MM-dd") ?? "").Append(';')
                .Append(SourceAccountOwnership.Csv(entry.Note)).Append(';')
                .AppendLine(entry.HandledDate.ToString("yyyy-MM-dd"));
        }
        return csv.ToString();
    }

    /// <summary>Same columns as the handled-issues list, so rows can be compared or pasted back.</summary>
    internal static string BuildNoLongerSeenCsv(IReadOnlyList<HandledOwnershipIssue> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(Header);
        foreach (var row in rows)
        {
            csv.Append(SourceAccountOwnership.Csv(row.Reason)).Append(';')
                .Append(SourceAccountOwnership.Csv(row.SourceAccount)).Append(';')
                .Append(SourceAccountOwnership.Csv(row.MeteringPoint)).Append(';')
                .Append(SourceAccountOwnership.Csv(row.SettlCustomers)).Append(';')
                .Append(SourceAccountOwnership.Csv(row.Note)).Append(';')
                .AppendLine(row.HandledDate.ToString("yyyy-MM-dd"));
        }
        return csv.ToString();
    }

    internal static string HandledReportPath(string stateOutputPath) =>
        BesideState(stateOutputPath, "handled-issues");

    internal static string NoLongerSeenReportPath(string stateOutputPath) =>
        BesideState(stateOutputPath, "handled-no-longer-seen");

    private static string BesideState(string stateOutputPath, string suffix)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-{suffix}.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }

    /// <summary>UTF-8 (with or without BOM); a file Excel saved in the Windows code page falls back to Latin-1.</summary>
    private static string ReadText(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Splits one semicolon line; double quotes wrap a field and "" is a literal quote.
    /// Null when a quoted field is not closed.</summary>
    private static string[]? SplitLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        if (quoted) return null;
        fields.Add(field.ToString());
        return [.. fields];
    }

    private static InvalidOperationException Malformed(string path, int line, string problem) =>
        new($"Handled-issues file {path} is malformed at line {line}: {problem}.");
}
