using System.Globalization;
using System.Text;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

public sealed record PaymentMpCoverage(
    string MeteringPointId, string? Account, string? ContractNumber,
    string? ContractStart, string? ContractEnd, string Outcome, int Agreements,
    string Explanation)
{
    public string? SettlCustomerName { get; init; }
    public string? XellentCustomerName { get; init; }
    public string? CustomerType { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorExplanation { get; init; }
}

public sealed record PaymentMpAgreement(
    string MeteringPointId, string Account, string SourceContractNumber, string? SourceKind,
    string AgreementId, string? SettlCustomerNumber, string? SettlAgreementNumber,
    string? MatchKey, string Status, string? SourceContractEnd, string? OwningContract,
    bool? IsOnOwningContract, string? PaymentMethod, string Explanation, bool NeedsReview)
{
    public string? SettlCustomerName { get; init; }
    public string? XellentCustomerName { get; init; }
    public string? CustomerType { get; init; }
    public string? SourceCommodity { get; init; }
    public string? ErrorCode { get; init; }
    public bool EvidenceOnly { get; init; }
    public int? PbsTransactionCode { get; init; }
    public bool IsJointMatch { get; init; }
}

public sealed record PaymentMpResult(
    string Environment, string Mode, string Cutover, string MpFileName, string SettlFileName,
    DateTime CreatedAt, int Supplied, int Unique, int Duplicates,
    List<PaymentMpCoverage> Coverage, List<PaymentMpAgreement> Agreements)
{
    public bool IncludeTerminated { get; init; }
    public List<object?[]> ReportRows { get; init; } = [];
    public string[] ReportColumns => PaymentMatchMpReport.Columns(Mode);
    public string ReportCsv() => Csv(ReportColumns, ReportRows);
    public object Summary => new
    {
        Environment, Mode, Cutover, IncludeTerminated, MpFileName, SettlFileName, CreatedAt, Supplied, Unique, Duplicates,
        matched = Coverage.Count(r => r.Outcome == "Matched"),
        unmatched = Coverage.Count(r => r.Outcome == "Unmatched"),
        noAgreement = Coverage.Count(r => r.Outcome == "NoAgreement"),
        unresolved = Coverage.Count(r => r.Outcome == "Unresolved"),
        businessErrors = Coverage.Count(r => r.ErrorCode == PaymentMpSelection.BusinessErrorCode),
        agreements = Agreements.Count,
        needsReview = ReportRows.Where(r => Equals(r[5], "Kræver undersøgelse") && !string.IsNullOrWhiteSpace(r[1] as string) && !string.IsNullOrWhiteSpace(r[14] as string))
            .Select(r => (Account: r[0], Source: r[1], Agreement: r[14], Mp: r[14] is null ? r[7] : null)).Distinct().Count()
    };

    public string FileName(string kind) =>
        $"payment-match-{Mode}-mp-list-{Environment}-{Cutover}-{CreatedAt:yyyyMMdd-HHmmss}-{kind}.csv";

    public static string OutcomeLabel(string value) => value switch
    {
        "Matched" => "Match",
        "Unmatched" => "Ikke matchet",
        "NoAgreement" => "Ingen relevant aftale",
        "BusinessError" => "Fejl: Betaling kun på vand/varme",
        _ => "Kræver undersøgelse"
    };

    public string CoverageCsv() => Csv(
        ["Målepunkt", "Kundenummer i Xellent", "Ejerkontrakt", "Kontraktstart", "Kontraktslut",
            "Resultat", "Aftaler", "Forklaring", "Miljø", "Cutover", "MP-fil", "Settl-fil", "Betalingsform", "Kundenavn i Xellent", "Kundetype", "Fejlkode", "Fejlbeskrivelse", "Kundenavn på betalingsaftalen i Settl"],
        Coverage.Select(r => new object?[] { r.MeteringPointId, r.Account, r.ContractNumber,
            r.ContractStart, r.ContractEnd, OutcomeLabel(r.Outcome), r.Agreements, r.Explanation,
            Environment, Cutover, MpFileName, SettlFileName, Mode == "pbs" ? "PBS" : "MobilePay", r.XellentCustomerName, CustomerTypeLabel(r.CustomerType), r.ErrorCode, r.ErrorExplanation, r.SettlCustomerName }));

    public string AgreementsCsv() => Csv(
        ["Målepunkt", "Kundenummer i Xellent", "Aftale fundet på kontrakt", "Kontrakttype",
            "Aftale-ID", "E-nummer i Settl", "Betalingsaftale i Settl", "Nøgle der matchede",
            "Status", "Kontraktslut", "Ejerkontrakt", "På ejerkontrakt", "Betalingsform",
            "Forklaring", "Kræver gennemgang", "Miljø", "Cutover", "MP-fil", "Settl-fil", "Kundenavn på betalingsaftalen i Settl", "Kundenavn i Xellent", "Kundetype", "Forsyningsart", "Fejlkode", "Kun til gennemgang", "PBS-transaktionskode"],
        Agreements.Select(r => new object?[] { r.MeteringPointId, r.Account, r.SourceContractNumber,
            KindLabel(r.SourceKind), r.AgreementId, r.SettlCustomerNumber, r.SettlAgreementNumber,
            r.MatchKey, r.Status, r.SourceContractEnd, r.OwningContract,
            r.IsOnOwningContract is null ? "" : r.IsOnOwningContract.Value ? "Ja" : "Nej",
            r.PaymentMethod, r.Explanation, r.NeedsReview ? "Ja" : "Nej", Environment, Cutover,
            MpFileName, SettlFileName, r.SettlCustomerName, r.XellentCustomerName, CustomerTypeLabel(r.CustomerType), r.SourceCommodity, r.ErrorCode, r.EvidenceOnly ? "Ja" : "Nej", r.PbsTransactionCode }));

    public static string CustomerTypeLabel(string? type) => type == "Business" ? "Erhverv" : type == "Private" ? "Privat" : "";

    public static string KindLabel(string? kind) => kind switch
    {
        "MainContract" => "Hovedkontrakt", "SubContract" => "Underkontrakt",
        "Standalone" => "Enkeltkontrakt", _ => ""
    };

    private static string Csv(string[] columns, IEnumerable<object?[]> rows)
    {
        var builder = new StringBuilder().AppendLine(string.Join(';', columns));
        foreach (var row in rows)
            builder.AppendLine(string.Join(';', row.Select(Escape)));
        return builder.ToString();
    }

    private static string Escape(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Excel's leading apostrophe keeps 18-digit IDs intact when opening the CSV directly.
        if (text.Length == 18 && text.All(char.IsAsciiDigit)) text = "'" + text;
        if (text.StartsWith('=') || text.StartsWith('+') || text.StartsWith('-') || text.StartsWith('@'))
            text = "'" + text;
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
