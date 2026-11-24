using static DataMigration.OpsCli.Services.ContractPartHistoryRestoration;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal sealed record AborabatRepairTarget(Guid ContractId, string ContractName,
    DateOnly Start, DateOnly End);

internal sealed record AborabatRepairItem(string MeteringPoint, Guid OldContractId,
    Guid? OldAssociationId, List<AborabatRepairTarget> Targets)
{
    public List<SettlContractLinkRow> ReplacedLinks { get; init; } = [];
    [JsonIgnore] public List<SettlContractLinkRow> KeptLinks { get; init; } = [];
}

internal sealed record AborabatRepairPlan(int Version, string Environment, string ManifestSha256,
    DateTimeOffset PreparedAt, int ExcludedCount,
    List<AborabatRepairItem> Items)
{
    public DateOnly ReferenceDate { get; init; }
    [JsonIgnore] public List<AborabatReviewRow> Review { get; init; } = [];
    [JsonIgnore] public List<SettlContractLinkRow> ExistingLinks { get; init; } = [];
    [JsonIgnore] public List<AborabatPartEvidence> Parts { get; init; } = [];
    [JsonIgnore] public List<AborabatAssignmentEvidence> Assignments { get; init; } = [];
}

internal sealed record AborabatPartEvidence(string MeteringPoint, string Account,
    string ContractNumber, string AgreementNumber, string Type, string Product,
    DateOnly Start, DateOnly End, string Record, string ProductField, string StartField,
    string MpField, string StartValue,
    string EndRecord, string EndValue);

internal sealed record AborabatAssignmentEvidence(string MeteringPoint, string Account,
    string ContractNumber, DateOnly ReferenceDate, bool AddressMatches,
    string InstallationNumber, string? Address,
    XellentChargerAddressEvidence? AddressEvidence);

internal sealed record AborabatReviewRow(string MeteringPoint, string Status, string Account,
    string ContractNumber, string PartType, string ProductNumber, string Start,
    string End, string Source, string ChangeType, string LoggedAt,
    string CountsAsPart, string EffectiveEnd, string ChargerAddressMatch, string PossibleSettlContracts,
    string Reason)
{
    public string Evidence { get; init; } = "";
    public string ContractBasis { get; init; } = "";
    public string ChargerAddress { get; init; } = "";
    public string MeteringPointAddress { get; init; } = "";
    public string AddressReason { get; init; } = "";
}

