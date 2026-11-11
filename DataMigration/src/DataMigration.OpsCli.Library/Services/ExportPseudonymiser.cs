using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Replaces personal data in a Settl export with fake values, for sample files that leave the
/// company. Fields are matched by property name wherever they sit in the tree, so the same rule
/// covers customer, payer and metering point consumer.
///
/// <para>Replacement is consistent within one run: the same real value always becomes the same fake
/// value, so a CPR shared by customer and payer still lines up. The key is random per run and never
/// written anywhere, so the fakes cannot be turned back into the real values, not even by us.</para>
///
/// <para>Identifiers keep their shape — a 10-digit CPR stays 10 digits, an 18-digit metering point
/// id stays 18 digits — so the receiver can check formats. Names, streets, emails and free text
/// become obvious placeholders. Postal code, city, municipality and country are kept: they say where
/// in the country, not who.</para>
/// </summary>
public sealed class ExportPseudonymiser
{
    /// <summary>Identifiers: every letter and digit is replaced, shape kept.</summary>
    private static readonly HashSet<string> ScrambleFields = new(StringComparer.OrdinalIgnoreCase)
    {
        // CPR / CVR
        "NationalIdentificationNumber", "FirstConsumerPartyNationalIdentificationNumber",
        "SecondConsumerPartyNationalIdentificationNumber", "VatIdentificationNumber", "OrganizationIdentifier",
        // Contact numbers
        "Phone", "Cellphone", "Fax",
        // Address parts below street level, and the street code that pins the street
        "BuildingNumber", "BuildingFloor", "RoomIdentification", "StreetCode",
        // Metering point, meter and customer numbers that lead back to a household
        "MeteringPointIdentification", "InstallationNumber", "MeterIdentifier", "CustomerNumber",
        "PayerIdentifier", "JointInvoiceNumber", "WebAccessCode",
        // Settl's copy of the source system's key — the metering point id or Xellent account number
        "ExternalIdentifier",
        // Payment and invoicing references
        "AccountNumber", "RegNumber", "Iban", "AgreementIdentifier", "OrderIdentifier", "ElectronicNumber",
        "BuyerReference", "BuyerReferenceIdentifier", "BookkeepingReference",
        // Payment agreements from the Settl CSV (export-active-customers --payment-agreements)
        "ExternalCustomerNumber", "PaymentAgreementNumber",
    };

