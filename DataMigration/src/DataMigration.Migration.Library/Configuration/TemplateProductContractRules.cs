using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// Which national identifier the customer carries. Same three answers, same rule and same order as
/// OpsCli's <c>CustomerIdentityKind</c>: a valid 8-digit CVR makes the customer a company, a valid
/// CPR and no CVR makes it a person, anything else is unidentified.
///
/// <para>The create path reads it off the already-mapped domain customer (<c>Customer.Cvr</c> is
/// null unless it validated as 8 digits), so no second trip to Xellent and no second rule.</para>
/// </summary>
public enum CustomerIdentity
{
    Blank,
    Cvr,
    Cpr
}

/// <summary>
/// One row of the create path's template+product table: which Settl contract the combination maps
/// to, per customer identification.
///
/// <para>Two forms in JSON, mirroring <see cref="ProductNumContractEntry"/>:</para>
/// <list type="bullet">
/// <item><c>"V Variabel": "V ErhVari"</c> — the same contract whoever the customer is.</item>
/// <item><c>"V Variabel": { "cvr": "V ErhVari" }</c> — by identification, with <c>"*"</c> as an
/// optional default. An identification the entry says nothing about does NOT match: resolution
/// falls through to the next rule, which is what leaves private customers on the product-name
/// fallback they are already correct on.</item>
/// </list>
/// </summary>
[JsonConverter(typeof(TemplateProductContractEntryConverter))]
public sealed class TemplateProductContractEntry
{
    /// <summary>Contract for a customer with a valid CVR.</summary>
    public string? Cvr { get; set; }

    /// <summary>Contract for a customer with a CPR and no CVR.</summary>
    public string? Cpr { get; set; }

    /// <summary>Contract for a customer with neither.</summary>
    public string? Blank { get; set; }

    /// <summary>The <c>"*"</c> entry: used when the identification has no entry of its own.</summary>
    public string? Default { get; set; }

    public string? Resolve(CustomerIdentity identity)
    {
        var byIdentity = identity switch
        {
            CustomerIdentity.Cvr => Cvr,
            CustomerIdentity.Cpr => Cpr,
            _ => Blank
        };
        return string.IsNullOrEmpty(byIdentity) ? NullIfEmpty(Default) : byIdentity;
    }

    public static TemplateProductContractEntry FromString(string contractName) =>
        new() { Default = contractName };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// The create path's template+product table, compiled for lookup: Xellent contract template plus the
/// product on the same contract part decide the Settl contract, and the customer's identification
/// picks between two answers for the same combination.
///
/// <para>It exists because one product legitimately maps two ways. <c>V Variabel</c> on template
/// <c>V_Privat</c> is right as it stands for the ~17,600 private customers who belong on the Settl
/// contract of the same name, and wrong for the ~40 companies on the same template, who belong on
/// <c>V ErhVari</c>. A plain product mapping cannot say that: it would move all of them.</para>
///
/// <para>Same shape and same vocabulary as OpsCli's <c>templateProducts</c> in
/// <c>settlement-config.json</c> — template → product pattern → value, patterns ordered
/// most-specific-first, wildcards <c>Spot*</c> and <c>*</c>, keys matched case-insensitively. The
/// two tables are deliberately not the same type: OpsCli's row carries a Settl <i>segment</i> plus
/// an expected contract and is CVR-only by design, while this one carries a contract per
/// identification. Sharing the record would force a meaningless segment into every migration
/// profile. What must not diverge — template key normalisation and pattern matching — is shared,
/// through <see cref="ContractTemplateKey"/> and <see cref="ProductPattern"/>.</para>
/// </summary>
public sealed class TemplateProductContractRules
{
    private readonly Dictionary<string, List<(string Pattern, TemplateProductContractEntry Entry)>> _byTemplate;

    private TemplateProductContractRules(
        Dictionary<string, List<(string, TemplateProductContractEntry)>> byTemplate) => _byTemplate = byTemplate;

