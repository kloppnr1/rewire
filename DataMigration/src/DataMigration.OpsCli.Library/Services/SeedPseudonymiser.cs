using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Replaces personal data in <c>generate-seed</c> output before it is written, so seed files can be
/// committed. Fields are matched by property name anywhere in the tree, in the Xellent tables, the
/// Settl customer and payer, and each raw Settl metering point.
///
/// <para>Unlike <see cref="ExportPseudonymiser"/>, this keeps the relations the regression tests depend
/// on. Names, streets and free text are replaced word by word: separators, digits and letter case stay,
/// and a word becomes the same fake word wherever it appears, compared without case or accents. Two
/// values that were equal under the merge fingerprint or the case-insensitive Settl comparison are still
/// equal, and two that differed still differ. Digit runs in phone numbers, CPR and codes are replaced the
/// same way, so a CPR copied into the alternative customer number still matches.</para>
///
/// <para>The key is random per instance and never written anywhere: the fakes cannot be turned back
/// into the real values. Use one instance for a whole run so values shared across accounts line up.
/// Postal code, city, country, CVR, account numbers and ids are kept.</para>
/// </summary>
public sealed class SeedPseudonymiser
{
    /// <summary>The fictitious CPR the create pipeline assigns on purpose; it is not personal data.</summary>
    public const string SentinelCpr = "1111110000";