    /// <summary>Names and free text: replaced by a placeholder carrying a short consistent tag.</summary>
    private static readonly Dictionary<string, string> PlaceholderFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FullName"] = "Name", ["FirstName"] = "First", ["LastName"] = "Last",
        ["StreetName"] = "Street", ["CitySubDivisionName"] = "District",
        ["AttentionOf"] = "Att", ["Attention"] = "Att", ["CareOf"] = "CareOf", ["OnBehalf"] = "OnBehalf",
        ["PostOfficeBox"] = "PoBox",
        ["CustomerResponsible"] = "Employee", ["ClientCoordinator"] = "Employee", ["PortfolioResponsible"] = "Employee",
        ["Notes"] = "Note", ["Remark"] = "Note", ["AdditionalInfo1"] = "Note", ["AdditionalInfo2"] = "Note",
        ["InstallationAddressNote"] = "Note", ["InvoiceText1"] = "Text", ["InvoiceText2"] = "Text",
    };

    private static readonly HashSet<string> EmailFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Email", "InvoiceCcEmail", "ReminderEmail",
    };

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public int Replaced { get; private set; }

    // Shorter real values are too likely to hit unrelated text (a 4-digit number inside a GUID).
    private const int MinEmbeddedLength = 5;

    /// <summary>
    /// Rewrites the nodes in place. The second pass removes every real value found in the nodes from
    /// free text in the same nodes — a name typed into a contract description, a street in an invoice
    /// line text. export-active-customers passes one customer at a time so a full run never holds the
    /// whole export; the fakes stay consistent across calls because the key lives for the run.
    /// </summary>
    public void Apply(IEnumerable<JsonNode?> nodes)
    {
        var all = nodes.ToList();
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in all) Walk(node, parentName: null, originals);
        var ordered = originals.Where(kv => kv.Key.Length >= MinEmbeddedLength)
            .OrderByDescending(kv => kv.Key.Length).ToList();
        if (ordered.Count == 0) return;
        foreach (var node in all) ScrubEmbedded(node, ordered);
    }

    public void Apply(JsonNode? node) => Apply([node]);

    private void Walk(JsonNode? node, string? parentName, Dictionary<string, string> originals)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) Walk(item, parentName, originals);
                break;
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is JsonValue value)
                    {
                        var replacement = Replace(name, parentName, value);
                        if (replacement != null)
                        {
                            var original = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
                            originals[original.Trim()] = replacement.ToJsonString().Trim('"');
                            obj[name] = replacement;
                            Replaced++;
                        }
                    }
                    else
                        Walk(child, name, originals);
                }
                break;
        }
    }

    private void ScrubEmbedded(JsonNode? node, List<KeyValuePair<string, string>> originals)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) ScrubEmbedded(item, originals);
                break;
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        var text = value.GetValue<string>();
                        // Settl ids and links are GUIDs; a digit run inside one is not personal data.
                        if (Guid.TryParse(text, out _) || name.Equals("Uri", StringComparison.OrdinalIgnoreCase)) continue;
                        var scrubbed = text;
                        foreach (var (original, fake) in originals)
                            if (scrubbed.Contains(original, StringComparison.OrdinalIgnoreCase))
                                scrubbed = scrubbed.Replace(original, fake, StringComparison.OrdinalIgnoreCase);
                        if (!ReferenceEquals(scrubbed, text) && scrubbed != text)
                        {
                            obj[name] = scrubbed;
                            Replaced++;
                        }
                    }
                    else
                        ScrubEmbedded(child, originals);
                }
                break;
        }
    }

    private JsonNode? Replace(string name, string? parentName, JsonValue value)
    {
        var kind = value.GetValueKind();
        if (kind is not (JsonValueKind.String or JsonValueKind.Number)) return null;
        var text = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Phone numbers and emails on a metering point consumer sit in CommunicationLines[].Value;
        // a DAR address id sits in AddressIdentifier.Identification. Both names are too generic to
        // match on their own.
        if (string.Equals(parentName, "CommunicationLines", StringComparison.OrdinalIgnoreCase)
            && (name.Equals("Value", StringComparison.OrdinalIgnoreCase) || name.Equals("Description", StringComparison.OrdinalIgnoreCase)))
            return text.Contains('@') ? FakeEmail(text) : Scramble(text);
        if (string.Equals(parentName, "AddressIdentifier", StringComparison.OrdinalIgnoreCase)
            && name.Equals("Identification", StringComparison.OrdinalIgnoreCase))
            return Scramble(text);
        // The plant's GSRN on a production metering point.
        if (string.Equals(parentName, "PowerPlantIdentification", StringComparison.OrdinalIgnoreCase)
            && name.Equals("Code", StringComparison.OrdinalIgnoreCase))
            return Scramble(text);

        if (ScrambleFields.Contains(name))
        {
            var scrambled = Scramble(text);
            return kind == JsonValueKind.Number && long.TryParse(scrambled, out var n) ? JsonValue.Create(n) : scrambled;
        }
        if (EmailFields.Contains(name)) return FakeEmail(text);
        if (PlaceholderFields.TryGetValue(name, out var label)) return $"{label}-{Tag(text, 6)}";
        return null;
    }

    /// <summary>Same shape: digit → digit, upper → upper, lower → lower; anything else kept.
    /// A leading digit never becomes 0, so numbers keep their length.</summary>
    private string Scramble(string text)
    {
        var stream = Hash(text);
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var b = stream[i % stream.Length] + i;
            result.Append(c switch
            {
                >= '0' and <= '9' => i == 0 ? (char)('1' + b % 9) : (char)('0' + b % 10),
                >= 'A' and <= 'Z' => (char)('A' + b % 26),
                >= 'a' and <= 'z' => (char)('a' + b % 26),
                _ => c,
            });
        }
        return result.ToString();
    }

    private string FakeEmail(string text) => $"user-{Tag(text.Trim().ToLowerInvariant(), 8)}@example.invalid";

    private string Tag(string text, int length) => Convert.ToHexString(Hash(text))[..length].ToLowerInvariant();

    private byte[] Hash(string text) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text));
}
