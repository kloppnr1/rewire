using System.Text.Json;
using System.Text.Json.Nodes;

namespace TimeSeries.Xellent.IntegrationTests;

/// <summary>
/// Compares an extraction output to a committed golden JSON file. On first run (or after a
/// bevidst extraction change) the golden file is missing — the asserter writes the fresh
/// output to a <c>new/</c> sibling folder and fails with a message that asks the developer
/// to review and promote.
///
/// <para>Canonicalisation: properties are recursively sorted, Guid-shaped strings are
/// replaced with <c>"&lt;guid&gt;"</c> so randomly generated DTO Ids don't cause drift.</para>
/// </summary>
public static class JsonGoldenAsserter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true
    };

    public static void AssertMatchesGolden(object extracted, string goldenPath)
    {
        var rootNode = extracted is JsonNode existingNode
            ? existingNode
            : JsonSerializer.SerializeToNode(extracted, SerializerOptions);
        var actualJson = Canonicalise(rootNode);

        if (!File.Exists(goldenPath))
        {
            WriteProposedBaseline(actualJson, goldenPath);
            throw new BaselineMissingException(
                $"No golden file at {goldenPath}. Proposed baseline written to the 'new/' sibling folder — " +
                $"review it, then copy into place to accept the baseline.");
        }

        var expectedJson = Canonicalise(JsonNode.Parse(File.ReadAllText(goldenPath)));

        if (!JsonEquals(expectedJson, actualJson))
        {
            WriteProposedBaseline(actualJson, goldenPath);
            var diffs = FindDifferences(expectedJson, actualJson, "").Take(20).ToList();
            var summary = string.Join("\n  ", diffs);
            throw new BaselineMismatchException(
                $"Golden file drift at {goldenPath}.\nFirst {diffs.Count} difference(s):\n  {summary}\n" +
                $"If this change is intentional, review the 'new/' sibling folder and promote.");
        }
    }

    private static void WriteProposedBaseline(JsonNode? canonical, string goldenPath)
    {
        var dir = Path.GetDirectoryName(goldenPath)!;
        var newDir = Path.Combine(dir, "new");
        Directory.CreateDirectory(newDir);
        var newPath = Path.Combine(newDir, Path.GetFileName(goldenPath));
        File.WriteAllText(newPath, canonical?.ToJsonString(SerializerOptions) ?? "null");
    }

    private static JsonNode? Canonicalise(JsonNode? node, string? propertyName = null)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                sorted[key] = Canonicalise(obj[key]?.DeepClone(), key);
            return sorted;
        }
        if (node is JsonArray arr)
        {
            var canonicalised = arr
                .Select(item => Canonicalise(item?.DeepClone(), propertyName))
                .OrderBy(item => item?.ToJsonString(SerializerOptions) ?? "null", StringComparer.Ordinal)
                .ToList();
            var result = new JsonArray();
            foreach (var item in canonicalised)
                result.Add(item);
            return result;
        }
        if (node is JsonValue val && val.TryGetValue<string>(out var s)
            && propertyName != null && IsRuntimeRandomProperty(propertyName)
            && (Guid.TryParse(s, out _) || DateTime.TryParse(s, out _)))
            return JsonValue.Create("<guid>");
        return node;
    }

    private static bool IsRuntimeRandomProperty(string propertyName) =>
        string.Equals(propertyName, "Id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "CustomerId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "MeteringPointId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "MeterId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "TransactionId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "SnapshotTime", StringComparison.OrdinalIgnoreCase);

    private static bool JsonEquals(JsonNode? a, JsonNode? b) =>
        (a?.ToJsonString(SerializerOptions) ?? "null") == (b?.ToJsonString(SerializerOptions) ?? "null");

    private static IEnumerable<string> FindDifferences(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is JsonObject eObj && actual is JsonObject aObj)
        {
            var keys = eObj.Select(kv => kv.Key).Union(aObj.Select(kv => kv.Key)).OrderBy(k => k);
            foreach (var k in keys)
            {
                var ePath = string.IsNullOrEmpty(path) ? k : $"{path}.{k}";
                foreach (var d in FindDifferences(eObj[k], aObj[k], ePath)) yield return d;
            }
            yield break;
        }
        if (expected is JsonArray eArr && actual is JsonArray aArr)
        {
            if (eArr.Count != aArr.Count)
            {
                yield return $"{path}: array length {eArr.Count} → {aArr.Count}";
                yield break;
            }
            for (var i = 0; i < eArr.Count; i++)
                foreach (var d in FindDifferences(eArr[i], aArr[i], $"{path}[{i}]")) yield return d;
            yield break;
        }
        var expStr = expected?.ToJsonString() ?? "null";
        var actStr = actual?.ToJsonString() ?? "null";
        if (expStr != actStr)
            yield return $"{path}: {expStr} → {actStr}";
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