    private static readonly HashSet<string> WordFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Namealias", "ExuAdditionalname", "Name1", "Name2", "Instbillingname",
        "FullName", "FirstName", "LastName",
        "Street", "StreetName", "Addressstreet", "Address", "FullAddress", "Instaddressformatted",
        "Addrlocation", "Remark", "AttentionOf", "Attention", "CareOf", "OnBehalf",
    };

    private static readonly HashSet<string> DigitFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Phone", "Cellularphone", "Cellphone", "Telefax", "Fax",
        "ExuCprDisponent1", "ExuCprDisponent2", "Identificationnumber", "NationalIdentificationNumber",
        "FirstConsumerPartyNationalIdentificationNumber", "SecondConsumerPartyNationalIdentificationNumber",
        "Exualternativecustomernumber", "WebAccessCode",
    };

    private static readonly HashSet<string> EmailFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Email", "InvoiceCcEmail", "ReminderEmail",
    };

    /// <summary>Values here are place, not person; their words stay readable in composite addresses.</summary>
    private static readonly HashSet<string> PlaceFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "City", "County", "CitySubDivisionName", "Countryregionid", "Country", "State",
    };

    /// <summary>Settl's system objects in a raw metering point: their names are not people.</summary>
    private static readonly HashSet<string> SystemParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Commands", "Description",
    };

    private static readonly Regex Word = new(@"[^\W\d_]+", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _digitRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);
    private readonly HashSet<string> _placeWords = new(StringComparer.Ordinal);

    public int Replaced { get; private set; }

    /// <summary>Pseudonymises a JSON document and returns it indented.</summary>
    public string ApplyJson(string json)
    {
        var node = JsonNode.Parse(json);
        Apply(node);
        return node?.ToJsonString(Indented) ?? json;
    }

    /// <summary>Pseudonymises the node in place.</summary>
    public void Apply(JsonNode? node)
    {
        CollectPlaceWords(node, parentName: null);
        Walk(node, parentName: null);
    }

    private void CollectPlaceWords(JsonNode? node, string? parentName)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) CollectPlaceWords(item, parentName);
                break;
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (child is JsonValue value && PlaceFields.Contains(name) && value.TryGetValue<string>(out var text))
                        foreach (Match m in Word.Matches(text)) _placeWords.Add(WordKey(m.Value));
                    else
                        CollectPlaceWords(child, name);
                }
                break;
        }
    }

    private void Walk(JsonNode? node, string? parentName)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) Walk(item, parentName);
                break;
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is not JsonValue value)
                    {
                        Walk(child, name);
                        continue;
                    }
                    if (!value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) continue;
                    var replacement = Replace(name, parentName, text);
                    if (replacement != null && replacement != text)
                    {
                        obj[name] = replacement;
                        Replaced++;
                    }
                }
                break;
        }
    }

    private string? Replace(string name, string? parentName, string text)
    {
        // Phone numbers and emails on a metering point consumer sit in CommunicationLines[].Value.
        if (string.Equals(parentName, "CommunicationLines", StringComparison.OrdinalIgnoreCase)
            && name.Equals("Value", StringComparison.OrdinalIgnoreCase))
            return text.Contains('@') ? FakeEmail(text) : FakeDigitsAndLetters(text, scrambleLetters: true);
        if (parentName != null && SystemParents.Contains(parentName)) return null;
        if (EmailFields.Contains(name)) return FakeEmail(text);
        if (DigitFields.Contains(name))
            return FakeDigitsAndLetters(text, scrambleLetters: name.Equals("WebAccessCode", StringComparison.OrdinalIgnoreCase));
        if (WordFields.Contains(name)) return FakeWords(text, minLength: 3);
        return null;
    }

    /// <summary>Words of at least <paramref name="minLength"/> letters become fake words; the rest stays.</summary>
    private string FakeWords(string text, int minLength) => Word.Replace(text, m =>
    {
        var word = m.Value;
        if (word.Length < minLength) return word;
        var key = WordKey(word);
        if (_placeWords.Contains(key)) return word;
        if (!_words.TryGetValue(key, out var fake))
            _words[key] = fake = Issue(seed => MakeWord(seed, Math.Clamp(word.Length, 4, 10)), "w:" + key);
        return MatchCase(word, fake);
    });

    private string FakeEmail(string text)
    {
        var at = text.LastIndexOf('@');
        if (at < 0) return FakeWords(text, minLength: 1);
        var local = FakeWords(Digits.Replace(text[..at], m => FakeDigitRun(m.Value)), minLength: 1);
        var domain = text[(at + 1)..];
        if (!domain.EndsWith("example.dk", StringComparison.OrdinalIgnoreCase))
        {
            if (!_domains.TryGetValue(domain.Trim(), out var fakeDomain))
                _domains[domain.Trim()] = fakeDomain = Issue(seed => MakeWord(seed, 2), "d:" + domain.Trim().ToLowerInvariant()) + ".example.dk";
            domain = fakeDomain;
        }
        return local + "@" + domain;
    }

    /// <summary>Digit runs are replaced consistently; letters are scrambled only in codes such as the
    /// web access code. The sentinel CPR stays.</summary>
    private string FakeDigitsAndLetters(string text, bool scrambleLetters)
    {
        if (text.Trim() == SentinelCpr) return text;
        var digits = Digits.Replace(text, m => m.Value == SentinelCpr ? m.Value : FakeDigitRun(m.Value));
        if (!scrambleLetters || !digits.Any(char.IsLetter)) return digits;
        var stream = Hash("l:" + text);
        var sb = new StringBuilder(digits.Length);
        for (var i = 0; i < digits.Length; i++)
        {
            var c = digits[i];
            var b = stream[i % stream.Length] + i;
            sb.Append(c switch
            {
                >= 'A' and <= 'Z' => (char)('A' + b % 26),
                >= 'a' and <= 'z' => (char)('a' + b % 26),
                _ => c,
            });
        }
        return sb.ToString();
    }

    private string FakeDigitRun(string run)
    {
        if (_digitRuns.TryGetValue(run, out var fake)) return fake;
        return _digitRuns[run] = Issue(seed =>
        {
            var sb = new StringBuilder(run.Length);
            for (var i = 0; i < run.Length; i++)
                sb.Append(i == 0 && run[0] != '0' ? (char)('1' + seed[i % seed.Length] % 9) : (char)('0' + seed[i % seed.Length] % 10));
            return sb.ToString();
        }, "n:" + run, avoid: run);
    }

    /// <summary>Draws fakes until one is unused, so two different real values never share a fake.</summary>
    private string Issue(Func<byte[], string> make, string source, string? avoid = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = make(Hash(source + "#" + attempt));
            if (candidate != avoid && _issued.Add(candidate)) return candidate;
        }
    }

    private static string MakeWord(byte[] seed, int length)
    {
        const string consonants = "bdfghjklmnprstv";
        const string vowels = "aeiou";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(i % 2 == 0 ? consonants[seed[i % seed.Length] % consonants.Length] : vowels[seed[i % seed.Length] % vowels.Length]);
        return sb.ToString();
    }

    private static string MatchCase(string source, string fake)
    {
        if (source.Length > 1 && source.All(c => !char.IsLetter(c) || char.IsUpper(c))) return fake.ToUpperInvariant();
        return char.IsUpper(source[0]) ? char.ToUpperInvariant(fake[0]) + fake[1..] : fake;
    }

    /// <summary>Lower case without accents, as the merge fingerprint compares.</summary>
    private static string WordKey(string word)
    {
        var decomposed = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    private byte[] Hash(string text) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text));
}
