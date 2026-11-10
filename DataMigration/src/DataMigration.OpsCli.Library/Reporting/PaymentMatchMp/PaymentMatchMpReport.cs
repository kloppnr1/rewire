using System.Numerics;
using System.Globalization;
using DataMigration.OpsCli.Reporting;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

/// <summary>One column/cell contract for the complete MP-list table and its CSV download.</summary>
public static class PaymentMatchMpReport
{
    private static readonly string[] CustomerColumns =
        ["Kundenavn på betalingsaftalen i Settl", "Kundenavn i Xellent", "Kundenummer i Xellent"];

    public static string[] Columns(string mode) =>
        [.. mode == "pbs" ? PaymentMatchColumns.Pbs : PaymentMatchColumns.Shared, .. CustomerColumns];

    public static object?[] Cells(PaymentMpCoverage coverage, PaymentMpAgreement? agreement, string mode,
        string cutover, int? ownAgreements, string? agreementStatus, int? candidates,
        string? sourceKind = null, string? sourceEnd = null)
    {
        var hasAgreement = !string.IsNullOrWhiteSpace(agreement?.AgreementId);
        var matched = agreement?.Status is "Match" or "Match (fælles)" or "E-nummer fundet" or "Forskel"
            && !string.IsNullOrWhiteSpace(agreement.SettlCustomerNumber);
        var status = Status(coverage, agreement);
        var statusNote = agreement?.Status switch
        {
            "Match (fælles)" or "Match (fællesfaktura)" => "Matchet via fællesfaktura.",
            "Kun Xellent" or "Uden match (Xellent)" => "Aftalen findes i Xellent, men har intet match i Settl-eksporten.",
            "Kun Settl" or "Uden match (Settl)" => "Rækken findes kun i Settl-eksporten; ingen tilsvarende Xellent-aftale.",
            "Forskel" or "Uoverensstemmelse" => "Uoverensstemmelse mellem Xellent-aftalen og Settl-eksporten.",
            "INTET E-nummer" => "Intet E-nummer fundet i Settl-eksporten.",
            "Fejl" => null,
            _ => agreement?.IsJointMatch == true ? "Matchet via fællesfaktura." : null
        };
        // Every pair remains a separate row. Coverage diagnostics supplement the relevant row,
        // rather than combining independent agreement IDs and E-numbers into ambiguous lists.
        var notes = new[] { agreement?.Explanation, statusNote,
            coverage.Outcome is "Unresolved" or "BusinessError" or "NoAgreement" || agreement is null ? coverage.Explanation : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal);
        List<object?> cells =
        [
            coverage.Account, hasAgreement ? agreement!.SourceContractNumber : null,
            hasAgreement ? PaymentMpResult.KindLabel(sourceKind ?? agreement!.SourceKind) : null,
            hasAgreement ? sourceEnd ?? agreement!.SourceContractEnd : null,
            ownAgreements, status, string.Join("; ", notes), coverage.MeteringPointId,
            matched ? PaymentMatchColumns.MatchKeyLabel(agreement!.MatchKey, agreement.IsJointMatch) : null,
            agreement?.SettlCustomerNumber, agreement?.PaymentMethod, coverage.ContractNumber,
            hasAgreement && coverage.ContractNumber is not null ? PaymentMatchColumns.YesNo(agreement!.SourceContractNumber == coverage.ContractNumber) : null,
            cutover, hasAgreement ? agreement!.AgreementId : null, agreementStatus
        ];
        if (mode == "pbs") cells.Add(candidates);
        cells.Add(agreement?.SettlCustomerName);
        cells.Add(coverage.XellentCustomerName);
        cells.Add(coverage.Account);
        return cells.ToArray();
    }

    public static (string? Code, string? Explanation) SelectHighestENumber(IReadOnlyList<object?[]> rows)
    {
        var candidates = rows.Where(row => Equals(row[5], "Match")).ToArray();
        if (candidates.Length == 0) return (null, null);
        var numbered = new List<(object?[] Row, BigInteger Number)>();
        foreach (var row in candidates)
        {
            var text = row[9] as string;
            if (text is null || text.Length < 2 || text[0] != 'E' || text.Skip(1).Any(c => c < '0' || c > '9'))
            {
                const string error = "Fejl: Et E-nummer kan ikke læses som E efterfulgt af cifre. Intet E-nummer er valgt.";
                foreach (var candidate in candidates) { candidate[5] = "Kræver undersøgelse"; candidate[6] += " " + error; }
                return ("InvalidENumber", error);
            }
            numbered.Add((row, BigInteger.Parse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture)));
        }
        var highest = numbered.Max(pair => pair.Number);
        var winners = numbered.Where(pair => pair.Number == highest).ToArray();
        foreach (var pair in numbered.Where(pair => pair.Number < highest))
        {
            pair.Row[5] = "Ikke matchet";
            pair.Row[6] += " Et højere E-nummer med samme kundenavn findes på målepunktet.";
        }
        if (winners.Length > 1)
        {
            const string error = "Fejl: Flere rækker har det samme højeste E-nummer. Intet E-nummer er valgt.";
            foreach (var winner in winners) { winner.Row[5] = "Kræver undersøgelse"; winner.Row[6] += " " + error; }
            return ("DuplicateHighestENumber", error);
        }
        if (candidates.Length > 1)
            winners[0].Row[6] += " Højeste E-nummer med samme kundenavn på målepunktet.";
        return (null, null);
    }

    public static bool NamesMatch(string? settl, string? xellent) => !string.IsNullOrWhiteSpace(settl)
        && !string.IsNullOrWhiteSpace(xellent) && string.Equals(settl.Trim(), xellent.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Status(PaymentMpCoverage coverage, PaymentMpAgreement? agreement) => coverage.Outcome switch
    {
        "BusinessError" => "Kræver undersøgelse",
        "NoAgreement" => "Ingen relevant aftale",
        "Unresolved" => "Kræver undersøgelse",
        _ => agreement?.Status switch
        {
            "Match" or "Match (fælles)" or "Match (fællesfaktura)" or "E-nummer fundet" => "Match",
            "Kun Xellent" or "Kun Settl" or "Uden match (Xellent)" or "Uden match (Settl)" or "INTET E-nummer" => "Ikke matchet",
            "Forskel" or "Uoverensstemmelse" or "Kræver undersøgelse" => "Kræver undersøgelse",
            "Fejl" => "Kræver undersøgelse",
            _ => PaymentMpResult.OutcomeLabel(coverage.Outcome)
        }
    };

    public static List<object?[]> IncompleteRows(IEnumerable<PaymentMpCoverage> coverage, string mode, string cutover) =>
        coverage.Select(row => Cells(row, null, mode, cutover, null, null, null)).ToList();
}
