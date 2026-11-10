using System.Globalization;

namespace DataMigration.OpsCli.Services;

internal static class RepairEvidenceReports
{
    private static string[] ContractHeaders(DateOnly? reference) => ["Målepunkt", "Status", "Xellent-kundenummer", "Xellent-kontraktnummer", "Deltype", "Produkt", "Startdato", "Slutdato", "Kilde", "Logændring", "Logtidspunkt", "Tæller som kontraktdel", "Slutdato brugt i beregning", $"Laderadresse matcher målepunkt {reference:yyyy-MM-dd}", "Settl-kontrakter og perioder", "Hvorfor uafklaret", "Xellent-grundlag for slutdato", "Grundlag for kontraktvalg", "Laderadresse i Xellent", "Målepunktets adresse i Xellent", "Adressevurdering"];
    internal static string Actions(CustomerPayerRepairDecision d)
    {
        if (d.Status != "Ready") return "Afventer afklaring.";
        return $"Kunde: {Action(d.CreateCustomer)}. Betaler: {Action(d.CreatePayer)}. Kunde-link: {Action(d.CreateCustomerLink)}. Betaler-link: {Action(d.CreatePayerLink)}. Kontrakt-link: {d.Contracts.Count(c => c.Create)} oprettes, {d.Contracts.Count(c => !c.Create)} beholdes.";
    }
    internal static string Reason(string reason)
    {
        const string catalog = "Expected contract is absent or ambiguous in Settl catalog: ";
        const string manualUa = "Manual UA follow-up after import: ";
        if (reason.StartsWith(manualUa, StringComparison.Ordinal))
            return "Manuel opfølgning efter import: UA-periode " + reason[manualUa.Length..];
        if (reason.StartsWith(catalog, StringComparison.Ordinal)) return "Kontrakt mangler eller er tvetydig i Settl: " + reason[catalog.Length..] + ".";
        if (reason.Contains("V AboRabat has no unique UA contract", StringComparison.Ordinal)) return "V AboRabat: entydig UA-kontrakt mangler.";
        return reason switch
        {
            "Ready" => "Afklaret.",
            "Delivery history does not establish the old customer's end date." => "Leveringshistorikken viser ikke entydigt, hvornår den gamle kundes links skal slutte.",
            "Original-account evidence missing; recapture without representative merging." => "Grundlag for originalkontoen mangler.",
            "Finite or inactive delivery requires period review." => "Afsluttet levering skal afklares.",
            "Source capture reference date is after the required ownership date." => "Kildedatoen ligger efter ejerskiftet.",
            "A later source owner requires period review." => "Senere ejerskifte skal afklares.",
            "Source payer ownership is inconsistent." => "Betalerens kunde er uklar.",
            "Customer source account disagrees with route evidence." => "Kildedata peger på forskellige kunder.",
            "Duplicate existing entity identifiers." => "Flere poster har samme nummer.",
            "Legacy unpadded identifier exists; resolve identifier policy before import." => "Et eksisterende nummer mangler det foranstillede 9.",
            "Invalid original account identifier." => "Originalkontoens nummer er ugyldigt.",
            "Contract evidence missing; recapture source mappings and Settl catalog/associations." => "Kontraktgrundlag mangler.",
            "Duplicate source contract periods require review." => "Gentagne kontraktperioder skal afklares.",
            "Payer identifier claimed by multiple customers." => "Flere kunder bruger samme betalernummer.",
            "Customer identity type conflicts." => "Kundetype og identifikation stemmer ikke overens.",
            "Source billing channel is not supported by the supplied Excel template." => "Faktureringsformen understøttes ikke af skabelonen.",
            "PDF invoicing requires an email." => "PDF-fakturering kræver en e-mailadresse.",
            "Delivery history missing or failed." => "Leveringshistorik mangler.",
            "First delivery is not a proven supplier change." => "Leverandørskifte er ikke bekræftet.",
            "Source customer/payer routing is missing or ambiguous at supplier change." => "Kunde eller betaler ved leverandørskiftet er uklar.",
            "Source customer/payer differs from original account; no representative or payer substitution is allowed." => "Kunde eller betaler afviger fra originalkontoen.",
            "Original source account is missing or ambiguous." => "Originalkontoen mangler eller er tvetydig.",
            "No proven contract covers the supplier-change start." => "Ingen bekræftet kontrakt dækker leverandørskiftet.",
            "Conflicting, overlapping or later associations require period review." => "Relationernes perioder skal afklares.",
            "Existing contract periods overlap partially or conflict; period review required." => "Kontraktperioderne overlapper eller er modstridende.",
            "An existing contract outside expected periods requires review." => "En eksisterende kontrakt ligger uden for de forventede perioder.",
            "Existing customer does not provably cover the required period." => "Kundens periode er ikke bekræftet.",
            "Existing payer ownership or period conflicts." => "Betalerens kunde eller periode er modstridende.",
            "Required customer details are missing." => "Kundeoplysninger mangler.",
            "Required payer details are missing." => "Betaleroplysninger mangler.",
            "Source customer/payer values require review." => "Kildeoplysninger for kunde eller betaler skal afklares.",
            "Source MP address requires review." => "Målepunktets adresse skal afklares.",
            "Unattributed source finding requires review." => "En kildefejl kan ikke knyttes entydigt til et målepunkt.",
            "MP missing from completed migration reports." => "Målepunkt mangler i afsluttede migrationsrapporter.",
            "Migration report information missing or ambiguous." => "Migrationsrapportens oplysninger mangler eller er uklare.",
            "Migration customer/payer relationship dates missing." => "Migrationsrapportens datoer for kunde og betaler mangler.",
            "Migration customer/payer record missing or ambiguous." => "Kunde eller betaler mangler eller er uklar i migrationsrapporten.",
            "Completed migration customer/payer records disagree." => "Migrationsrapporter viser forskellige kunde- eller betaleroplysninger.",
            "Completed migration reports disagree." => "Afsluttede migrationsrapporter viser forskellige resultater.",
            "Migration report and Xellent account disagree." => "Migrationskontoen er ikke fundet til målepunktet i Xellent-data.",
            "Latest completed migration report selected." => "Seneste afsluttede migrationsrapport bruges.",
            "Customer already exists in Settl." => "Kunden findes allerede i Settl.",
            "Payer already exists in Settl." => "Betaleren findes allerede i Settl.",
            "Customer links already exist in Settl." => "Kunde-link findes allerede i Settl.",
            "Payer links already exist in Settl." => "Betaler-link findes allerede i Settl.",
            "Metering point missing in Settl snapshot." => "Målepunktet findes ikke i Settl-data.",
            "Migration report and Xellent contracts or dates disagree." => "Migrationsrapport og Xellent viser forskellige kontrakter eller datoer.",
            "Ambiguous historical electricity or charger contract part." => "Historikken for en kontraktdel er ikke entydig.",
            "Restored part has no unique domain contract/payment context." => "Kontraktdelens kunde eller betalingsvilkår er ikke entydige.",
            "An account contract part requires historical period review." => "Kontraktperioderne skal afklares.",
            "Contract planner reported errors for this account." => "Kontraktvalget for kontoen skal afklares.",
            _ => reason
        };
    }
    private static string Action(bool create) => create ? "Opret" : "Behold";
    private static string Date(DateOnly? date) => date?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? "";
    private static string End(DateOnly date) => date == DateOnly.MaxValue || date.Year == 1900 ? "Åben" : Date(date);

