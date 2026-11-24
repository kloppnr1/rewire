using System.Text.RegularExpressions;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

public sealed record PaymentMatchMpInput(
    int Supplied, int Unique, int Duplicates, int Invalid, List<string> InvalidValues,
    List<string> Ids);

public static partial class PaymentMatchMpList
{
    [GeneratedRegex("^[0-9]{18}$")]
    private static partial Regex Identification();

    public static PaymentMatchMpInput Parse(string raw)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalidValues = new List<string>();
        int supplied = 0, duplicates = 0, invalid = 0;
        var first = true;
        foreach (var token in raw.TrimStart('\uFEFF').Split(
            [',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var id = token.Trim().Trim('"', '\'');
            if (first && id.Equals("MeteringPointId", StringComparison.OrdinalIgnoreCase))
            {
                first = false;
                continue;
            }
            first = false;
            supplied++;
            if (!Identification().IsMatch(id))
            {
                invalid++;
                if (invalidValues.Count < 20) invalidValues.Add(id[..Math.Min(id.Length, 100)]);
            }
            else if (seen.Add(id)) ids.Add(id);
            else duplicates++;
        }
        return new(supplied, ids.Count, duplicates, invalid, invalidValues, ids);
    }
}
