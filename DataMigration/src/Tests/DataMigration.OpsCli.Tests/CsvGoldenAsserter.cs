namespace DataMigration.OpsCli.Tests;

/// <summary>
/// Compares CSV runner output to a committed golden file. On first run (golden missing),
/// writes the actual output to a <c>new/</c> sibling folder and throws
/// <see cref="BaselineMissingException"/>. On drift, writes proposed baseline to <c>new/</c>
/// and throws <see cref="BaselineMismatchException"/> with the first 20 differing lines.
/// </summary>
public static class CsvGoldenAsserter
{
    public static void AssertMatchesGolden(string csv, string goldenPath)
    {
        if (!File.Exists(goldenPath))
        {
            WriteProposedBaseline(csv, goldenPath);
            throw new BaselineMissingException(
                $"No golden file at {goldenPath}. Proposed baseline written to the 'new/' sibling folder — " +
                "review it, then copy into place to accept the baseline.");
        }

        var expected = File.ReadAllText(goldenPath);
        if (expected == csv) return;

        WriteProposedBaseline(csv, goldenPath);

        var expectedLines = expected.Split('\n');
        var actualLines = csv.Split('\n');
        var diffs = new List<string>();
        var max = Math.Max(expectedLines.Length, actualLines.Length);
        for (var i = 0; i < max && diffs.Count < 20; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            var a = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (e != a)
                diffs.Add($"line {i + 1}:\n  expected: {e}\n  actual:   {a}");
        }

        throw new BaselineMismatchException(
            $"Golden file drift at {goldenPath}.\nFirst {diffs.Count} difference(s):\n  {string.Join("\n  ", diffs)}\n" +
            "If this change is intentional, review the 'new/' sibling folder and promote.");
    }

    private static void WriteProposedBaseline(string csv, string goldenPath)
    {
        var dir = Path.GetDirectoryName(goldenPath)!;
        var newDir = Path.Combine(dir, "new");
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, Path.GetFileName(goldenPath)), csv);
    }
}

public class BaselineMissingException : Xunit.Sdk.XunitException
{
    public BaselineMissingException(string message) : base(message) { }
}

public class BaselineMismatchException : Xunit.Sdk.XunitException
{
    public BaselineMismatchException(string message) : base(message) { }
}
