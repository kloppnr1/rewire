using System.Globalization;
using System.Text;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Computes a canonical "fingerprint" string for a customer used as the merge key
/// (combined with CVR) when grouping Xellent accounts into Settl customers.
///
/// <para>Two customers with the same CVR but different fingerprints become separate
/// Settl-customers; same CVR + same fingerprint merge into one (lowest Accountnum wins).</para>
///
/// <para>Concatenates Name | AdditionalName | Street | Zipcode | City | Email with '|',
/// then accent-folds (NFD + drop non-spacing marks), lowercases, and strips
/// whitespace + punctuation: space, tab, CR, LF, NBSP ( ), '/', '.', ',', '-',
/// '&amp;', '\'', '"', '(', ')'.</para>
///
/// <para>Email participates in the merge key: same-CVR accounts whose contact email
/// differs do NOT merge (different contact person =&gt; separate Settl-customer). EAN is
/// deliberately NOT part of the fingerprint — contract-level EAN variation is not a
/// valid basis for splitting a legal entity.</para>
/// </summary>
public static class FingerprintCalculator
{
    private static readonly HashSet<char> StripChars = new()
    {
        ' ', '\t', '\n', '\r', ' ',
        '/', '.', ',', '-', '&', '\'', '"', '(', ')'
    };

    public static string Compute(
        string? name,
        string? additionalName,
        string? street,
        string? zipcode,
        string? city,
        string? email = null)
    {
        var raw = string.Join('|',
            name ?? "",
            additionalName ?? "",
            street ?? "",
            zipcode ?? "",
            city ?? "",
            email ?? "");

        var folded = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (StripChars.Contains(c))
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
