namespace DataMigration.OpsCli.Services;

public static class SettlementConfigCoverage
{
    private const int MaximumReportedPortfolioCauses = 10;
    public static SettlementConfigMeteringPointSelection? Validate(IReadOnlyList<string>? ids,
        DateOnly? dateFilter, int limit)
    {
        if (dateFilter == DateOnly.MinValue)
            throw new ArgumentException("The supplier-change date must allow reading the preceding day.");
        if (ids is null) return null;
        if (dateFilter is null)
            throw new ArgumentException("An MP list requires a supplier-change date.");
        if (limit != 0)
            throw new ArgumentException("An MP list cannot be combined with a scan limit.");
        if (ids.Count == 0 || ids.Any(id => id is null || id.Length != 18 || id.Any(c => c is < '0' or > '9')))
            throw new ArgumentException("The MP list must contain at least one ID; every ID must have exactly 18 digits.");
        var count = ids.Distinct(StringComparer.Ordinal).Count();
        return new(ids.Count, count, ids.Count - count) { ChangeOfSupplierDate = dateFilter };
    }

    public static IReadOnlyList<SettlementConfigMeteringPointCoverage> Build(IReadOnlyCollection<string> ids,
        IReadOnlyDictionary<string, DateOnly> dates, SettlementConfigDocument document,
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlySet<string>? exemptionReadFailedIds = null,
        IReadOnlyDictionary<string,string>? customerReadProblems = null,
        DateOnly? currentExemptionDate = null,
        IReadOnlyDictionary<string,IReadOnlyList<SettlementConfigHistoricalIssue>>? portfolioCauses = null)
    {
        var entryByMp = entries.ToDictionary(e => e.MeteringPointIdentification, StringComparer.Ordinal);
        var unresolvedByMp = document.Unresolved.GroupBy(u => u.MeteringPointIdentification, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var reviewByCustomer = (document.Review ?? []).ToDictionary(r => r.CustomerNumber, StringComparer.Ordinal);
        var pointReasons = document.Unresolved.Select(u => $"{u.MeteringPointIdentification}: {u.Reason}").ToHashSet(StringComparer.Ordinal);
        var e18Issues = (document.E18Review?.Issues ?? []).GroupBy(i => i.MeteringPointIdentification)
            .ToDictionary(g => g.Key,g => g.Select(i => ExplainMeterPointType(i.Reason)).ToList());
        var exemptions = (document.ExemptionReview?.MeteringPoints ?? []).ToDictionary(r => r.MeteringPointIdentification);
        var historicalIssues = (document.HistoricalSelection?.Issues ?? []).GroupBy(i => i.MeteringPointIdentification)
            .ToDictionary(g => g.Key,g => g.ToList());
        var coveredIds = ids.ToHashSet(StringComparer.Ordinal);
        // Format each customer's blockers once, rather than scanning all points for every row.
        var blockingMessages = (portfolioCauses ?? new Dictionary<string,IReadOnlyList<SettlementConfigHistoricalIssue>>())
            .ToDictionary(kv => kv.Key,kv =>
            {
                var causes = kv.Value
                    .Where(i => coveredIds.Contains(i.MeteringPointIdentification) && i.Reason != SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete)
                    .DistinctBy(i => i.MeteringPointIdentification)
                    .OrderBy(i => i.MeteringPointIdentification,StringComparer.Ordinal).ToArray();
                var messages = causes.Take(MaximumReportedPortfolioCauses)
                    .Select(i => $"Customer proposals are blocked by metering point {i.MeteringPointIdentification}: {Explain(i.Reason)}").ToList();
                if (causes.Length > MaximumReportedPortfolioCauses)
                    messages.Add($"Plus {causes.Length - MaximumReportedPortfolioCauses} more blocking metering points; see the JSON unresolved rows.");
                return messages.ToArray();
            },StringComparer.Ordinal);
        return ids.Distinct(StringComparer.Ordinal).Select(id =>
        {
            var entry = entryByMp.GetValueOrDefault(id);
            var unresolved = unresolvedByMp.GetValueOrDefault(id) ?? [];
            var customer = entry?.CustomerNumber ?? unresolved.Select(u => u.CustomerNumber).FirstOrDefault(c => c is not null);
            var review = customer is null ? null : reviewByCustomer.GetValueOrDefault(customer);
            IEnumerable<string> ExplainProblem(SettlementConfigUnresolvedReason reason) =>
                reason == SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete && customer is not null
                    && blockingMessages.TryGetValue(customer,out var blockers) && blockers.Length > 0
                    ? blockers : [Explain(reason)];
            var problems = unresolved.SelectMany(u => ExplainProblem(u.Reason)).ToList();
            if (review is not null)
                problems.AddRange(review.UnresolvedReasons.Where(reason => !pointReasons.Contains(reason)));
            if (exemptionReadFailedIds?.Contains(id) == true) problems.Add("Could not read Settl billing exemptions");
            problems.AddRange(e18Issues.GetValueOrDefault(id) ?? []);
            var exemption = exemptions.GetValueOrDefault(id);
            if (!string.IsNullOrWhiteSpace(exemption?.Warning)) problems.Add(exemption.Warning);
            if (currentExemptionDate is { } readDate && dates.TryGetValue(id,out var deliveryDate) && deliveryDate != readDate)
                problems.Add(SettlementExemptionProposal.HistoricalWarning);
            problems.AddRange((historicalIssues.GetValueOrDefault(id) ?? []).SelectMany(i => ExplainProblem(i.Reason)));
            if (customerReadProblems?.TryGetValue(id,out var readProblem) == true) problems.Add(readProblem);
            if (customer is null && !problems.Contains("Missing customer")) problems.Add("Missing customer");
            return new SettlementConfigMeteringPointCoverage(id, dates.TryGetValue(id, out var date) ? date : null,
                customer, review?.ProposedSegment, review?.ProposedSchedule, problems.Distinct(StringComparer.Ordinal).ToList());
        }).ToList();
    }

    private static string ExplainMeterPointType(string reason) => reason switch
    {
        "MeterPointTypeMissing" or "MeterPointTypeMissingAtReferenceDate" => "Missing Xellent metering-point type for the day before delivery starts",
        "MeterPointTypeAmbiguous" => "More than one Xellent metering-point type applies on the day before delivery starts",
        "MeterPointTypeReadFailed" => "Could not read the Xellent metering-point type",
        "MeterPointTypeNotRead" => "Xellent metering-point type was not read",
        _ => "Could not determine the Xellent metering-point type"
    };

    public static string Explain(SettlementConfigUnresolvedReason reason) => reason switch
    {
        SettlementConfigUnresolvedReason.NotFoundInSettl => "Listed metering point not found in Settl",
        SettlementConfigUnresolvedReason.NoDeliveryPeriod => "No usable current or next delivery period in Settl",
        SettlementConfigUnresolvedReason.NotInDelivery => "Listed metering point is not in delivery",
        SettlementConfigUnresolvedReason.ChildMeteringPoint => "Listed metering point is a child point and is excluded by the existing rule",
        SettlementConfigUnresolvedReason.NotFoundInXellent => "Metering point not found in Xellent",
        SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier => "No Xellent contract found for the day before delivery starts",
        SettlementConfigUnresolvedReason.NoBillingPlan => "Missing Xellent billing plan",
        SettlementConfigUnresolvedReason.NoRepresentative => "Missing customer",
        SettlementConfigUnresolvedReason.AmbiguousHistoricalContract => "Conflicting or ambiguous historical contract-part evidence",
        SettlementConfigUnresolvedReason.AmbiguousSettlRecord => "Several conflicting Settl records have the listed metering point ID",
        SettlementConfigUnresolvedReason.FirstDeliveryIsMoveIn => "The first Settl delivery started with a move-in, so it does not establish the original supplier-change date",
        SettlementConfigUnresolvedReason.FirstDeliveryStartReasonUnknown => "The reason for the first Settl delivery start is unknown",
        SettlementConfigUnresolvedReason.FirstDeliveryNotChangeOfSupplier => "The first Settl delivery start is not a supplier change",
        SettlementConfigUnresolvedReason.DeliveryHistoryReadFailed => "Could not read Settl delivery history",
        SettlementConfigUnresolvedReason.DeliveryHistoryMissing => "Missing Settl delivery history",
        SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous => "Ambiguous Settl delivery history",
        SettlementConfigUnresolvedReason.HistoricalCustomerReadFailed => "Could not read the historical Settl customer",
        SettlementConfigUnresolvedReason.HistoricalCustomerMissing => "Missing historical Settl customer",
        SettlementConfigUnresolvedReason.HistoricalCustomerAmbiguous => "Ambiguous historical Settl customer",
        SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete => "Historical customer information is incomplete",
        SettlementConfigUnresolvedReason.XellentCustomerMismatch => "Xellent customer differs from the confirmed historical Settl customer",
        _ => reason.ToString()
    };
}
