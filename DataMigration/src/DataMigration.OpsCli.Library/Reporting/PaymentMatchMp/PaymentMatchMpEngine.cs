using Microsoft.Extensions.Configuration;
using DataMigration.Core.Reporting;
using DataMigration.OpsCli.Reporting;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

public sealed class PaymentMatchMpEngine(IPaymentMatchMpReader reader, IConfiguration? configuration = null)
{
    public async Task<PaymentMpResult> RunAsync(PaymentMatchMpInput input,
        List<SettlCsvRecord> records, string environment, string mode, DateOnly cutover,
        bool includeTerminated, string mpFileName, string settlFileName,
        Action<string, int, int> progress, CancellationToken ct)
    {
        if (includeTerminated) throw new ArgumentException("MP-listematch medtager ikke afmeldte aftaler.");
        var exclusions = configuration?.GetSection("PaymentMatch:PbsWaterHeatRuleExcludedMeteringPoints")
            .Get<string[]>() ?? [];
        if (exclusions.Any(id => id is null || id.Length != 18 || id.Any(c => c < '0' || c > '9')))
            throw new InvalidOperationException("PaymentMatch:PbsWaterHeatRuleExcludedMeteringPoints must contain 18-digit metering point IDs.");
        var excludedPoints = exclusions.ToHashSet(StringComparer.Ordinal);
        var appliedExceptions = new HashSet<string>(StringComparer.Ordinal);
        var day = cutover.ToDateTime(TimeOnly.MinValue);
        progress("Finder kunder i Xellent", 0, input.Unique);
        var candidates = await reader.OwnersAsync(input.Ids, day.AddDays(-1),
            (done, total) => progress("Finder kunder i Xellent", done, total), ct);
        var (owners, problems) = ResolveOwners(input.Ids, candidates, day.AddDays(-1));
        var candidateCounts = candidates.GroupBy(c => c.MeteringPoint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => (c.Account, c.ContractNumber)).Distinct().Count(), StringComparer.Ordinal);
        var accounts = owners.Values.Select(o => o.Account).Distinct(StringComparer.Ordinal).ToArray();

