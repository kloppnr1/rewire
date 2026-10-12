using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Property-for-property diff of two objects via their JSON projections. Walks the
/// <b>intersection</b> of keys (a field present on both intended and actual), recursing into
/// nested objects, and yields one entry per differing leaf. Excluded keys (GUIDs, Uri/Links,
/// audit/timestamps) are skipped at every level — matching reconcile's "match on natural keys,
/// ignore server-assigned" semantics. Arrays are compared as canonical JSON (order-sensitive).
///
/// <para>Intersection-only means a field that exists solely on the intended side (e.g.
/// <c>CrmData</c>) or solely on the actual side is NOT reported — intended/actual DTO shapes
/// must already align for full coverage (true for customer/payer/billing; MP-master uses an
/// explicit projector instead).</para>
/// </summary>
public static class JsonValueDiff
{
    /// <summary>Property names skipped at every nesting level.</summary>
    public static readonly IReadOnlySet<string> DefaultExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Uri", "Links", "TransactionId", "CustomerId", "MeteringPointId", "PayerId",
        "OwnerId", "ProductElementId", "LastUpdated", "CreatedAt", "UpdatedAt", "Timestamp",
    };

    private static readonly JsonSerializerOptions SerializerOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    /// <summary>Node→text options for leaf comparison: relaxed encoder so Danish characters (æøå)
    /// render literally instead of as \uXXXX escapes leaking into the CSV output.</summary>
    private static readonly JsonSerializerOptions NodeTextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="includeEqual">When true, leaves whose intended and actual text are equal are
    /// emitted too (Intended == Actual), not just the differing ones. The drift scan leaves this false
    /// (only differences are drift); the per-representative full-payload view sets it true so every
    /// compared field surfaces — equal ones the caller tags <c>DriftKind.Match</c>. Intersection-only
    /// is unchanged: a leaf present on only one side is still not emitted.</param>
    public static IReadOnlyList<(string Path, string? Intended, string? Actual, string? IntendedJson)> Diff(
        object? intended, object? actual, IReadOnlySet<string>? exclude = null, bool includeEqual = false)
    {
        var i = intended == null ? null : JsonSerializer.SerializeToNode(intended, intended.GetType(), SerializerOptions);
        var a = actual == null ? null : JsonSerializer.SerializeToNode(actual, actual.GetType(), SerializerOptions);
        return DiffNodes(i, a, exclude, includeEqual: includeEqual);
    }

    /// <summary>
    /// Diff two already-parsed JSON nodes (e.g. two DTOs serialised to nodes). Walks the
    /// intersection of keys, recursing into nested objects. <paramref name="prefix"/> seeds the
    /// reported path. See <see cref="Diff"/> for <paramref name="includeEqual"/>.
    /// </summary>
    public static IReadOnlyList<(string Path, string? Intended, string? Actual, string? IntendedJson)> DiffNodes(
        JsonNode? intended, JsonNode? actual, IReadOnlySet<string>? exclude = null, string prefix = "", bool includeEqual = false)
    {
        exclude ??= DefaultExcludes;
        var results = new List<(string, string?, string?, string?)>();
        Walk(prefix, intended, actual, exclude, results, includeEqual);
        return results;
    }

    private static void Walk(string path, JsonNode? i, JsonNode? a, IReadOnlySet<string> exclude, List<(string, string?, string?, string?)> acc, bool includeEqual)
    {
        if (i is JsonObject io && a is JsonObject ao)
        {
            // Case-insensitive key match so PascalCase-intended aligns with camelCase/raw actual.
            var aByKey = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ao) aByKey[kv.Key] = kv.Value;
            foreach (var kv in io)
            {
                if (exclude.Contains(kv.Key)) continue;
                if (!aByKey.TryGetValue(kv.Key, out var av)) continue; // intersection only
                Walk(path.Length == 0 ? kv.Key : $"{path}.{kv.Key}", kv.Value, av, exclude, acc, includeEqual);
            }
            return;
        }

        // Leaf or array: compare raw canonical text. No value-normalisation — every genuine
        // difference is surfaced (null vs "", open-ended-End representations, etc.); only the
        // server-assigned keys in the exclude-set are skipped. Reconcile must not silently decide
        // a difference is "expected". With includeEqual, matching leaves are emitted too (the
        // full-payload view tags them Match) — still intersection-only.
        var iv = i?.ToJsonString(NodeTextOptions);
        var av2 = a?.ToJsonString(NodeTextOptions);
        // iv is the raw canonical JSON token (quoted string / number / bool / array) — round-trippable via
        // JsonNode.Parse, unlike the Unquote'd display value. Carried as IntendedJson so reconcile-apply
        // can write the leaf without re-deriving the plan. Null when the intended leaf is JSON null
        // (indistinguishable from absent here) → the applier falls back to the capture for that row.
        if (includeEqual || !string.Equals(iv, av2, StringComparison.Ordinal))
            acc.Add((path, Unquote(iv), Unquote(av2), iv));
    }

    private static string? Unquote(string? json)
    {
        if (json == null) return null;
        if (json.Length >= 2 && json[0] == '"' && json[^1] == '"')
            return json[1..^1];
        return json;
    }
}
