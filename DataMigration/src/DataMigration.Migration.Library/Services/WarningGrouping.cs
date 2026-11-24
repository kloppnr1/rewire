using System.Text.RegularExpressions;

namespace DataMigration.Migration.Services;

/// <summary>One bucket of warnings that share a normalised template.</summary>
public sealed record WarningGroup(string Template, int Count, IReadOnlyList<string> Ids);

/// <summary>
/// Groups free-text migration warnings by a normalised template so a run with hundreds of
/// "Customer 12345: invalid CVR …" lines collapses into a handful of meaningful buckets.
/// Mirrors the TypeScript implementation in the dashboard's <c>lib/groupWarnings.ts</c> so the
/// CLI summary and the dashboard group identically.
/// </summary>
public static partial class WarningGrouping
{
    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRe();

    [GeneratedRegex(@"\b\d{3,}\b")]
    private static partial Regex LongNumRe();

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex SingleQuotedRe();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex DoubleQuotedRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRe();

    [GeneratedRegex(@"(#|<id>)(?:\s*,\s*\1)+")]
    private static partial Regex MaskedListRe();

    /// <summary>Normalise a single warning to its grouping template.</summary>
    public static string Template(string msg)
    {
        var t = GuidRe().Replace(msg, "<id>");
        t = SingleQuotedRe().Replace(t, "'…'");
        t = DoubleQuotedRe().Replace(t, "\"…\"");
        t = LongNumRe().Replace(t, "#");
        // A message carrying a whole list of ids masked to "#, #, #, #" says nothing beyond
        // "#" — and worse, two messages about the same problem land in different buckets
        // purely because one names six accounts and the other names two. The ids belong in
        // the id list, not in the template.
        t = MaskedListRe().Replace(t, "$1");
        t = WhitespaceRe().Replace(t, " ");
        return t.Trim();
    }

    /// <summary>Best-effort representative id (long number, GUID, or first quoted value).</summary>
    public static string? Id(string msg) => Ids(msg).FirstOrDefault();

    /// <summary>
    /// Every id in the message, not just the first: a warning about eight accounts carries
    /// eight, and taking only the leading one dropped the other seven off the report entirely.
    /// GUIDs win over numbers when both appear, mirroring the single-id precedence.
    /// </summary>
    public static IReadOnlyList<string> Ids(string msg)
    {
        var guids = GuidRe().Matches(msg);
        if (guids.Count > 0) return guids.Select(m => m.Value).ToList();

        var nums = LongNumRe().Matches(msg);
        if (nums.Count > 0) return nums.Select(m => m.Value).ToList();

        var quoted = SingleQuotedRe().Match(msg);
        return quoted.Success ? [quoted.Value.Trim('\'')] : [];
    }

    /// <summary>Group warnings by template, sorted by count (desc), then template (ordinal asc).</summary>
    public static List<WarningGroup> Group(IEnumerable<string> messages)
    {
        var byTemplate = new Dictionary<string, (int Count, List<string> Ids)>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            var t = Template(m);
            if (!byTemplate.TryGetValue(t, out var entry))
                entry = (0, []);
            entry.Count++;
            foreach (var id in Ids(m))
                if (!entry.Ids.Contains(id)) entry.Ids.Add(id);
            byTemplate[t] = entry;
        }

        return byTemplate
            .Select(kv => new WarningGroup(kv.Key, kv.Value.Count, kv.Value.Ids))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Template, StringComparer.Ordinal)
            .ToList();
    }
}