        // A conflicting E-number must never be silently selected by the shared matcher's first-row rule.
        var csvConflicts = records.Where(r => !string.IsNullOrEmpty(r.MeteringpointId))
            .GroupBy(r => r.MeteringpointId!, StringComparer.Ordinal)
            .Where(g => g.Select(r => r.ExternalCustomerNumber).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal).Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var jointConflicts = records.Where(r => string.IsNullOrEmpty(r.MeteringpointId)
                && !string.IsNullOrEmpty(r.JointInvoiceNumber))
            .GroupBy(r => r.JointInvoiceNumber!, StringComparer.Ordinal)
            .Where(g => g.Select(r => r.ExternalCustomerNumber).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal).Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var repeatedMpKeys = records.Where(r => !string.IsNullOrEmpty(r.MeteringpointId)).GroupBy(r => r.MeteringpointId!).Where(g => g.Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var repeatedJointKeys = records.Where(r => string.IsNullOrEmpty(r.MeteringpointId) && !string.IsNullOrEmpty(r.JointInvoiceNumber)).GroupBy(r => r.JointInvoiceNumber!).Where(g => g.Skip(1).Any()).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var conflictMpRecords = records.Where(r => repeatedMpKeys.Contains(r.MeteringpointId ?? ""))
            .ToLookup(r => r.MeteringpointId!, StringComparer.Ordinal);
        var conflictJointRecords = records.Where(r => string.IsNullOrEmpty(r.MeteringpointId) && repeatedJointKeys.Contains(r.JointInvoiceNumber ?? ""))
            .ToLookup(r => r.JointInvoiceNumber!, StringComparer.Ordinal);
        var comparisonRecords = input.Ids.ToDictionary(id => id, id => conflictMpRecords[id].ToList(), StringComparer.Ordinal);
        var usableRecords = records.Where(r => !csvConflicts.Contains(r.MeteringpointId ?? "")
            && !jointConflicts.Contains(r.JointInvoiceNumber ?? "")).ToList();
        var recordsByMp = usableRecords.Where(r => !string.IsNullOrEmpty(r.MeteringpointId)).ToLookup(r => r.MeteringpointId!, StringComparer.Ordinal);
        var recordsByJoint = usableRecords.Where(r => string.IsNullOrEmpty(r.MeteringpointId) && !string.IsNullOrEmpty(r.JointInvoiceNumber))
            .ToLookup(r => r.JointInvoiceNumber!, StringComparer.Ordinal);
        var details = new List<PaymentMpAgreement>();
        var associations = new Dictionary<string, List<PaymentMpAgreement>>(StringComparer.Ordinal);
        var selections = new Dictionary<string, PaymentMpSelection>(StringComparer.Ordinal);
        var sourceByContract = new Dictionary<(string Account, string Contract), PaymentMpSource>();
        var customerByAccount = new Dictionary<string, PaymentMpCustomer>(StringComparer.Ordinal);
        var reportCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceKinds = new Dictionary<(string Account, string Contract), string>();
        progress("Læser betalingsaftaler", 0, accounts.Length);
        const int batchSize = 1500;
        for (var offset = 0; offset < accounts.Length; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = accounts.Skip(offset).Take(batchSize).ToHashSet(StringComparer.Ordinal);
            var snapshot = await reader.PaymentsAsync(batch, day.AddDays(-1), ct);
            foreach (var customer in snapshot.Customers) customerByAccount[customer.Account] = customer;
            foreach (var source in snapshot.Contracts) sourceByContract[(source.Account, source.Contract)] = source;
            var contractsByAccount = snapshot.Contracts.ToLookup(c => c.Account, StringComparer.Ordinal);
            foreach (var account in batch)
                reportCounts[account] = mode == "pbs" ? snapshot.PbsOwnAgreementCounts.GetValueOrDefault(account)
                    : contractsByAccount[account].Where(c => c.HasMobile).Select(c => c.MobileId).Distinct(StringComparer.Ordinal).Count();
            var mainContracts = snapshot.Contracts.Where(c => c.MainContract is not null)
                .Select(c => (c.Account, Contract: c.MainContract!)).ToHashSet();
            foreach (var source in snapshot.Contracts)
                sourceKinds[(source.Account, source.Contract)] = source.MainContract is not null ? "SubContract"
                    : mainContracts.Contains((source.Account, source.Contract)) ? "MainContract" : "Standalone";
            var selected = new List<PaymentMpSelection>();
            foreach (var owner in owners.Values.Where(o => batch.Contains(o.Account)))
            {
                ct.ThrowIfCancellationRequested();
                if (!customerByAccount.TryGetValue(owner.Account, out var customer))
                {
                    problems[owner.MeteringPoint] = "Xellent-kunden kunne ikke genfindes; kundetypen er ukendt.";
                    continue;
                }
                var selection = PaymentMpSelection.Select(owner, customer, contractsByAccount[owner.Account].ToArray(), day.AddDays(-1), mode);
                if (mode == "pbs" && excludedPoints.Contains(owner.MeteringPoint) && selection.BusinessErrors.Count > 0)
                {
                    appliedExceptions.Add(owner.MeteringPoint);
                    selection = selection with
                    {
                        Agreements = selection.Agreements.Concat(selection.BusinessErrors)
                            .DistinctBy(source => (source.Account, source.Contract, source.PbsNumber)).ToArray(),
                        BusinessErrors = []
                    };
                }
                selections[owner.MeteringPoint] = selection;
                if (selection.Ambiguous)
                {
                    problems[owner.MeteringPoint] = "Flere forskellige betalingsaftaler på kundens kontrakter. Vælg aftale efter gennemgang.";
                    foreach (var source in selection.Agreements)
                    {
                        var detail = ReviewRow(owner, source, mode, "AmbiguousAgreements", problems[owner.MeteringPoint]);
                        details.Add(detail); Link(associations, owner.MeteringPoint, detail);
                    }
                }
                else selected.Add(selection);
                foreach (var evidence in selection.BusinessErrors)
                    foreach (var paymentType in new[] { mode }.Where(evidence.Eligible))
                    {
                        var detail = ReviewRow(owner, evidence, paymentType, PaymentMpSelection.BusinessErrorCode, selection.ErrorExplanation);
                        details.Add(detail); Link(associations, owner.MeteringPoint, detail);
                    }
            }
            if (mode == "pbs")
            {
                foreach (var selection in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var source in selection.Agreements.Count == 0
                        ? new PaymentMpSource?[] { null } : selection.Agreements.Cast<PaymentMpSource?>())
                    {
                        var query = selection.PbsQuery(source);
                        var scopedRecords = recordsByMp[selection.Owner.MeteringPoint]
                            .Concat(recordsByJoint[selection.Own.Contract]).Concat(recordsByJoint[selection.Own.MainContract ?? ""])
                            .Distinct().ToList();
                        BuildPbs(query, scopedRecords, owners, false, day, details, associations,
                            selection.Agreements.Count == 0 ? selection.NoAgreementExplanation : null);
                        if (query.Contracts.Any(c => jointConflicts.Contains(c.ContractNumber)
                            || jointConflicts.Contains(c.Referencetomaincontract ?? "")))
                            csvConflicts.Add(selection.Owner.MeteringPoint);
                    }
                }
            }
            else
            {
                var chosen = selected.SelectMany(s => s.Agreements).DistinctBy(s => (s.Account, s.Contract, s.MobileId)).ToArray();
                var agreements = chosen.Select(s => new MobilePayContract(s.Account, s.MobileId!, s.MainContract, s.Contract,
                    s.MainContract is not null ? MobilePaySourceKind.SubContract
                        : contractsByAccount[s.Account].Any(c => c.MainContract == s.Contract) ? MobilePaySourceKind.MainContract : MobilePaySourceKind.Standalone,
                    s.End.Year == 1900 ? null : DateOnly.FromDateTime(s.End),
                    contractsByAccount[s.Account].Where(c => c.HasMobile).Select(c => c.MobileId).Distinct().Count())).ToArray();
                var eligibleMps = selected.SelectMany(s => s.Agreements.Select(a => (Source: a, Mp: s.Owner.MeteringPoint)))
                    .GroupBy(x => (x.Source.Account, x.Source.Contract, x.Source.MobileId))
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Mp).ToArray());
                BuildMobilePay(agreements, usableRecords, owners, cutover, details, associations, eligibleMps);
                foreach (var selection in selected.Where(s => s.Agreements.Any(a => jointConflicts.Contains(a.Contract)
                    || jointConflicts.Contains(a.MainContract ?? "")))) csvConflicts.Add(selection.Owner.MeteringPoint);
            }
            progress("Læser betalingsaftaler", Math.Min(offset + batchSize, accounts.Length), accounts.Length);
        }
        // Keep original CSV rows for comparison only; the safe match results remain cleared.
        foreach (var selection in selections.Values)
        {
            var intended = selection.Agreements.FirstOrDefault() ?? (string.IsNullOrWhiteSpace(selection.Own.MainContract) ? selection.Own : selection.Main);
            var keys = mode == "pbs" ? new[] { selection.Own.Contract, selection.Own.MainContract }
                : new[] { intended?.Contract, intended?.MainContract };
            foreach (var key in keys.OfType<string>().Distinct(StringComparer.Ordinal))
                comparisonRecords[selection.Owner.MeteringPoint].AddRange(conflictJointRecords[key]);
            if (comparisonRecords[selection.Owner.MeteringPoint].Select(r => r.ExternalCustomerNumber).Distinct(StringComparer.Ordinal).Skip(1).Any()) csvConflicts.Add(selection.Owner.MeteringPoint);
        }
        // Shared MobilePay detail rows are associated with several supplied MPs. Propagate the
        // competing records through those exact associations, not through all customer contracts.
        if (mode == "mb")
        {
            var conflictsByDetail = new Dictionary<PaymentMpAgreement, List<SettlCsvRecord>>();
            foreach (var (mp, linked) in associations)
                foreach (var detail in linked.Where(d => !d.EvidenceOnly))
                {
                    if (!conflictsByDetail.TryGetValue(detail, out var rows)) conflictsByDetail[detail] = rows = [];
                    rows.AddRange(comparisonRecords[mp]);
                }
            foreach (var (mp, linked) in associations)
                foreach (var detail in linked.Where(d => !d.EvidenceOnly))
                    comparisonRecords[mp].AddRange(conflictsByDetail[detail]);
            foreach (var (mp, rows) in comparisonRecords.Where(p => p.Value.Select(r => r.ExternalCustomerNumber).Distinct(StringComparer.Ordinal).Skip(1).Any())) csvConflicts.Add(mp);
        }
        // Account-level MobilePay fallback must not hide a conflicting MP or joint-invoice key.
        var ambiguousDetails = csvConflicts.Where(associations.ContainsKey)
            .SelectMany(id => associations[id]).Distinct().ToDictionary(r => r, r => r with
            {
                SettlCustomerNumber = null,
                SettlCustomerName = null,
                Status = "Kræver undersøgelse",
                NeedsReview = true,
                Explanation = "Modstridende E-numre i Settl-betalingsaftaleeksporten. " + r.Explanation
            });
        for (var i = 0; i < details.Count; i++)
            if (ambiguousDetails.TryGetValue(details[i], out var replacement)) details[i] = replacement;
        foreach (var linked in associations.Values)
            for (var i = 0; i < linked.Count; i++)
                if (ambiguousDetails.TryGetValue(linked[i], out var replacement)) linked[i] = replacement;
        // Add source/customer labels without using either name as a matching key.
        for (var i = 0; i < details.Count; i++)
        {
            var row = details[i];
            customerByAccount.TryGetValue(row.Account, out var customer);
            sourceByContract.TryGetValue((row.Account, row.SourceContractNumber), out var source);
            details[i] = row with { XellentCustomerName = customer?.Name, CustomerType = customer is null ? null : customer.IsBusiness ? "Business" : "Private",
                SourceCommodity = source?.Commodity, SourceContractEnd = source is null ? row.SourceContractEnd : Date(source.End) };
        }
        progress("Gør rapporten klar", 0, input.Unique);
        var coverage = new List<PaymentMpCoverage>(input.Unique);
        var reportRows = new List<object?[]>();
        foreach (var id in input.Ids)
        {
            ct.ThrowIfCancellationRequested();
            owners.TryGetValue(id, out var owner);
            var linked = associations.GetValueOrDefault(id);
            var reason = problems.GetValueOrDefault(id);
            if (csvConflicts.Contains(id)) reason = "Modstridende E-numre i Settl-betalingsaftaleeksporten.";
            selections.TryGetValue(id, out var selection);
            var outcome = selection?.ErrorCode is not null ? "BusinessError"
                : selection?.Agreements.Count == 0 && !problems.ContainsKey(id) ? "NoAgreement"
                : reason is not null ? "Unresolved"
                : selection?.Agreements.Count == 0 || linked is not { Count: > 0 } ? "NoAgreement"
                : linked.Any(r => r.Status is not ("Match" or "Match (fælles)" or "E-nummer fundet"))
                    ? "Unmatched" : "Matched";
            var settlNames = linked?.Select(r => r.SettlCustomerName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
            coverage.Add(new(id, owner?.Account, owner?.ContractNumber,
                Date(owner?.ContractStart), Date(owner?.ContractEnd), outcome, linked?.Count ?? 0,
                selection?.ErrorCode is not null ? selection.ErrorExplanation
                    : outcome == "NoAgreement" && reason is not null ? selection?.NoAgreementExplanation + " " + reason
                    : reason ?? (outcome switch
                {
                    "NoAgreement" => selection?.NoAgreementExplanation ?? "Ingen relevant betalingsaftale på hovedkontrakten.",
                    "Unmatched" => "En eller flere aftaler mangler et match i Settl-betalingsaftaleeksporten.",
                    _ => "Betalingsaftale matchet mod Settl-eksporten."
                })) { SettlCustomerName = settlNames is { Length: > 0 } ? string.Join("; ", settlNames) : null,
                    XellentCustomerName = selection?.Customer.Name,
                    CustomerType = selection is null ? null : selection.Customer.IsBusiness ? "Business" : "Private",
                    ErrorCode = selection?.ErrorCode,
                    ErrorExplanation = selection?.ErrorCode is not null ? selection.ErrorExplanation : null });
            var firstReportRow = reportRows.Count;
            var competing = comparisonRecords[id].Distinct().ToArray();
            var reportAgreements = linked?.Cast<PaymentMpAgreement?>().DefaultIfEmpty().ToList() ?? [null];
            if (competing.Length > 0)
            {
                // Comparison entries use only the selected source agreement, never a CSV agreement ID.
                var selectedSource = selection?.Agreements.FirstOrDefault();
                PaymentMpAgreement? selectedDetail = selectedSource is null ? null
                    : ReviewRow(owner!, selectedSource, mode, "CsvConflict", "");
                reportAgreements = reportAgreements.Where(a => a?.EvidenceOnly == true).ToList();
                var status = selectedSource is null ? null : mode == "pbs"
                    ? PbsContractSelection.PbsStatusLabel(selectedSource.PbsCode) : "Aktiv";
                foreach (var record in competing)
                {
                    var cells = PaymentMatchMpReport.Cells(coverage[^1], selectedDetail, mode, cutover.ToString("yyyy-MM-dd"),
                        owner is null ? null : reportCounts.GetValueOrDefault(owner.Account), status, candidateCounts.GetValueOrDefault(id),
                        selectedSource is null ? null : sourceKinds.GetValueOrDefault((selectedSource.Account, selectedSource.Contract)),
                        selectedSource is null ? null : Date(selectedSource.End));
                    var joint = string.IsNullOrEmpty(record.MeteringpointId);
                    cells[5] = PaymentMatchMpReport.Status(coverage[^1], selectedDetail);
                    cells[6] = (string.IsNullOrWhiteSpace(record.PaymentAgreementNumber) ? ""
                        : "Betalingsaftale i Settl: " + record.PaymentAgreementNumber.Trim() + ". ")
                        + (selectedSource is null ? selection?.NoAgreementExplanation + " " : "")
                        + (selection?.ErrorCode is not null ? selection.ErrorExplanation : problems.GetValueOrDefault(id));
                    cells[8] = PaymentMatchColumns.MatchKeyLabel(joint ? record.JointInvoiceNumber : record.MeteringpointId, joint);
                    cells[9] = record.ExternalCustomerNumber;
                    cells[10] = record.PaymentMethod;
                    cells[mode == "pbs" ? 17 : 16] = record.CustomerName;
                    reportRows.Add(cells);
                }
            }
            foreach (var agreement in reportAgreements)
            {
                sourceByContract.TryGetValue((owner?.Account ?? "", agreement?.SourceContractNumber ?? ""), out var source);
                var statusSource = string.IsNullOrWhiteSpace(agreement?.AgreementId)
                    ? selection is null ? null : string.IsNullOrWhiteSpace(selection.Own.MainContract) ? selection.Own : selection.Main
                    : source;
                var code = agreement?.PbsTransactionCode ?? statusSource?.PbsCode;
                var agreementStatus = agreement?.PbsTransactionCode is { } evidenceCode ? PbsContractSelection.PbsStatusLabel(evidenceCode)
                    : mode == "pbs" && !(agreement?.EvidenceOnly == true && agreement.PbsTransactionCode is null)
                    ? code is null ? null : PbsContractSelection.PbsStatusLabel(code.Value)
                                        : string.IsNullOrWhiteSpace(statusSource?.MobileId) ? null
                        : PaymentMatchColumns.MobilePayAgreementStatusLabel(statusSource.MobileCancel is { Year: not 1900 } cancelDate
                            ? DateOnly.FromDateTime(cancelDate) : null);
                reportRows.Add(PaymentMatchMpReport.Cells(coverage[^1], agreement, mode, cutover.ToString("yyyy-MM-dd"),
                    owner is null ? null : reportCounts.GetValueOrDefault(owner.Account), agreementStatus,
                    candidateCounts.GetValueOrDefault(id), sourceKinds.GetValueOrDefault((owner?.Account ?? "", agreement?.SourceContractNumber ?? "")),
                    source is null ? null : Date(source.End)));
            }
            var review = selection?.ErrorCode is not null || problems.ContainsKey(id) || selection is null;
            var eligible = selection?.Agreements.Count > 0;

            for (var index = firstReportRow; index < reportRows.Count; index++)
            {
                var cells = reportRows[index];
                if (selection?.ErrorCode == PaymentMpSelection.BusinessErrorCode)
                {
                    var sourceIsWaterHeat = selection.BusinessErrors.Any(source => Equals(cells[1], source.Contract));
                    var remark = "Ingen " + (mode == "pbs" ? "PBS" : "MobilePay") + "-aftale på elkontrakten. "
                        + (sourceIsWaterHeat ? "Denne aftale findes på vand/varme." : "Kunden har aftaler på vand/varme.");
                    cells[6] = (cells[6]?.ToString() ?? "").Replace(selection.ErrorExplanation, remark, StringComparison.Ordinal);
                }
                var nameMatch = eligible && !string.IsNullOrWhiteSpace(cells[9] as string)
                    && PaymentMatchMpReport.NamesMatch(cells[mode == "pbs" ? 17 : 16] as string, cells[mode == "pbs" ? 18 : 17] as string);
                cells[5] = review ? "Kræver undersøgelse" : !eligible ? "Ingen relevant aftale" : nameMatch ? "Match" : "Ikke matchet";
                // Legacy safe-match clearing keeps its diagnostic internally. The primary report
                // evaluates names per row, so that old conflict phrase no longer applies to any row.
                cells[6] = (cells[6]?.ToString() ?? "").Replace("Modstridende E-numre i Settl-betalingsaftaleeksporten.", "", StringComparison.Ordinal).Trim();
                if (!review && eligible)
                    cells[6] += " " + (string.IsNullOrWhiteSpace(cells[9] as string)
                        ? "Ingen række med E-nummer i Settl-betalingsaftaleeksporten."
                        : nameMatch ? "Kundenavnene stemmer overens." : "Kundenavnene stemmer ikke overens eller mangler.");
            }
            if (!review)
            {
                var mpRows = reportRows.GetRange(firstReportRow, reportRows.Count - firstReportRow);
                var usedHighestENumber = mpRows.Count(row => Equals(row[5], "Match")) > 1;
                var error = eligible ? PaymentMatchMpReport.SelectHighestENumber(mpRows) : (Code: (string?)null, Explanation: (string?)null);
                var matched = mpRows.Any(row => Equals(row[5], "Match"));
                coverage[^1] = coverage[^1] with { Outcome = !eligible ? "NoAgreement" : error.Code is not null ? "Unresolved" : matched ? "Matched" : "Unmatched",
                    ErrorCode = error.Code, ErrorExplanation = error.Explanation,
                    Explanation = !eligible ? selection!.NoAgreementExplanation : error.Explanation ?? (matched
                        ? usedHighestENumber ? "Højeste E-nummer med samme kundenavn er valgt." : "Kundenavnene stemmer overens."
                        : mpRows.All(row => string.IsNullOrWhiteSpace(row[9] as string))
                            ? "Ingen række med E-nummer i Settl-betalingsaftaleeksporten."
                            : "Ingen række i Settl-betalingsaftaleeksporten med samme kundenavn.") };
            }
            if (appliedExceptions.Contains(id))
            {
                const string exceptionNote = "PBS-aftale fra vand/varme tilladt for dette målepunkt.";
                coverage[^1] = coverage[^1] with { Explanation = (coverage[^1].Explanation + " " + exceptionNote).Trim() };
                for (var index = firstReportRow; index < reportRows.Count; index++)
                    reportRows[index][6] = ((reportRows[index][6]?.ToString() ?? "") + " " + exceptionNote).Trim();
            }
            // A joint lookup can find a candidate that fails the final name/ranking checks.
            // Describe the lookup without claiming that the rejected row was matched.
            for (var index = firstReportRow; index < reportRows.Count; index++)
                if (!Equals(reportRows[index][5], "Match"))
                    reportRows[index][6] = (reportRows[index][6]?.ToString() ?? "")
                        .Replace("Matchet via fællesfaktura.", "Fundet via fællesfaktura.", StringComparison.Ordinal);
        }
        progress("Færdig", input.Unique, input.Unique);
        return new(environment, mode, cutover.ToString("yyyy-MM-dd"), mpFileName, settlFileName,
            DateTime.UtcNow, input.Supplied, input.Unique, input.Duplicates, coverage, details)
            { IncludeTerminated = includeTerminated, ReportRows = reportRows };
    }

    internal static (Dictionary<string, XellentDeliveryOnDate.Delivery> Owners, Dictionary<string, string> Problems)
        ResolveOwners(IReadOnlyList<string> ids, IEnumerable<XellentDeliveryOnDate.Delivery> candidates, DateTime on)
    {
        var byMp = candidates.Where(c => c.ContractStart.Date <= on.Date
                && (c.ContractEnd.Year == 1900 || c.ContractEnd.Date >= on.Date))
            .ToLookup(c => c.MeteringPoint, StringComparer.Ordinal);
        var owners = new Dictionary<string, XellentDeliveryOnDate.Delivery>(StringComparer.Ordinal);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var rows = byMp[id].ToList();
            if (rows.Count == 0)
                problems[id] = $"Ingen Xellent-kunde fundet pr. {on:yyyy-MM-dd}.";
            else if (rows.Any(c => string.IsNullOrWhiteSpace(c.Account) || string.IsNullOrWhiteSpace(c.ContractNumber)))
                problems[id] = $"Xellent mangler kundenummer eller ejerkontrakt pr. {on:yyyy-MM-dd}.";
            else if (rows.Select(c => c.Account).Distinct(StringComparer.Ordinal).Skip(1).Any())
                problems[id] = $"Flere Xellent-kunder knyttet til målepunktet pr. {on:yyyy-MM-dd}.";
            else
                owners[id] = rows.OrderByDescending(c => c.ContractStart)
                    .ThenBy(c => c.ContractNumber, StringComparer.Ordinal).First();
        }
        return (owners, problems);
    }

    private static void BuildPbs(PbsContractQueryResult query, List<SettlCsvRecord> records,
        Dictionary<string, XellentDeliveryOnDate.Delivery> owners, bool includeTerminated, DateTime day,
        List<PaymentMpAgreement> details, Dictionary<string, List<PaymentMpAgreement>> associations, string? exclusionExplanation = null)
    {
        var contracts = query.Contracts.Where(c => c.MeteringpointId is { } mp && owners.TryGetValue(mp, out var owner)
            && owner.Account == c.CustomerAccount).ToList();
        var excluded = query.ExcludedByMp.Where(p => owners.TryGetValue(p.Key, out var owner)
            && owner.Account == p.Value.CustomerAccount).ToDictionary(p => p.Key, p => p.Value);
        var scoped = new PbsContractQueryResult(contracts, query.ContractStatusMap, excluded, query.ContractCodes);
        var mps = contracts.Select(c => c.MeteringpointId).Concat(excluded.Keys).ToHashSet(StringComparer.Ordinal);
        var jointKeys = contracts.Select(c => c.ContractNumber)
            .Concat(contracts.Select(c => c.Referencetomaincontract ?? "")).ToHashSet(StringComparer.Ordinal);
        var scopedRecords = records.Where(r => !string.IsNullOrEmpty(r.MeteringpointId)
            ? mps.Contains(r.MeteringpointId)
            : jointKeys.Contains(r.JointInvoiceNumber ?? "")).ToList();
        var rows = new PbsMatchService().Match(scoped, scopedRecords, includeTerminated, day);
        var mpByContract = contracts.GroupBy(c => c.ContractNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.MeteringpointId!).Distinct().ToArray(), StringComparer.Ordinal);
        var exportRows = records.GroupBy(r => (r.MeteringpointId, Joint: string.IsNullOrEmpty(r.MeteringpointId) ? r.JointInvoiceNumber : null,
                (string?)r.ExternalCustomerNumber, (string?)r.PaymentAgreementNumber, (string?)r.PaymentMethod))
            .ToDictionary(g => g.Key, g => new Queue<SettlCsvRecord>(g));
        foreach (var row in rows)
        {
            var ids = row.MeteringpointId is { } id ? new[] { id }
                : mpByContract.GetValueOrDefault(row.ContractNumber ?? "", []);
            foreach (var mp in ids.Where(owners.ContainsKey))
            {
                var owner = owners[mp];
                var status = row.MatchStatus switch
                {
                    PbsMatchStatus.Matched => "Match", PbsMatchStatus.MatchedJoint => "Match (fælles)",
                    PbsMatchStatus.UnmatchedXellent => "Kun Xellent", PbsMatchStatus.UnmatchedSettl => "Kun Settl",
                    _ => "Forskel"
                };
                var detail = new PaymentMpAgreement(mp, owner.Account,
                    row.PbsAgreementSourceContract ?? row.ContractNumber ?? "", row.SourceKind?.ToString(),
                    row.PbsAgreementNumber == 0 ? "" : row.PbsAgreementNumber.ToString(),
                    row.ExternalCustomerNumber, row.PaymentAgreementNumber,
                    row.JointInvoiceNumber ?? row.MeteringpointId, status,
                    row.ContractEndDate?.ToString("yyyy-MM-dd"), owner.ContractNumber,
                    (row.PbsAgreementSourceContract ?? row.ContractNumber) == owner.ContractNumber,
                    row.PaymentMethod, exclusionExplanation ?? row.Notes ?? "", row.MatchStatus == PbsMatchStatus.Mismatch);
                var key = (row.JointInvoiceNumber is { Length: > 0 } ? null : row.MeteringpointId,
                    row.JointInvoiceNumber is { Length: > 0 } ? row.JointInvoiceNumber : null,
                    row.ExternalCustomerNumber, row.PaymentAgreementNumber, row.PaymentMethod);
                var matchedRecord = exportRows.TryGetValue(key, out var exportRowsForKey) && exportRowsForKey.Count > 0
                    ? exportRowsForKey.Dequeue() : null;
                detail = detail with { SettlCustomerName = matchedRecord?.CustomerName, IsJointMatch = !string.IsNullOrEmpty(row.JointInvoiceNumber) };
                details.Add(detail);
                Link(associations, mp, detail);
            }
        }
    }

    private static void BuildMobilePay(IReadOnlyList<MobilePayContract> agreements, List<SettlCsvRecord> records,
        Dictionary<string, XellentDeliveryOnDate.Delivery> owners, DateOnly cutover,
        List<PaymentMpAgreement> details, Dictionary<string, List<PaymentMpAgreement>> associations,
        Dictionary<(string Account, string Contract, string? MobileId), string[]> eligibleMps)
    {
        var accounts = agreements.Select(a => a.Account).ToHashSet(StringComparer.Ordinal);
        var scope = owners.Values.Where(o => accounts.Contains(o.Account)).ToList();
        var mpsByAccount = scope.GroupBy(o => o.Account, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(o => o.MeteringPoint).ToList(), StringComparer.Ordinal);
        var cos = scope.ToDictionary(o => o.MeteringPoint, _ => cutover, StringComparer.Ordinal);
        var contracts = scope.ToDictionary(o => o.MeteringPoint,
            o => new MeteringPointAccountResolver.OwnerContract(o.ContractNumber, DateOnly.FromDateTime(o.ContractEnd)),
            StringComparer.Ordinal);
        var rows = MobilePayRowBuilder.Build(agreements, mpsByAccount, cos, contracts, records);
        var exportByKey = records.GroupBy(r => (Key: string.IsNullOrEmpty(r.MeteringpointId) ? r.JointInvoiceNumber : r.MeteringpointId,
                (string?)r.ExternalCustomerNumber, (string?)r.PaymentMethod))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var row in rows.GroupBy(r => (r.Account, r.SourceContractNumber, r.MobilePayAgreementId)).Select(g => g.First()))
        {
            // Global scans require COS-1 contract endings. Here ownership was verified from dated validity;
            // an open owning contract is valid evidence and must not acquire the global-scan warning.
            var remarks = row.Remarks.Where(r => r != MobilePayRowClassifier.PinningOffRemark).ToList();
            var found = !string.IsNullOrWhiteSpace(row.EnummerSettl);
            if (!found && row.Status == MobilePayMatchStatus.Matched)
                remarks.Add("Betalingsaftalen i Settl-eksporten mangler E-nummer.");
            var detail = new PaymentMpAgreement(row.MeteringPointId, row.Account,
                row.SourceContractNumber, row.SourceKind.ToString(), row.MobilePayAgreementId,
                row.EnummerSettl, null, row.MatchKey, found ? "E-nummer fundet" : "INTET E-nummer",
                row.SourceContractEndDate?.ToString("yyyy-MM-dd"), row.MigratedContractNumber,
                row.IsOnMigratedContract, row.EnummerSourcePaymentMethod, string.Join("; ", remarks),
                remarks.Count > 0 || row.Severity == MobilePayRowSeverity.Blocking);
            var matchedRecord = string.IsNullOrEmpty(row.EnummerSettl) ? null
                : exportByKey.GetValueOrDefault((row.MatchKey, row.EnummerSettl, row.EnummerSourcePaymentMethod));
            detail = detail with { SettlCustomerName = matchedRecord?.CustomerName, SettlAgreementNumber = matchedRecord?.PaymentAgreementNumber, IsJointMatch = row.IsJoint };
            details.Add(detail);
            // Only MPs that selected this agreement receive coverage; the matcher's account-level
            // lookup stays unchanged and the shared agreement is not duplicated.
            foreach (var mp in eligibleMps[(row.Account, row.SourceContractNumber, row.MobilePayAgreementId)]) Link(associations, mp, detail);
        }
    }

    private static PaymentMpAgreement ReviewRow(XellentDeliveryOnDate.Delivery owner, PaymentMpSource source,
        string mode, string code, string explanation) => new(owner.MeteringPoint, owner.Account, source.Contract,
            source.MainContract is null ? "Standalone" : "SubContract", source.Agreement(mode), null, null, null,
            code == PaymentMpSelection.BusinessErrorCode ? "Fejl" : "Kræver undersøgelse", Date(source.End),
            owner.ContractNumber, source.Contract == owner.ContractNumber, mode == "pbs" ? "PBS" : "MobilePay", explanation, true)
            { ErrorCode = code, EvidenceOnly = true, PbsTransactionCode = mode == "pbs" ? source.PbsCode : null };

    private static void Link(Dictionary<string, List<PaymentMpAgreement>> associations,
        string mp, PaymentMpAgreement detail)
    {
        if (!associations.TryGetValue(mp, out var list)) associations[mp] = list = [];
        list.Add(detail);
    }

    private static string? Date(DateTime? date) => date is null || date.Value.Year == 1900
        ? null : date.Value.ToString("yyyy-MM-dd");
}
