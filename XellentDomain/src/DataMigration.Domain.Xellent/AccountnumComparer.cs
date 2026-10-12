namespace DataMigration.Domain.Xellent;

/// <summary>
/// Compares Xellent Accountnum strings numerically when possible (so "58529" &lt; "107158"),
/// falling back to ordinal when either side can't be parsed as a long. Used when picking
/// the lowest Accountnum as merge-group representative — "lowest" in CLAUDE.md is the
/// natural numeric reading, not lexicographic.
/// </summary>
public sealed class AccountnumComparer : IComparer<string>
{
    public static readonly AccountnumComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var xParsed = long.TryParse(x, out var xv);
        var yParsed = long.TryParse(y, out var yv);
        if (xParsed && yParsed) return xv.CompareTo(yv);
        // Numeric beats non-numeric for stable ordering; pure-numeric strings sort first.
        if (xParsed != yParsed) return xParsed ? -1 : 1;
        return string.CompareOrdinal(x, y);
    }
}