internal static class AborabatRepair
{
    // Apply the existing UA/split rules to recorded model windows, without replacing their source dates.
    internal static List<RepairExpectedContract> ResolveModelDiscounts(IEnumerable<RepairExpectedContract> contracts)
    {
        var result = new List<RepairExpectedContract>();
        foreach (var group in contracts.GroupBy(c => c.MeteringPoint))
        {
            var discounts = group.Where(c => c.ContractName == "V AboRabat" && c.Product == "V AboRabat").ToArray();
            if (discounts.Length == 0) { result.AddRange(group); continue; }
            var discountPeriods = MergePeriods(discounts.Select(c => new Period(c.Start, c.End)));
            var companions = group.Except(discounts).ToArray();
            if (!discountPeriods.All(d => Covers(d, companions.Select(c => new Period(c.Start, c.End)))))
                throw new InvalidOperationException("V AboRabat has no complete companion period in migration model.");
            foreach (var companion in companions)
            {
                var period = new Period(companion.Start, companion.End);
                var overlaps = discountPeriods.Select(d => Intersect(period, d)).Where(p => p is not null).Select(p => p!.Value).ToArray();
                if (overlaps.Length == 0) { result.Add(companion); continue; }
                var ua = companion.ContractName == "LadeAbo_Afventer_Installation" ? "LadeAbo_Afventer_Installation_ElUA"
                    : ProductTargets.TryGetValue(companion.ContractName, out var productUa) ? productUa
                    : ChargerTargets.Values.FirstOrDefault(name => name == companion.ContractName + "UA");
                if (ua is null) throw new InvalidOperationException("V AboRabat companion has no confirmed UA mapping: " + companion.ContractName);
                foreach (var overlap in overlaps)
                    result.Add(companion with { ContractName = ua, Start = overlap.Start, End = overlap.End,
                        MappingNote = $"V AboRabat vælger {ua} i rabattens periode. Datoer fra migrationsmodellen." });
                foreach (var ordinary in Subtract(period, discountPeriods))
                    result.Add(companion with { Start = ordinary.Start, End = ordinary.End,
                        MappingNote = "Almindelig kontrakt uden for V AboRabat-perioden. Datoer fra migrationsmodellen." });
            }
        }
        return result.OrderBy(c => c.MeteringPoint).ThenBy(c => c.Start).ThenBy(c => c.ContractName).ToList();
    }
    private const string DeletedPartReviewText = "Fremtidig kontraktdel slettet ved migrering";
    private static readonly DateTime BatchFrom = new(2026, 8, 31, 9, 13, 49);
    private static readonly DateTime BatchBefore = new(2026, 8, 31, 9, 59, 33);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly TimeZoneInfo DanishTime = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    private static readonly IReadOnlyDictionary<string, string> ChargerTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["OAboEje0"] = "LadeAbo_Eje0kr_VariabelUA",
        ["OAboLejMEL"] = "LadeAbo_Leje_VariabelUA",
        ["OAboEjeMEL"] = "LadeAbo_Eje_VariabelUA"
    };
    private static readonly HashSet<string> AllowedTargets =
        [.. ChargerTargets.Values, "V Kvartal UA", "V Variabel UA",
            "V Kvartal", "V Variabel", "LadeAbo_Eje0kr_Variabel",
            "LadeAbo_Leje_Variabel", "LadeAbo_Eje_Variabel",
            "LadeAbo_Afventer_Installation", "LadeAbo_Afventer_Installation_ElUA"];
    private static readonly IReadOnlyDictionary<string, string> ProductTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["V Variabel"] = "V Variabel UA",
        ["V Vari UA"] = "V Variabel UA",
        ["V S VariUA"] = "V Variabel UA",
        ["V Kvartal"] = "V Kvartal UA",
        ["V Kvt UA"] = "V Kvartal UA"
    };

    private readonly record struct Period(DateOnly Start, DateOnly End);
    private readonly record struct ElectricKey(string MeteringPoint, string Account,
        string ContractNumber, string AgreementNumber, string CompanyId,
        string ProductNumber, string AmrCode, DateTime Start);
    private readonly record struct ChargeKey(string Account, string ContractNumber,
        string AgreementNumber, string CompanyId, string ProductNumber, string AmrCode,
        DateTime Start);

    private static ElectricKey Key(XellentContractPartRow p) => new(p.MeteringPoint,
        p.Account, p.ContractNumber, p.AgreementNumber, p.CompanyId!.Trim().ToUpperInvariant(), p.ProductNumber,
        p.AmrCode!, p.Start);
    private static ChargeKey Key(XellentChargeSubscriptionRow p) => new(p.Account,
        p.ContractNumber, p.AgreementNumber!, p.CompanyId!.Trim().ToUpperInvariant(), p.ProductNumber,
        p.AmrCode!, p.Start);

    // Share the existing historical restoration rules with the missing-link repair. This is
    // reconstruction of source evidence only: it never changes Xellent or existing Settl links.
    internal static (List<XellentContractPartRow> Electricity, List<XellentChargeSubscriptionRow> Chargers,
        HashSet<string> AmbiguousPoints, HashSet<string> AmbiguousChargerAccounts,
        List<AborabatPartEvidence> ElectricityEvidence, List<AborabatPartEvidence> ChargerEvidence) RestoreCustomerPayerSources(
        IReadOnlyCollection<XellentContractPartRow> live, IReadOnlyCollection<XellentContractPartRow> logs,
        IReadOnlyCollection<XellentChargeSubscriptionRow> chargers, DateOnly reference)
    {
        if (live.Concat(logs).Any(p => p.CompanyId is null || p.AmrCode is null)
            || logs.Any(p => p.ChangeType is null || p.LoggedAt is null || p.LogRecordId is null)
            || chargers.Any(p => p.AgreementNumber is null || p.CompanyId is null || p.AmrCode is null
                || p.Source == "Log" && (p.ChangeType is null || p.LoggedAt is null || p.LogRecordId is null)))
            throw new InvalidOperationException("Historical restoration requires complete contract-part identity and ordered log evidence.");
        var cutoff = reference.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var electric = RestoreParts(live, logs, Key, p => p.ChangeType, p => p.LoggedAt, p => p.LogRecordId,
            p => p.End, p => p.Start, (p, end) => p with { End = end },
            p => p.LoggedAt >= BatchFrom && p.LoggedAt < BatchBefore && string.IsNullOrWhiteSpace(p.LogEmployeeId), cutoff);
        var charge = RestoreParts(chargers.Where(p => p.Source != "Log").ToArray(), chargers.Where(p => p.Source == "Log").ToArray(),
            Key, p => p.ChangeType, p => p.LoggedAt, p => p.LogRecordId, p => p.End, p => p.Start,
            (p, end) => p with { End = end },
            p => IsChargingMigrationChange(p.LoggedAt,p.LogEmployeeId), cutoff);
        return (electric.Parts, charge.Parts,
            electric.Ambiguous.Select(k => k.MeteringPoint).ToHashSet(StringComparer.Ordinal),
            charge.Ambiguous.Select(k => k.Account).ToHashSet(StringComparer.Ordinal),
            electric.Parts.Select(p => PartEvidence(p, electric.Evidence[Key(p)])).ToList(),
            charge.Parts.Select(p => PartEvidence("", p, charge.Evidence[Key(p)])).ToList());
    }
    public static async Task<AborabatRepairPlan> PrepareAsync(string inputDir, string environment,
        CancellationToken ct, DateOnly? requestedReferenceDate = null)
    {
        if (environment != "production") throw new InvalidOperationException("This repair plan is for the production snapshot only.");
        var snapshot = await StateSourceSnapshotStore.Open(inputDir, environment).ReadContractSourcesAsync(ct);
        if (!snapshot.HasChargerAssignments) throw new InvalidOperationException("Charger assignments are missing.");
        var availableDates = snapshot.ChargerAssignments.Select(a => a.ReferenceDate).Distinct().ToList();
        if (availableDates.Count == 0)
            throw new InvalidOperationException("The offline snapshot has no charger reference date; pull Xellent contract sources with --reference-date.");
        if (requestedReferenceDate is null && availableDates.Count != 1)
            throw new InvalidOperationException("The snapshot has multiple charger dates; specify --reference-date.");
        var referenceDate = requestedReferenceDate ?? availableDates[0];
        if (!availableDates.Contains(referenceDate))
            throw new InvalidOperationException($"No charger assignments for {referenceDate:yyyy-MM-dd} in the offline snapshot. Available: {string.Join(", ", availableDates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}.");
        var oldLinks = snapshot.SettlLinks.Where(l => l.ContractName == "V AboRabat")
            .GroupBy(l => l.MeteringPoint).ToDictionary(g => g.Key, g => g.ToList());
        if (oldLinks.Count == 0)
            throw new InvalidOperationException("No MPs linked to V AboRabat in the snapshot.");
        var idsByName = snapshot.SettlLinks.GroupBy(l => l.ContractName)
            .ToDictionary(g => g.Key, g => g.Select(l => l.ContractId).Distinct().ToList());
        if (snapshot.LoggedParts.Any(p => p.ChangeType is null)
            || snapshot.ChargeSubscriptions.Any(p => p.Source == "Log" && p.ChangeType is null))
            throw new InvalidOperationException("The offline Xellent snapshot lacks log change types. Refresh it with pull-state-sources --only xellent before preparing this repair.");
        if (snapshot.LiveParts.Any(p => p.CompanyId is null || p.AmrCode is null)
            || snapshot.LoggedParts.Any(p => p.CompanyId is null || p.AmrCode is null
                || p.LoggedAt is null || p.LogRecordId is null)
            || snapshot.ChargeSubscriptions.Any(p => p.AgreementNumber is null
                || p.CompanyId is null || p.AmrCode is null
                || (p.Source == "Log" && (p.LoggedAt is null || p.LogRecordId is null))))
            throw new InvalidOperationException("The offline Xellent snapshot lacks contract-part identity or log order fields. "
                + $"On the production host, run pull-state-sources --env production --only xellent --reference-date {referenceDate:yyyy-MM-dd} --output-dir <snapshot directory>, "
                + "then copy the updated snapshot locally before preparing this repair.");
        bool Relevant(Period period) => period.Start <= period.End
            && period.End >= referenceDate;
        var historyCutoff = referenceDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var affectedMps = oldLinks.Keys.ToHashSet(StringComparer.Ordinal);
        var electric = RestoreParts(
            snapshot.LiveParts.Where(p => affectedMps.Contains(p.MeteringPoint)).ToList(),
            snapshot.LoggedParts.Where(p => affectedMps.Contains(p.MeteringPoint)).ToList(), Key,
            p => p.ChangeType, p => p.LoggedAt, p => p.LogRecordId,
            p => p.End, p => p.Start,
            (p, end) => p with { End = end },
            p => p.LoggedAt >= BatchFrom && p.LoggedAt < BatchBefore
                && string.IsNullOrWhiteSpace(p.LogEmployeeId),
            historyCutoff);
        var liveParts = electric.Parts.Where(p => Relevant(PartPeriod(p))).ToList();
        var parts = liveParts.ToLookup(p => p.MeteringPoint);
        var inScopeLinks = oldLinks.Where(kv => parts[kv.Key].Any(p => p.ProductNumber == "V AboRabat")
                || electric.Ambiguous.Any(k => k.MeteringPoint == kv.Key && k.ProductNumber == "V AboRabat")
                || kv.Value.Any(l => LinkPeriod(l).Start <= referenceDate
                    && LinkPeriod(l).End >= referenceDate))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var inScopeMps = inScopeLinks.Keys.ToHashSet(StringComparer.Ordinal);
        if (snapshot.ChargerAssignments.Any(a => a.ReferenceDate == referenceDate
            && a.MeteringPoint is not null && inScopeMps.Contains(a.MeteringPoint)
            && a.AddressEvidence is null))
            throw new InvalidOperationException("The offline Xellent charger assignments lack address evidence. "
                + $"On the production host, run pull-state-sources --env production --only xellent --reference-date {referenceDate:yyyy-MM-dd} --output-dir <snapshot directory>, "
                + "then copy the updated snapshot locally before preparing this repair.");
        var assignments = snapshot.ChargerAssignments.Where(a => a.MeteringPoint != null)
            .ToLookup(a => a.MeteringPoint!);
        var relevantChargeContracts = snapshot.ChargerAssignments
            .Where(a => a.MeteringPoint is not null && inScopeMps.Contains(a.MeteringPoint)
                && a.ReferenceDate == referenceDate)
            .Select(a => (a.Charger.CustomerAccount, a.Charger.ContractNumber)).ToHashSet();
        var chargeLive = snapshot.ChargeSubscriptions.Where(p => p.Source != "Log"
            && relevantChargeContracts.Contains((p.Account, p.ContractNumber))).ToList();
        var chargeLogs = snapshot.ChargeSubscriptions.Where(p => p.Source == "Log"
            && relevantChargeContracts.Contains((p.Account, p.ContractNumber))).ToList();
        var charge = RestoreParts(chargeLive, chargeLogs, Key,
            p => p.ChangeType, p => p.LoggedAt, p => p.LogRecordId,
            p => p.End, p => p.Start,
            (p, end) => p with { End = end },
            p => IsChargingMigrationChange(p.LoggedAt,p.LogEmployeeId),
            historyCutoff);
        var chargeParts = charge.Parts.Where(p => Relevant(PartPeriod(p))).ToList();
        var chargeSubscriptions = chargeParts.ToLookup(p => (p.Account, p.ContractNumber));
        var items = new List<AborabatRepairItem>();
        var review = new List<AborabatReviewRow>();
        var evidenceParts = new List<AborabatPartEvidence>();
        var evidenceAssignments = new List<AborabatAssignmentEvidence>();
        var unresolvedMps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (meteringPoint, mpLinks) in inScopeLinks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var scopedAccounts = parts[meteringPoint]
                .Where(p => p.ProductNumber == "V AboRabat")
                .Select(p => p.Account).ToHashSet(StringComparer.Ordinal);
            var activeSettlDiscountLinks = mpLinks.Where(l => LinkPeriod(l).Start <= referenceDate
                && LinkPeriod(l).End >= referenceDate).ToList();
            if (scopedAccounts.Count == 0 && activeSettlDiscountLinks.Count > 0
                && !electric.Ambiguous.Any(k => k.MeteringPoint == meteringPoint
                    && k.ProductNumber == "V AboRabat"))
            {
                var expiredDiscount = electric.Parts.Where(p => p.MeteringPoint == meteringPoint
                        && p.ProductNumber == "V AboRabat" && PartPeriod(p).End < referenceDate)
                    .OrderByDescending(p => PartPeriod(p).End).FirstOrDefault();
                var sourceEvidence = expiredDiscount is null ? ""
                    : EvidenceText(electric.Evidence[Key(expiredDiscount)]);
                if (expiredDiscount is not null)
                    evidenceParts.Add(PartEvidence(expiredDiscount,
                        electric.Evidence[Key(expiredDiscount)]));
                var first = activeSettlDiscountLinks[0];
                items.Add(new AborabatRepairItem(meteringPoint, first.ContractId,
                    first.AssociationId, [])
                { ReplacedLinks = activeSettlDiscountLinks.Skip(1).ToList() });
                review.Add(new AborabatReviewRow(meteringPoint, "Resolved",
                    expiredDiscount?.Account ?? "", expiredDiscount?.ContractNumber ?? "",
                    "El-rabat", "V AboRabat",
                    expiredDiscount?.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    expiredDiscount is null ? "" : PartPeriod(expiredDiscount).End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    expiredDiscount?.Source == "Live" ? "Aktuel" : expiredDiscount?.Source ?? "",
                    "", "", "Nej",
                    expiredDiscount is null ? "" : PartPeriod(expiredDiscount).End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "Ikke relevant", "V AboRabat: slettes", "")
                {
                    Evidence = sourceEvidence,
                    ContractBasis = $"Settls V AboRabat-link dækker {referenceDate:dd-MM-yyyy}, men Xellent har ingen V AboRabat-del, der er aktiv den dag eller starter senere. Linket slettes uden ny kontrakt."
                });
                continue;
            }
            var mpParts = parts[meteringPoint]
                .Where(p => scopedAccounts.Contains(p.Account)).ToList();
            var discountGroups = mpParts.Where(p => p.ProductNumber == "V AboRabat")
                .GroupBy(p => (p.Account, p.ContractNumber, p.AgreementNumber))
                .OrderBy(g => g.Key.Account, StringComparer.Ordinal)
                .ThenBy(g => g.Key.ContractNumber, StringComparer.Ordinal).ToList();
            var chargersAtReferenceDate = assignments[meteringPoint]
                .Where(a => a.ReferenceDate == referenceDate
                    && scopedAccounts.Contains(a.Charger.CustomerAccount)).ToList();
            evidenceAssignments.AddRange(snapshot.ChargerAssignments
                .Where(a => a.MeteringPoint is not null && a.ReferenceDate == referenceDate
                    && scopedAccounts.Contains(a.Charger.CustomerAccount))
                .Select(a => new AborabatAssignmentEvidence(a.MeteringPoint!, a.Charger.CustomerAccount,
                    a.Charger.ContractNumber, a.ReferenceDate, a.Charger.AddressMatchesMeteringPoint,
                    a.Charger.InstallationNumber, a.Charger.Address, a.AddressEvidence)));
            var candidatePeriods = new List<(string Contract, Period Dates)>();
            var contractBasis = new List<string>();
            string? reason = null;
            if (electric.Ambiguous.Any(k => k.MeteringPoint == meteringPoint
                && (scopedAccounts.Count == 0 || scopedAccounts.Contains(k.Account))))
                reason = "En El-kontraktdel kan ikke matches entydigt med sin log.";
            if (chargersAtReferenceDate.Any(a => charge.Ambiguous.Any(k =>
                    k.Account == a.Charger.CustomerAccount
                    && k.ContractNumber == a.Charger.ContractNumber)))
                reason ??= "Et laderabonnement kan ikke matches entydigt med sin log.";

            void Exclude(string message)
            {
                unresolvedMps.Add(meteringPoint);
                AddReviewRows("Unresolved", message);
            }

            void AddReviewRows(string status, string message)
            {
                var match = chargersAtReferenceDate.Count switch
                {
                    0 => "Ingen lader knyttet",
                    1 => chargersAtReferenceDate[0].Charger.AddressMatchesMeteringPoint ? "Ja" : "Nej",
                    _ => "Flere ladere knyttet"
                };
                var possibleContracts = candidatePeriods.Count == 0 ? "Kan ikke udledes"
                    : string.Join(" | ", candidatePeriods.Distinct().OrderBy(c => c.Dates.Start)
                        .ThenBy(c => c.Contract, StringComparer.Ordinal)
                        .Select(c => $"{c.Contract}: {c.Dates.Start:dd-MM-yyyy} - "
                            + (c.Dates.End == DateOnly.MaxValue ? "Åben" : c.Dates.End.ToString("dd-MM-yyyy"))));
                static string EndText(Period period) => period.End == DateOnly.MaxValue
                    ? "Åben" : period.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                void AddPart(string account, string contract, string type, string product,
                    Period period, string source, string? changeType, DateTime? loggedAt,
                    DateOnly? effectiveEnd, bool countsAsPart, string evidence)
                {
                    review.Add(new AborabatReviewRow(meteringPoint, status, account, contract,
                        type, product, period.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        EndText(period), source == "Live" ? "Aktuel" : "Log",
                        changeType == DeletedPartChangeType ? DeletedPartReviewText : changeType ?? "",
                        loggedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                        countsAsPart ? "Ja" : "Nej",
                        effectiveEnd is { } end ? EndText(new Period(period.Start, end)) : "",
                        match, possibleContracts, message)
                    {
                        Evidence = evidence,
                        ContractBasis = string.Join(" | ", contractBasis.Distinct()),
                        ChargerAddress = chargersAtReferenceDate.Count == 1
                            ? chargersAtReferenceDate[0].AddressEvidence?.InstallationAddress ?? "" : "",
                        MeteringPointAddress = chargersAtReferenceDate.Count == 1
                            ? AborabatAddressEvidence.CandidateSummary(chargersAtReferenceDate[0].AddressEvidence) : "",
                        AddressReason = chargersAtReferenceDate.Count == 1
                            ? AborabatAddressEvidence.Explain(chargersAtReferenceDate[0].AddressEvidence,
                                chargersAtReferenceDate[0].Charger.AddressMatchesMeteringPoint) : ""
                    });
                }
                foreach (var part in parts[meteringPoint]
                    .Where(p => scopedAccounts.Contains(p.Account))
                    .OrderBy(p => p.Account, StringComparer.Ordinal)
                    .ThenBy(p => p.ContractNumber, StringComparer.Ordinal)
                    .ThenBy(p => p.ProductNumber, StringComparer.Ordinal)
                    .ThenBy(p => p.Start).ThenBy(p => p.Source, StringComparer.Ordinal)
                    .ThenBy(p => p.LoggedAt))
                {
                    var endEvidence = electric.Evidence[Key(part)];
                    evidenceParts.Add(PartEvidence(part, endEvidence));
                    AddPart(part.Account, part.ContractNumber,
                        part.ProductNumber == "V AboRabat" ? "El-rabat" : "Elprodukt",
                        part.ProductNumber, PartPeriod(part), part.Source, part.ChangeType,
                        part.LoggedAt, PartPeriod(part).End, true,
                        EvidenceText(endEvidence));
                }
                foreach (var part in chargersAtReferenceDate.SelectMany(a =>
                        chargeSubscriptions[(a.Charger.CustomerAccount, a.Charger.ContractNumber)])
                    .Distinct<XellentChargeSubscriptionRow>(ReferenceEqualityComparer.Instance)
                    .OrderBy(p => p.Account, StringComparer.Ordinal)
                    .ThenBy(p => p.ContractNumber, StringComparer.Ordinal)
                    .ThenBy(p => p.ProductNumber, StringComparer.Ordinal)
                    .ThenBy(p => p.Start).ThenBy(p => p.Source, StringComparer.Ordinal)
                    .ThenBy(p => p.LoggedAt))
                {
                    var endEvidence = charge.Evidence[Key(part)];
                    evidenceParts.Add(PartEvidence(meteringPoint, part, endEvidence));
                    AddPart(part.Account, part.ContractNumber, "Laderabonnement",
                        part.ProductNumber, PartPeriod(part), part.Source, part.ChangeType,
                        part.LoggedAt, PartPeriod(part).End, true,
                        EvidenceText(endEvidence));
                }
                if (review.Count == 0 || review[^1].MeteringPoint != meteringPoint)
                    review.Add(new AborabatReviewRow(meteringPoint, status, "", "", "", "", "",
                        "", "", "", "", "", "", match, possibleContracts, message));
            }
            if (mpLinks.Count != 1)
            {
                Exclude("Flere Settl-rabatlinks: afklar hvilket link der skal erstattes.");
                continue;
            }
            if (scopedAccounts.Count > 1)
            {
                Exclude("Flere Xellent-kunder har en relevant V AboRabat-del på målepunktet.");
                continue;
            }
            var old = mpLinks[0];
            if (discountGroups.Count == 0)
                reason = "Ingen V AboRabat-del er aktiv på referencedatoen eller starter senere for målepunktet.";

            foreach (var group in discountGroups)
            {
                var discountPeriods = MergePeriods(group.Select(PartPeriod));
                var companionParts = mpParts.Where(p => p.Account == group.Key.Account
                    && p.ContractNumber == group.Key.ContractNumber
                    && p.AgreementNumber == group.Key.AgreementNumber
                    && p.ProductNumber != "V AboRabat").ToList();
                var groupCandidates = new List<(string Contract, Period Dates)>();
                foreach (var companion in companionParts)
                {
                    var companionPeriod = PartPeriod(companion);
                    void AddSplit(string ua, string ordinary, Period period)
                    {
                        foreach (var discount in discountPeriods)
                            if (Intersect(period, discount) is { } discounted)
                                groupCandidates.Add((ua, discounted));
                        groupCandidates.AddRange(Subtract(period, discountPeriods).Select(p => (ordinary, p)));
                    }
                    if (ProductTargets.TryGetValue(companion.ProductNumber, out var target))
                    {
                        var ordinary = companion.ProductNumber is "V Variabel" or "V Kvartal"
                            ? companion.ProductNumber : target;
                        AddSplit(target, ordinary, companionPeriod);
                        continue;
                    }
                    if (companion.ProductNumber is not ("V LadVariP" or "V LadVarUA"))
                    {
                        reason ??= $"Elproduktet {companion.ProductNumber} kan ikke knyttes til en UA-kontrakt.";
                        continue;
                    }
                    if (chargersAtReferenceDate.Count != 1 || !chargersAtReferenceDate[0].Charger.AddressMatchesMeteringPoint)
                    {
                        var pendingLinks = snapshot.SettlLinks.Where(l => l.MeteringPoint == meteringPoint
                            && l.ContractName == "LadeAbo_Afventer_Installation").ToList();
                        var noChargerPart = chargersAtReferenceDate.Count == 0
                            && !snapshot.ChargeSubscriptions.Any(p => p.Account == group.Key.Account
                                && Intersect(PartPeriod(p), companionPeriod) is not null);
                        if (noChargerPart && pendingLinks.Any(l => Intersect(LinkPeriod(l), companionPeriod) is not null))
                        {
                            AddSplit("LadeAbo_Afventer_Installation_ElUA",
                                "LadeAbo_Afventer_Installation", companionPeriod);
                            contractBasis.Add($"Elproduktet {companion.ProductNumber} gælder {companionPeriod.Start:dd-MM-yyyy} - {ReviewEnd(companionPeriod.End)}. "
                                + "Ingen Xellent-laderdel for kunden dækker perioden. Settl har LadeAbo_Afventer_Installation på målepunktet. "
                                + "Derfor vælges afventende installation med El-UA under rabatten og almindelig afventende installation efter rabatten.");
                            continue;
                        }
                        if (noChargerPart && pendingLinks.Any(l => LinkPeriod(l).Start > companionPeriod.End))
                        {
                            AddSplit("V Variabel UA", "V Variabel", companionPeriod);
                            contractBasis.Add($"Elproduktet {companion.ProductNumber} gælder {companionPeriod.Start:dd-MM-yyyy} - {ReviewEnd(companionPeriod.End)}. "
                                + "Settls afventende installationskontrakt starter først senere. Derfor bruges El uden lader i denne tidligere periode.");
                            continue;
                        }
                        var otherAccountChargers = snapshot.ChargerAssignments.Where(a =>
                            a.ReferenceDate == referenceDate && a.MeteringPoint is not null
                            && a.MeteringPoint != meteringPoint
                            && a.Charger.CustomerAccount == group.Key.Account).ToList();
                        reason ??= chargersAtReferenceDate.Count == 0 && otherAccountChargers.Count == 1
                            ? $"Kundens lader {otherAccountChargers[0].Charger.InstallationNumber} er knyttet til målepunkt {otherAccountChargers[0].MeteringPoint} ({otherAccountChargers[0].Charger.Address}), ikke dette målepunkt."
                            : chargersAtReferenceDate.Count == 0 ? "Ingen lader knyttet sikkert til målepunktet."
                            : chargersAtReferenceDate.Count > 1 ? "Flere ladere knyttet til målepunktet."
                            : AborabatAddressEvidence.Explain(chargersAtReferenceDate[0].AddressEvidence, false);
                        continue;
                    }
                    var subscriptionCandidates = new List<(string Contract, Period Dates)>();
                    foreach (var subscription in chargeSubscriptions[(chargersAtReferenceDate[0].Charger.CustomerAccount,
                        chargersAtReferenceDate[0].Charger.ContractNumber)])
                    {
                        var subscriptionOverlap = Intersect(companionPeriod, PartPeriod(subscription));
                        if (subscriptionOverlap is null) continue;
                        if (!ChargerTargets.TryGetValue(subscription.ProductNumber, out var chargerTarget))
                        {
                            reason ??= $"Laderabonnementet {subscription.ProductNumber} kan ikke knyttes til en UA-kontrakt.";
                            continue;
                        }
                        subscriptionCandidates.Add((chargerTarget, subscriptionOverlap.Value));
                        AddSplit(chargerTarget, companion.ProductNumber == "V LadVarUA"
                            ? chargerTarget : chargerTarget[..^2], subscriptionOverlap.Value);
                    }
                    foreach (var gap in Subtract(companionPeriod, subscriptionCandidates.Select(c => c.Dates)))
                    {
                        if (gap.End < referenceDate) continue;
                        // A known subscription ending does not end the electricity supply.
                        // Before a subscription starts, only the electricity product is active.
                        AddSplit("V Variabel UA", companion.ProductNumber == "V LadVarUA"
                            ? "V Variabel UA" : "V Variabel", gap);
                        var preceding = subscriptionCandidates.Where(c => c.Dates.End < gap.Start)
                            .OrderByDescending(c => c.Dates.End).FirstOrDefault();
                        var boundary = preceding.Dates.End != default
                            ? $"Laderabonnementet slutter {preceding.Dates.End:dd-MM-yyyy}. "
                            : $"Laderabonnementet er endnu ikke startet {gap.Start:dd-MM-yyyy}. ";
                        contractBasis.Add(boundary
                            + $"Elproduktet {companion.ProductNumber} fortsætter til {ReviewEnd(companionPeriod.End)}. "
                            + $"Ingen anvendt Xellent-laderdel dækker {gap.Start:dd-MM-yyyy} - {ReviewEnd(gap.End)}. "
                            + "Derfor vælges El uden lader i denne periode; UA gælder kun, mens rabatten gælder, eller Elproduktet selv er UA.");
                    }
                }
                var discountDuringElectricity = discountPeriods.SelectMany(d => companionParts
                    .Select(c => Intersect(d, PartPeriod(c))).Where(p => p is not null).Select(p => p!.Value));
                if (!discountDuringElectricity.Where(d => d.End >= referenceDate)
                    .All(d => Covers(new Period(d.Start > referenceDate ? d.Start : referenceDate, d.End),
                        groupCandidates.Select(c => c.Dates))))
                    reason ??= "Et V AboRabat-tidsrum mangler en sikker UA-kontrakt.";
                candidatePeriods.AddRange(groupCandidates.Where(c => c.Dates.End >= referenceDate));
            }
            var targets = new List<AborabatRepairTarget>();
            foreach (var byTarget in candidatePeriods.GroupBy(c => c.Contract))
            {
                if (!idsByName.TryGetValue(byTarget.Key, out var targetIds) || targetIds.Count != 1)
                {
                    reason ??= $"Kontrakten {byTarget.Key} er ikke entydig i Settl-snapshot.";
                    continue;
                }
                targets.AddRange(MergePeriods(byTarget.Select(c => c.Dates))
                    .Select(d => new AborabatRepairTarget(targetIds[0], byTarget.Key, d.Start, d.End)));
            }
            if (targets.Any(a => targets.Any(b => a.ContractId != b.ContractId
                    && Intersect(new(a.Start, a.End), new(b.Start, b.End)) is not null)))
                reason ??= "Flere kontrakter dækker samme periode.";
            var replacedLinks = new List<SettlContractLinkRow>();
            var desired = targets.ToList();
            var scope = MergePeriods(desired.Select(t => new Period(t.Start, t.End)));
            var keptTargets = new HashSet<AborabatRepairTarget>();
            var keptLinks = new List<SettlContractLinkRow>();
            foreach (var link in snapshot.SettlLinks.Where(l => l.MeteringPoint == meteringPoint
                && AllowedTargets.Contains(l.ContractName)))
            {
                var period = LinkPeriod(link);
                if (!scope.Any(p => Intersect(p, period) is not null)) continue;
                var exact = desired.SingleOrDefault(t => t.ContractId == link.ContractId
                    && t.Start == period.Start && t.End == period.End);
                if (exact is not null)
                {
                    if (!keptTargets.Add(exact)) reason ??= "Flere ens Settl-links dækker samme periode.";
                    keptLinks.Add(link);
                    targets.Remove(exact);
                    continue;
                }
                if (link.AssociationId is null || link.AssociationId == Guid.Empty)
                    reason ??= "Et Settl-link mangler AssociationId og kan ikke erstattes.";
                replacedLinks.Add(link);
                // Preserve history before the repaired timeline, not unsupported future
                // coverage copied from Settl beyond the Xellent electricity end date.
                targets.AddRange(Subtract(period, scope).Where(p => p.End >= referenceDate
                    && p.End < scope[0].Start).Select(p =>
                    new AborabatRepairTarget(link.ContractId, link.ContractName, p.Start, p.End)));
            }
            if (snapshot.SettlLinks.Any(l => l.MeteringPoint == meteringPoint
                && l.ContractName != "V AboRabat" && !AllowedTargets.Contains(l.ContractName)
                && scope.Any(p => Intersect(p, LinkPeriod(l)) is not null)))
                reason ??= "Et andet eksisterende Settl-link overlapper den foreslåede El-periode.";
            targets = targets.Distinct().ToList();
            if (targets.Any(a => targets.Any(b => a != b
                && Intersect(new(a.Start, a.End), new(b.Start, b.End)) is not null)))
                reason ??= "Flere kontrakter dækker samme periode efter rettelsen.";
            if (reason is not null || desired.Count == 0)
            {
                Exclude(reason ?? "Ingen UA-kontrakt kan udledes af Xellent.");
                continue;
            }
            items.Add(new AborabatRepairItem(meteringPoint, old.ContractId, old.AssociationId,
                targets.OrderBy(t => t.Start).ThenBy(t => t.ContractName, StringComparer.Ordinal)
                    .ThenBy(t => t.End).ToList()) { ReplacedLinks = replacedLinks, KeptLinks = keptLinks });
            AddReviewRows("Resolved", "");
        }
        if (unresolvedMps.Count + items.Count != inScopeLinks.Count
            || items.Select(i => i.MeteringPoint).Distinct().Count() != items.Count
            || items.Select(i => i.MeteringPoint).Intersect(unresolvedMps).Any())
            throw new InvalidOperationException("The repair and unresolved rows do not partition relevant V AboRabat MPs.");
        return new AborabatRepairPlan(7, environment,
            await HashFileAsync(Path.Combine(inputDir, "manifest.json"), ct),
            DateTimeOffset.UtcNow, unresolvedMps.Count, items)
            { ReferenceDate = referenceDate, Review = review,
                ExistingLinks = snapshot.SettlLinks.Where(l => inScopeMps.Contains(l.MeteringPoint)).ToList(),
                Parts = evidenceParts, Assignments = evidenceAssignments };
    }

    public static async Task WriteReviewCsvAsync(AborabatRepairPlan plan, string output, CancellationToken ct)
    {
        if (File.Exists(output)) throw new IOException($"Output already exists: {output}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { $"Målepunkt;Status;Xellent-kundenummer;Xellent-kontraktnummer;Deltype;Produkt;Startdato;Slutdato;Kilde;Logændring;Logtidspunkt;Tæller som kontraktdel;Slutdato brugt i beregning;Laderadresse matcher målepunkt {plan.ReferenceDate:yyyy-MM-dd};Settl-kontrakter og perioder;Hvorfor uafklaret;Xellent-grundlag for slutdato;Grundlag for kontraktvalg;Laderadresse i Xellent;Målepunktets adresse i Xellent;Adressevurdering" };
        lines.AddRange(plan.Review.Select(row => string.Join(';', new[] { row.MeteringPoint,
            row.Status == "Resolved" ? "Afklaret" : "Uafklaret",
            row.Account, row.ContractNumber, row.PartType, row.ProductNumber, row.Start,
            row.End, row.Source, row.ChangeType, row.LoggedAt, row.CountsAsPart,
            row.EffectiveEnd,
            row.ChargerAddressMatch, row.PossibleSettlContracts, row.Reason,
            row.Evidence, row.ContractBasis, row.ChargerAddress,
            row.MeteringPointAddress, row.AddressReason }.Select(Csv))));
        await File.WriteAllLinesAsync(output, lines, new UTF8Encoding(true), ct);
    }

    public static async Task WritePlanAsync(AborabatRepairPlan plan, string output, CancellationToken ct)
    {
        if (File.Exists(output)) throw new IOException($"Output already exists: {output}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(plan, Json), new UTF8Encoding(false), ct);
    }

    public static Task ExportSettlImportWorkbookAsync(AborabatRepairPlan plan,
        string templatePath, string outputPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(outputPath)) throw new IOException($"Output already exists: {outputPath}");
        var fullTemplate = Path.GetFullPath(templatePath);
        var fullOutput = Path.GetFullPath(outputPath);
        if (string.Equals(fullTemplate, fullOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output workbook cannot overwrite the supplied template.");
        if (plan.Items.Any(item => item.OldAssociationId is null || item.OldAssociationId == Guid.Empty
            || item.ReplacedLinks.Any(l => l.AssociationId is null || l.AssociationId == Guid.Empty)))
            throw new InvalidOperationException("Settl contract links in the offline snapshot lack AssociationId. " +
                "Run pull-state-sources --env production --only settl with the updated OpsCli, " +
                "copy the refreshed snapshot here, then prepare the workbook again.");

        using var templateWorkbook = new XLWorkbook(fullTemplate);
        var template = templateWorkbook.Worksheets.FirstOrDefault(s => s.Name == "ContractMeteringPoints")
            ?? throw new InvalidOperationException("Template must contain a ContractMeteringPoints sheet.");
        if (template.Cell(1, 1).GetString() != "Type" || template.Cell(1, 2).GetString() != "ContractMeteringPoint"
            || template.Cell(1, 3).GetString() != "Version" || template.Cell(1, 4).GetValue<int>() != 2)
            throw new InvalidOperationException("Template is not a ContractMeteringPoints V2 workbook.");

        var headers = new[] { "Action", "ContractName", "CountryCode", "AssociationId",
            "MeteringPointIdentification", "Start", "End", "CommodityType" };
        for (var column = 0; column < headers.Length; column++)
            if (!string.Equals(template.Cell(2, column + 1).GetString(), headers[column], StringComparison.Ordinal))
                throw new InvalidOperationException($"Template column {column + 1} must be '{headers[column]}'.");
        if (template.LastRowUsed()?.RowNumber() > 2)
            throw new InvalidOperationException("Supply a blank V2 template without existing data rows.");

        // Start a clean workbook using the checked template values. This keeps the worksheet's
        // stored dimension in sync with the data rows for importers that stream the XLSX file.
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("ContractMeteringPoints");
        for (var column = 1; column <= 4; column++)
            sheet.Cell(1, column).Value = template.Cell(1, column).Value;
        for (var column = 1; column <= headers.Length; column++)
        {
            sheet.Cell(2, column).Value = template.Cell(2, column).Value;
            sheet.Column(column).Width = template.Column(column).Width;
        }

        var outputRow = 3;
        foreach (var item in plan.Items.OrderBy(i => i.MeteringPoint, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var deletion in new[] { (Id: item.OldAssociationId!.Value, Name: "V AboRabat") }
                .Concat(item.ReplacedLinks.Select(l => (Id: l.AssociationId!.Value, Name: l.ContractName)))
                .DistinctBy(l => l.Id).OrderBy(l => l.Id))
            {
                sheet.Cell(outputRow, 1).SetValue("Delete");
                sheet.Cell(outputRow, 2).SetValue(deletion.Name);
                sheet.Cell(outputRow, 4).SetValue(deletion.Id.ToString("D"));
                sheet.Cell(outputRow, 5).SetValue(item.MeteringPoint);
                outputRow++;
            }
            foreach (var target in item.Targets.OrderBy(t => t.Start)
                .ThenBy(t => t.ContractName, StringComparer.Ordinal).ThenBy(t => t.End))
            {
                sheet.Cell(outputRow, 1).SetValue("Create");
                sheet.Cell(outputRow, 2).SetValue(target.ContractName);
                sheet.Cell(outputRow, 3).SetValue("DK");
                sheet.Cell(outputRow, 5).SetValue(item.MeteringPoint);
                sheet.Cell(outputRow, 6).SetValue(target.Start.ToDateTime(TimeOnly.MinValue));
                sheet.Cell(outputRow, 6).Style.DateFormat.Format = "yyyy-mm-dd";
                if (target.End != DateOnly.MaxValue)
                {
                    sheet.Cell(outputRow, 7).SetValue(target.End.ToDateTime(TimeOnly.MinValue));
                    sheet.Cell(outputRow, 7).Style.DateFormat.Format = "yyyy-mm-dd";
                }
                sheet.Cell(outputRow, 8).SetValue("Electricity");
                outputRow++;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        workbook.SaveAs(fullOutput);
        return Task.CompletedTask;
    }

    public static async Task<AborabatRepairPlan> ReadPlanAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Version", out var version) || version.GetInt32() != 7)
            throw new InvalidOperationException("Repair plan version is outdated; prepare a new plan from the offline snapshot.");
        var plan = JsonSerializer.Deserialize<AborabatRepairPlan>(json)
            ?? throw new InvalidOperationException("Invalid repair plan.");
        if (plan.Environment != "production" || plan.Items is null || plan.Items.Count == 0
            || plan.ExcludedCount < 0 || plan.Items.Select(i => i.MeteringPoint).Distinct().Count() != plan.Items.Count
            || plan.Items.Any(i => string.IsNullOrWhiteSpace(i.MeteringPoint) || i.OldContractId == Guid.Empty
                || i.OldAssociationId is null || i.OldAssociationId == Guid.Empty
                || i.ReplacedLinks is null || i.ReplacedLinks.Any(l => l.MeteringPoint != i.MeteringPoint
                    || l.AssociationId is null || l.AssociationId == Guid.Empty
                    || !(AllowedTargets.Contains(l.ContractName) || l.ContractName == "V AboRabat"))
                || i.Targets is null || i.Targets.Any(t => !AllowedTargets.Contains(t.ContractName)
                    || t.ContractId == Guid.Empty || t.ContractId == i.OldContractId || t.Start > t.End)
                || i.Targets.GroupBy(t => (t.ContractId, t.Start, t.End)).Any(g => g.Count() > 1)))
            throw new InvalidOperationException("Repair plan scope or version is invalid.");
        return plan;
    }

    public static async Task<int> ApplyAsync(AborabatRepairPlan plan, string reportPath,
        ISettlContractClient client, CancellationToken ct)
    {
        if (plan.Items.Any(i => i.ReplacedLinks.Count != 0 || i.Targets.Count == 0
            || i.Targets.Any(t => !t.ContractName.EndsWith("UA", StringComparison.Ordinal))))
            throw new InvalidOperationException("This plan includes contract replacements or ordinary contracts. Use the Excel import; API application is not supported for this plan.");
        if (File.Exists(reportPath)) throw new IOException($"Report already exists: {reportPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await using var report = new StreamWriter(File.Create(reportPath), new UTF8Encoding(true)) { AutoFlush = true };
        await report.WriteLineAsync("MeteringPoint;TargetContract;Outcome;Reason");
        var failures = 0;
        var links = new Dictionary<Guid, List<ContractMeteringPointInfoDto>>();
        foreach (var id in plan.Items.SelectMany(i => new[] { i.OldContractId }
            .Concat(i.Targets.Select(t => t.ContractId))).Distinct())
        {
            var (found, error) = await client.GetContractMeteringPointsAsync(id, ct);
            if (error is not null || found is null) throw new InvalidOperationException($"GET contract {id} failed: {error}");
            links[id] = found;
        }
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var old = links[item.OldContractId].Where(l => l.MeteringPointIdentification == item.MeteringPoint).ToList();
            string outcome;
            string reason = "";
            try
            {
                var unexpectedTarget = item.Targets.Any(target => links[target.ContractId]
                    .Where(l => l.MeteringPointIdentification == item.MeteringPoint)
                    .Any(l => !item.Targets.Any(t => t.ContractId == target.ContractId && Matches(l, t))));
                if (old.Count > 1 || old.Any(l => l.Id != item.OldAssociationId) || unexpectedTarget)
                    outcome = "SkippedChanged";
                else if (old.Count == 0)
                    outcome = item.Targets.All(target => links[target.ContractId]
                        .Any(l => l.MeteringPointIdentification == item.MeteringPoint && Matches(l, target)))
                        ? "AlreadyApplied" : "SkippedMissingOld";
                else
                {
                    foreach (var target in item.Targets)
                    {
                        if (links[target.ContractId].Any(l => l.MeteringPointIdentification == item.MeteringPoint
                            && Matches(l, target))) continue;
                        var source = old[0];
                        var dto = new ContractMeteringPointDto
                        {
                            MeteringPointId = source.MeteringPointId,
                            MeteringPointIdentification = source.MeteringPointIdentification,
                            InstallationNumber = source.InstallationNumber,
                            Category = source.Category,
                            Start = target.Start,
                            End = target.End
                        };
                        var (added, addError) = await client.AddContractMeteringPointsAsync(target.ContractId,
                            new AddContractMeteringPointsRequest { ContractMeteringPoints = [dto] }, ct);
                        if (!added) throw new InvalidOperationException($"Add failed: {addError}");
                        var (confirmed, confirmError) = await client.GetContractMeteringPointsAsync(target.ContractId, ct);
                        if (confirmError is not null || confirmed is null
                            || !confirmed.Any(l => l.MeteringPointIdentification == item.MeteringPoint
                                && Matches(l, target)))
                            throw new InvalidOperationException($"Target add was not confirmed: {confirmError}");
                        links[target.ContractId] = confirmed;
                    }
                    var (removed, removeError) = await client.RemoveContractMeteringPointAsync(item.OldContractId, old[0].Id, ct);
                    if (!removed) throw new InvalidOperationException($"Target added; remove failed: {removeError}");
                    links[item.OldContractId].RemoveAll(l => l.Id == old[0].Id);
                    outcome = "Applied";
                }
            }
            catch (Exception ex) { outcome = "Failed"; reason = ex.Message; }
            if (outcome is "Failed" or "SkippedChanged" or "SkippedMissingOld") failures++;
            await report.WriteLineAsync($"{item.MeteringPoint};{string.Join(" | ", item.Targets.Select(t => t.ContractName).Distinct())};{outcome};{reason.Replace(';', ',').Replace('\n', ' ')}");
        }
        return failures;
    }

    private static bool Matches(ContractMeteringPointInfoDto link, AborabatRepairTarget target) =>
        LocalDate(link.Start) == target.Start
        && (link.End.Year >= 9999 ? DateOnly.MaxValue : LocalDate(link.End).AddDays(-1)) == target.End;

    private static Period PartPeriod(XellentContractPartRow part) =>
        new(DateOnly.FromDateTime(part.Start),
            part.End.Year is 1900 or >= 9999 ? DateOnly.MaxValue : DateOnly.FromDateTime(part.End));

    private static string ReviewEnd(DateOnly end) => end == DateOnly.MaxValue ? "Åben"
        : end.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static AborabatPartEvidence PartEvidence(XellentContractPartRow part,
        XellentContractPartRow endEvidence)
    {
        var identity = $"CONTRACTNUM={part.ContractNumber}, INSTAGREENUM={part.AgreementNumber}, "
            + $"PRODUCTNUM={part.ProductNumber}, START={part.Start:yyyy-MM-dd}, "
            + $"COMPANYID={part.CompanyId}, AMRCODE={part.AmrCode}";
        var record = part.Source == "Log"
            ? $"EXU_CONTRACTPARTLOG[RECID={part.LogRecordId}, TIME={part.LoggedAt:yyyy-MM-dd HH:mm:ss}]"
            : $"EXUCONTRACTPARTTABLE[{identity}]";
        var productField = record + ".PRODUCTNUM";
        var startField = record + (part.Source == "Log" ? ".CONTRACTPARTSTARTDATE" : ".STARTDATE");
        var endRecord = endEvidence.Source == "Log"
            ? $"EXU_CONTRACTPARTLOG[RECID={endEvidence.LogRecordId}, TIME={endEvidence.LoggedAt:yyyy-MM-dd HH:mm:ss}, CHANGE={endEvidence.ChangeType}].CONTRACTPARTENDDATE"
            : $"EXUCONTRACTPARTTABLE[{identity}].ENDDATE";
        return new AborabatPartEvidence(part.MeteringPoint, part.Account, part.ContractNumber,
            part.AgreementNumber, part.ProductNumber == "V AboRabat" ? "Discount" : "Electricity",
            part.ProductNumber, PartPeriod(part).Start, PartPeriod(part).End, record,
            productField, startField,
            $"EXUDELPOINT[ATTACHMENTNUM={part.AgreementNumber}].METERINGPOINT",
            part.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endRecord,
            endEvidence.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static AborabatPartEvidence PartEvidence(string mp, XellentChargeSubscriptionRow part,
        XellentChargeSubscriptionRow endEvidence)
    {
        var identity = $"CONTRACTNUM={part.ContractNumber}, INSTAGREENUM={part.AgreementNumber}, "
            + $"PRODUCTNUM={part.ProductNumber}, START={part.Start:yyyy-MM-dd}, "
            + $"COMPANYID={part.CompanyId}, AMRCODE={part.AmrCode}";
        var record = part.Source == "Log"
            ? $"EXU_CONTRACTPARTLOG[RECID={part.LogRecordId}, TIME={part.LoggedAt:yyyy-MM-dd HH:mm:ss}]"
            : $"EXUCONTRACTPARTTABLE[Charge_Abo, {identity}]";
        var productField = record + ".PRODUCTNUM";
        var startField = record + (part.Source == "Log" ? ".CONTRACTPARTSTARTDATE" : ".STARTDATE");
        var endRecord = endEvidence.Source == "Log"
            ? $"EXU_CONTRACTPARTLOG[RECID={endEvidence.LogRecordId}, TIME={endEvidence.LoggedAt:yyyy-MM-dd HH:mm:ss}, CHANGE={endEvidence.ChangeType}].CONTRACTPARTENDDATE"
            : $"EXUCONTRACTPARTTABLE[Charge_Abo, {identity}].ENDDATE";
        return new AborabatPartEvidence(mp, part.Account, part.ContractNumber,
            part.AgreementNumber ?? "", "Charger", part.ProductNumber,
            PartPeriod(part).Start, PartPeriod(part).End, record, productField, startField,
            $"Charger assignment[MP={mp}, CONTRACTNUM={part.ContractNumber}].MeteringPoint",
            part.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endRecord,
            endEvidence.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static string EvidenceText(XellentContractPartRow p) =>
        EvidenceText(p.Source, p.LogRecordId, p.LoggedAt, p.ChangeType, PartPeriod(p).End);
    private static string EvidenceText(XellentChargeSubscriptionRow p) =>
        EvidenceText(p.Source, p.LogRecordId, p.LoggedAt, p.ChangeType, PartPeriod(p).End);
    private static string EvidenceText(string source, long? id, DateTime? time, string? change, DateOnly end) =>
        source == "Log"
            ? $"Xellent-log {id}, {time:dd-MM-yyyy HH:mm:ss}: {change}. Registreret slutdato: {ReviewEnd(end)}."
            : $"Aktuel Xellent-kontraktdel. Slutdato: {ReviewEnd(end)}.";

    private static Period PartPeriod(XellentChargeSubscriptionRow part) =>
        new(DateOnly.FromDateTime(part.Start),
            part.End.Year is 1900 or >= 9999 ? DateOnly.MaxValue : DateOnly.FromDateTime(part.End));

    private static Period? Intersect(Period left, Period right)
    {
        var start = left.Start > right.Start ? left.Start : right.Start;
        var end = left.End < right.End ? left.End : right.End;
        return start <= end ? new Period(start, end) : null;
    }

    private static Period LinkPeriod(SettlContractLinkRow link) => new(LocalDate(link.Start),
        link.End.Year >= 9999 ? DateOnly.MaxValue : LocalDate(link.End).AddDays(-1));

    private static List<Period> Subtract(Period period, IEnumerable<Period> removed)
    {
        var remaining = new List<Period> { period };
        foreach (var cut in MergePeriods(removed))
        {
            var next = new List<Period>();
            foreach (var part in remaining)
            {
                if (Intersect(part, cut) is not { } overlap) { next.Add(part); continue; }
                if (part.Start < overlap.Start) next.Add(new(part.Start, overlap.Start.AddDays(-1)));
                if (part.End > overlap.End) next.Add(new(overlap.End.AddDays(1), part.End));
            }
            remaining = next;
        }
        return remaining;
    }

    private static List<Period> MergePeriods(IEnumerable<Period> periods)
    {
        var merged = new List<Period>();
        foreach (var period in periods.OrderBy(p => p.Start).ThenBy(p => p.End))
        {
            if (merged.Count > 0 && (merged[^1].End == DateOnly.MaxValue
                || period.Start <= merged[^1].End.AddDays(1)))
            {
                var previous = merged[^1];
                merged[^1] = new Period(previous.Start, previous.End > period.End ? previous.End : period.End);
            }
            else merged.Add(period);
        }
        return merged;
    }

    private static bool Covers(Period required, IEnumerable<Period> evidence)
    {
        var merged = MergePeriods(evidence);
        return merged.Any(p => p.Start <= required.Start && p.End >= required.End);
    }

    private static DateOnly LocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, DanishTime).DateTime);
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }
}
