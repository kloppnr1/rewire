using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// Mapping fra én productnum til contract name. To former understøttes i profil-JSON:
/// <list type="bullet">
/// <item><c>"PROD1": "ContractName"</c> — gælder for alle paymentterms.</item>
/// <item><c>"PROD1": { "M14": "Contract A", "Q14": "Contract B", "*": "Default" }</c>
/// — opslag på paymentterm (CUSTPAYMCODE), med <c>"*"</c> som valgfri fallback.
/// Payment-term keys match case-insensitively; see <see cref="ByPaymentTerm"/>.</item>
/// </list>
/// </summary>
[JsonConverter(typeof(ProductNumContractEntryConverter))]
public class ProductNumContractEntry
{
    private Dictionary<string, string>? _byPaymentTerm;

    public string? Default { get; set; }

    /// <summary>
    /// Payment-term keys are matched case-insensitively. The profile JSON is hand-typed and
    /// Xellent's CUSTPAYMCODE is not normalised, so the two drifted apart: every live contract
    /// part says <c>Lbm+1d</c> while the config said <c>lbm+1d</c>, and ~667 accounts silently
    /// fell through to the product name with no contract behind it. Whether an account gets its
    /// contract must not depend on how somebody typed a JSON key.
    ///
    /// <para>The dictionary is rebuilt on assignment rather than only in the converter, so the
    /// rule holds however the entry was constructed. Two keys differing only by case are the same
    /// key here — the last one assigned wins, instead of the whole profile failing to load.</para>
    /// </summary>
    public Dictionary<string, string>? ByPaymentTerm
    {
        get => _byPaymentTerm;
        set => _byPaymentTerm = value == null ? null : CaseInsensitive(value);
    }

    /// <summary>Copies into an OrdinalIgnoreCase dictionary key by key — the copy-constructor
    /// throws on keys that collide once case stops counting, and a profile is not worth losing
    /// over that.</summary>
    internal static Dictionary<string, string> CaseInsensitive(IEnumerable<KeyValuePair<string, string>> source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
            result[key] = value;
        return result;
    }

    public string? Resolve(string? paymentTerm)
    {
        if (!string.IsNullOrEmpty(paymentTerm)
            && ByPaymentTerm is { Count: > 0 }
            && ByPaymentTerm.TryGetValue(paymentTerm, out var v))
            return v;
        return Default;
    }

    public static ProductNumContractEntry FromString(string contractName) =>
        new() { Default = contractName };
}

internal sealed class ProductNumContractEntryConverter : JsonConverter<ProductNumContractEntry>
{
    private const string WildcardKey = "*";

    public override ProductNumContractEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return ProductNumContractEntry.FromString(reader.GetString()!);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("ProductNum entry must be a string or an object keyed by paymentterm.");

        var byPaymentTerm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? @default = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new ProductNumContractEntry
                {
                    Default = @default,
                    ByPaymentTerm = byPaymentTerm.Count > 0 ? byPaymentTerm : null
                };

            var key = reader.GetString()!;
            reader.Read();
            var value = reader.GetString()
                ?? throw new JsonException($"ProductNum entry value for key '{key}' must be a string.");

            if (key == WildcardKey)
                @default = value;
            else
                byPaymentTerm[key] = value;
        }

        throw new JsonException("Unexpected end of JSON in ProductNum entry.");
    }

    public override void Write(Utf8JsonWriter writer, ProductNumContractEntry value, JsonSerializerOptions options)
    {
        // Serialize as compact string when only Default is present and no per-paymentterm overrides.
        if (value.ByPaymentTerm is null or { Count: 0 })
        {
            writer.WriteStringValue(value.Default);
            return;
        }

        writer.WriteStartObject();
        if (value.Default != null)
            writer.WriteString(WildcardKey, value.Default);
        foreach (var (key, contractName) in value.ByPaymentTerm)
            writer.WriteString(key, contractName);
        writer.WriteEndObject();
    }
}