    internal static async Task WriteFailureAsync(string output, StateScopeEntry[] entries, string reason, CancellationToken ct)
    {
        await RepairCsv.WriteAsync(Path.Combine(output, "contract-evidence.csv"), ContractHeaders(null), entries.Select(e =>
            new[] { e.Value, "Uafklaret", "", "", "", "", "", "", "", "", "", "Nej", "", "", "", Reason(reason), "", $"Scope, linje {e.Line}", "", "", "" }), ct);
    }

    internal static async Task WriteAsync(string output, IReadOnlyList<CustomerPayerRepairDecision> decisions,
        RepairXellentEvidence source, RepairSettlEvidence settl, ContractSourceSnapshot? snapshot, bool released, CancellationToken ct)
    {
        var contractRows = new List<string[]>();
        var electricity = new List<AborabatPartEvidence>(); var chargers = new List<AborabatPartEvidence>();
        if (snapshot is not null)
        {
            var restored = AborabatRepair.RestoreCustomerPayerSources(snapshot.LiveParts, snapshot.LoggedParts, snapshot.ChargeSubscriptions, source.ReferenceDate);
            electricity = restored.ElectricityEvidence; chargers = restored.ChargerEvidence;
        }
        foreach (var d in decisions)
        {
            var beforeCount = contractRows.Count;
            var account = RepairVerification.Account(source, d.MeteringPoint);
            var parts = electricity.Where(p => p.MeteringPoint == d.MeteringPoint).ToList();
            foreach (var part in parts) AddPart(part, null, part.Account == account);
            var accountAssignments = snapshot?.ChargerAssignments.Where(a => a.Charger.CustomerAccount == account).ToArray() ?? [];
            var explainUnresolvedLinkage = d.Status != "Ready" && source.ContractMappingErrors.ContainsKey(d.MeteringPoint);
            // Only the target MP's charger parts belong in its normal effective-part review.
            // Other assignments are evidence solely when source resolution itself is blocked.
            var assignments = accountAssignments.Where(a => a.MeteringPoint == d.MeteringPoint || explainUnresolvedLinkage).ToArray();
            foreach (var assignment in assignments)
            foreach (var part in chargers.Where(p => p.Account == assignment.Charger.CustomerAccount && p.ContractNumber == assignment.Charger.ContractNumber))
                AddPart(part with { MeteringPoint = d.MeteringPoint }, assignment, assignment.MeteringPoint == d.MeteringPoint);
            foreach (var part in chargers.Where(p => explainUnresolvedLinkage && p.Account == account && !accountAssignments.Any(a => a.Charger.ContractNumber == p.ContractNumber)))
                AddPart(part with { MeteringPoint = d.MeteringPoint }, null, false);
            // Even missing or unmapped raw basis must remain visible for every scoped MP.
            if (beforeCount == contractRows.Count)
                contractRows.Add([d.MeteringPoint, RepairVerification.Outcome(d, released), account, RepairVerification.Contracts(source, d.MeteringPoint),
                    source.IncludeAllMigrationRecords ? "Migrationsgrundlag" : "Kontraktgrundlag mangler", "", "", "", "Kildedata", "", "", "Nej", "", "", Proposals(d), (d.Status == "Ready" ? "" : Reason(d.Status) + " ") + string.Join(" ", d.Warnings.Select(Reason)) + " " + RepairDeliveryHistory.Explanation(d, settl, source.CapturedAt),
                    "", "Kontraktdele mangler i de gemte Xellent-data.", "", "", "Ikke dokumenteret"]);

            void AddPart(AborabatPartEvidence part, XellentChargerAssignmentRow? assignment, bool linked)
            {
                var live = snapshot!.LiveParts.FirstOrDefault(p => p.MeteringPoint == part.MeteringPoint && p.Account == part.Account && p.ContractNumber == part.ContractNumber
                    && p.AgreementNumber == part.AgreementNumber && p.ProductNumber == part.Product && DateOnly.FromDateTime(p.Start) == part.Start);
                var chargeLive = snapshot.ChargeSubscriptions.FirstOrDefault(p => p.Source != "Log" && p.Account == part.Account && p.ContractNumber == part.ContractNumber
                    && p.AgreementNumber == part.AgreementNumber && p.ProductNumber == part.Product && DateOnly.FromDateTime(p.Start) == part.Start);
                var rawEnd = part.Type == "Charger" ? chargeLive?.End : live?.End;
                var log = snapshot.LoggedParts.Select(p => (p.LogRecordId, p.LoggedAt, p.ChangeType))
                    .Concat(snapshot.ChargeSubscriptions.Where(p => p.Source == "Log").Select(p => (p.LogRecordId, p.LoggedAt, p.ChangeType)))
                    .FirstOrDefault(p => p.LogRecordId is { } id && part.EndRecord.Contains($"RECID={id},", StringComparison.Ordinal));
                var effective = linked && part.Account == account && part.Start <= part.End
                    && (source.MigrationModelVersion is null ? part.End >= (d.Start ?? source.ReferenceDate)
                        : (source.ExpectedContracts ?? []).Any(c => c.MeteringPoint == d.MeteringPoint
                            && (part.Type == "Charger" || string.Equals(c.Product, part.Product, StringComparison.OrdinalIgnoreCase))
                            && c.Start <= part.End && c.End >= part.Start));
                var address = assignment?.AddressEvidence;
                var reportBasis = source.MigrationReports.TryGetValue(d.MeteringPoint, out var supporting)
                    ? "Migrationsrapporter: " + string.Join(" | ", supporting) + ". " : "";
                var basis = reportBasis + $"Produkt {part.Product}; start {part.StartValue}. "
                    + (source.ContractMappingErrors.TryGetValue(d.MeteringPoint, out var error) ? Reason(error)
                        : source.MigrationModelVersion is not null ? "Migrationsresultatet er kontrolleret mod Xellent." : "Kontraktvalg følger produkt og lader.")
                   ;
                basis += " " + string.Join(" ", (source.ExpectedContracts ?? []).Where(c => c.MeteringPoint == d.MeteringPoint
                    && c.MappingNote is not null).Select(c => c.MappingNote).Distinct());
                if (part.Type == "Charger") basis = basis.Replace("[MP=,", $"[MP={assignment?.MeteringPoint ?? "ikke tilknyttet"},", StringComparison.Ordinal);
                contractRows.Add([d.MeteringPoint, RepairVerification.Outcome(d, released), part.Account, part.ContractNumber,
                    part.Type == "Charger" ? "Laderabonnement" : part.Type == "Discount" ? "El-rabat" : "Elprodukt", part.Product, Date(part.Start),
                    End(part.End), part.Record.Contains("LOG", StringComparison.OrdinalIgnoreCase) ? "Xellent ændringshistorik" : "Kontraktdel i Xellent", log.ChangeType ?? "", log.LoggedAt?.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    effective ? "Ja" : "Nej", End(part.End), assignment is null ? "Ikke relevant" : assignment.Charger.AddressMatchesMeteringPoint ? "Ja" : "Nej",
                    Proposals(d), (d.Status == "Ready" ? "" : Reason(d.Status) + " ") + string.Join(" ", d.Warnings.Select(Reason)) + " " + RepairDeliveryHistory.Explanation(d, settl, source.CapturedAt), (log.LoggedAt is not null ? $"Slutdato før ændringen {log.LoggedAt:dd-MM-yyyy HH:mm:ss}: {End(part.End)}" : $"Slutdato i Xellent: {End(part.End)}") + (rawEnd is { } raw && End(DateOnly.FromDateTime(raw)) != End(part.End) ? $". Nuværende slutdato {End(DateOnly.FromDateTime(raw))} bruges ikke." : ""),
                    basis + (linked ? "" : part.Type == "Charger" ? "; Laderen er ikke dokumenteret tilknyttet. Tæller ikke." : "; Anden kunde. Tæller ikke."),
                    address?.InstallationAddress ?? assignment?.Charger.Address ?? "", address is null ? "" : string.Join(" | ", address.MeteringPointCandidates.Select(p => $"{p.Street} {p.HouseNumber}{p.HouseLetter}, {p.PostalCode}")),
                    assignment is null ? "Ikke relevant" : assignment.Charger.AddressMatchesMeteringPoint ? "Adresser matcher." : address is null ? "Adressegrundlag mangler." : "Adresser matcher ikke entydigt."]);
            }
            string Proposals(CustomerPayerRepairDecision decision)
            {
                if (decision.Status == "Ready") return string.Join(" | ", decision.Contracts.Select(c => $"{c.ContractName}: {Date(c.Start)} – {End(c.End)} ({Action(c.Create)})"));
                return string.Join(" | ", (source.ExpectedContracts ?? []).Where(c => c.MeteringPoint == decision.MeteringPoint)
                    .Select(c => $"Forslag: {c.ContractName}: {Date(c.Start)} – {End(c.End)}"));
            }
        }
        await RepairCsv.WriteAsync(Path.Combine(output, "contract-evidence.csv"), ContractHeaders(source.ReferenceDate), contractRows, ct);
    }
}