    /// <summary>
    /// Compiles the raw profile/appsettings table. Returns null when nothing is configured, so a
    /// caller can skip the whole dimension rather than resolving against an empty table.
    /// </summary>
    public static TemplateProductContractRules? Build(
        IReadOnlyDictionary<string, Dictionary<string, TemplateProductContractEntry>>? raw)
    {
        if (raw is null || raw.Count == 0) return null;

        var byTemplate = new Dictionary<string, List<(string, TemplateProductContractEntry)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (template, products) in raw)
        {
            if (string.IsNullOrWhiteSpace(template) || products is not { Count: > 0 }) continue;

            // Specificity is fixed here, not at match time, so a reordered file cannot change which
            // rule wins.
            var patterns = products
                .Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value is not null)
                .OrderBy(p => ProductPattern.SpecificityRank(p.Key))
                .ThenByDescending(p => p.Key.Length)
                .Select(p => (p.Key, p.Value))
                .ToList();
            if (patterns.Count == 0) continue;

            var key = ContractTemplateKey.Normalize(template);
            if (byTemplate.TryGetValue(key, out var existing))
            {
                // "V Privat" and "V_Privat" are the same template — both spellings are live in
                // Xellent. Merging beats letting the later block silently drop the earlier one.
                existing.AddRange(patterns);
                existing.Sort((a, b) =>
                {
                    var rank = ProductPattern.SpecificityRank(a.Item1).CompareTo(ProductPattern.SpecificityRank(b.Item1));
                    return rank != 0 ? rank : b.Item1.Length.CompareTo(a.Item1.Length);
                });
                continue;
            }
            byTemplate[key] = patterns;
        }

        return byTemplate.Count > 0 ? new TemplateProductContractRules(byTemplate) : null;
    }

    /// <summary>
    /// The Settl contract this template + product + identification maps to, or null when the table
    /// says nothing about the combination — an unknown template, an unmatched product, or a matched
    /// product with no answer for this identification. Null means "ask the next rule", never "no
    /// contract".
    /// </summary>
    public string? Resolve(string? contractTemplate, string? productNum, CustomerIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(contractTemplate) || string.IsNullOrEmpty(productNum)) return null;
        if (!_byTemplate.TryGetValue(ContractTemplateKey.Normalize(contractTemplate), out var patterns))
            return null;

        foreach (var (pattern, entry) in patterns)
        {
            if (!ProductPattern.Matches(pattern, productNum)) continue;
            if (entry.Resolve(identity) is { Length: > 0 } contract) return contract;
        }
        return null;
    }
}

internal sealed class TemplateProductContractEntryConverter : JsonConverter<TemplateProductContractEntry>
{
    public override TemplateProductContractEntry Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return TemplateProductContractEntry.FromString(reader.GetString()!);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("TemplateProducts entry must be a string or an object keyed by cvr/cpr/blank/*.");

        var entry = new TemplateProductContractEntry();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return entry;

            var key = reader.GetString()!;
            reader.Read();
            var value = reader.GetString()
                ?? throw new JsonException($"TemplateProducts entry value for key '{key}' must be a string.");

            Apply(entry, key, value);
        }

        throw new JsonException("Unexpected end of JSON in TemplateProducts entry.");
    }

    /// <summary>Identification keys are matched case-insensitively, like every other key in these
    /// tables. An unknown key is a typo that would otherwise silently do nothing.</summary>
    internal static void Apply(TemplateProductContractEntry entry, string key, string value)
    {
        if (string.Equals(key, "cvr", StringComparison.OrdinalIgnoreCase)) entry.Cvr = value;
        else if (string.Equals(key, "cpr", StringComparison.OrdinalIgnoreCase)) entry.Cpr = value;
        else if (string.Equals(key, "blank", StringComparison.OrdinalIgnoreCase)) entry.Blank = value;
        else if (key == "*") entry.Default = value;
        else throw new JsonException(
            $"TemplateProducts entry has unknown key '{key}' (expected cvr, cpr, blank or *).");
    }

    public override void Write(
        Utf8JsonWriter writer, TemplateProductContractEntry value, JsonSerializerOptions options)
    {
        if (value.Cvr is null && value.Cpr is null && value.Blank is null)
        {
            writer.WriteStringValue(value.Default);
            return;
        }

        writer.WriteStartObject();
        if (value.Default != null) writer.WriteString("*", value.Default);
        if (value.Cvr != null) writer.WriteString("cvr", value.Cvr);
        if (value.Cpr != null) writer.WriteString("cpr", value.Cpr);
        if (value.Blank != null) writer.WriteString("blank", value.Blank);
        writer.WriteEndObject();
    }
}
