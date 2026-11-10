using DataMigration.OpsCli.Runners;
using DataMigration.Migration.Services;

namespace DataMigration.OpsCli.Services;

/// <summary>List-only agreement of independently mapped points. Uses loaded sources; performs no reads.</summary>
public static class SettlementConfigCustomerResults
{
    public static bool HasNamedException(string? name,IReadOnlyList<SettlementCustomerException>? rules = null) =>
        SettlementCustomerExceptionRules.Match(rules,name) is not null;

    public static SettlementConfigDocument Apply(SettlementConfigDocument document,
        IReadOnlyCollection<SettlementConfigEntry> entries, IReadOnlyDictionary<string,DateOnly> dates,
        ExportSettlementConfigRunOptions options, IReadOnlyDictionary<string,CustomerIdentityKind> identities,
        IReadOnlySet<string> incompleteCustomers, IReadOnlySet<string> chargeFailures,
        IReadOnlyList<SettlementConfigExemptionRead>? exemptionReads,
        IReadOnlyDictionary<string,SettlementCurrentCustomerEvidence>? currentCustomers)
    {
        // Index once. Each pure helper below sees one point, never the complete population.
        var baseline = document.MeteringPointCoverage ?? [];
        var coveredIds = baseline.Select(p => p.MeteringPointIdentification).ToHashSet(StringComparer.Ordinal);
        entries = entries.Where(e => coveredIds.Contains(e.MeteringPointIdentification)).ToArray();
        var entriesById = entries.ToDictionary(e => e.MeteringPointIdentification,StringComparer.Ordinal);
        var unresolvedById = document.Unresolved.GroupBy(u => u.MeteringPointIdentification)
            .ToDictionary(g => g.Key,g => (IReadOnlyList<SettlementConfigUnresolved>)g.ToArray());
        var exemptionsById = exemptionReads?.ToDictionary(r => r.MeteringPointIdentification);
        var points = new Dictionary<string,List<SettlementConfigMeteringPointResult>>(StringComparer.Ordinal);
        var unknown = new List<SettlementConfigCustomerResult>();
        var exceptions = new List<SettlementConfigNamedCustomerExceptionEvidence>();
        var names = entries.GroupBy(e => e.CustomerNumber,StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(e => e.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),StringComparer.Ordinal);
        var inconsistentNames = entries.GroupBy(e => e.CustomerNumber,StringComparer.Ordinal)
            .Where(g => HasConflictingNames(g.Select(e => e.CustomerName),options.Config?.CustomerExceptions))
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var matchedRules = names.ToDictionary(kv => kv.Key,kv => inconsistentNames.Contains(kv.Key) ? null
            : SettlementCustomerExceptionRules.Match(options.Config?.CustomerExceptions,kv.Value),StringComparer.Ordinal);
        var ownSegmentConflicts = new Dictionary<string,List<CustomerSegmentCandidate>>(StringComparer.Ordinal);
        foreach (var covered in baseline)
        {
            var id = covered.MeteringPointIdentification;
            if (!entriesById.TryGetValue(id,out var entry))
            {
                unknown.Add(new(null,null,null,null,covered.Problems,
                    [new(id,covered.ChangeOfSupplierDate,null,null,covered.Problems)]));
                continue;
            }
            SettlementConfigEntry[] ownEntries = [entry];
            var ownDates = new Dictionary<string,DateOnly> { [id] = dates[id] };
            var ownUnresolved = unresolvedById.GetValueOrDefault(id) ?? [];
            var own = SettlementScheduleGrouper.Build(ownEntries,ownUnresolved,options.ScheduleMap,1,
                document.Environment,document.DataAreaId,document.GeneratedAt);
            var chargeAnswer = entry.ChargeReadStatus == "Read"
                ? XellentChargeSubscriptionReader.SegmentFor(entry.ChargeProducts ?? []) : null;
            var charging = chargeAnswer is not null && (chargeAnswer != "Privat"
                || SettlementConfigKeys.NormalizeTemplate(entry.ContractTemplate ?? "") == "V_LadePriv")
                ? new Dictionary<string,string> { [entry.CustomerNumber] = chargeAnswer } : null;
            var ownChargeFailure = entry.ChargeReadStatus is "ReadFailed" or "Ambiguous";
            var segments = CustomerSegmentGrouper.Build(ownEntries,options.SegmentMap,identities,
                segmentByCustomerFromXellentCharge: charging,
                chargeReadFailedCustomers: ownChargeFailure ? new HashSet<string> { entry.CustomerNumber } : null);
            var e18 = SettlementE18Proposal.Apply(segments,ownEntries,identities);
            var exemption = SettlementExemptionProposal.Apply(e18.Segments,ownEntries,identities,
                exemptionReads is null ? null : exemptionsById!.TryGetValue(id,out var read) ? [read] : [],
                options.ChangeOfSupplierDate,document.ExemptionReview?.ReadAt ?? document.GeneratedAt,
                currentCustomers,document.Environment,ownDates);
            own = own with { Segments = exemption.Segments.Segments, SegmentConflicts = exemption.Segments.Conflicts,
                SegmentUnresolved = exemption.Segments.Unresolved, UnmappedContractTemplates = exemption.Segments.UnmappedContractTemplates,
                E18Review = e18.Review, ExemptionReview = exemption.Review };
            own = own with { Review = SettlementConfigReview.Build(own,ownEntries,null,null,
                new Dictionary<string,SettlementConfigCurrentSegment>(),false,assignmentsSkipped:true) };
            var row = SettlementConfigCoverage.Build([id],ownDates,own,ownEntries,
                exemption.Review?.MeteringPoints.Any(r => r.ReadStatus != "Read") == true ? new HashSet<string> { id } : null).Single();
            var segment = row.ProposedSegment;
            var schedule = row.ProposedSchedule;
            var sourceBlocked = !entry.CustomerInXellent || ownUnresolved.Any(u => u.Reason is not
                (SettlementConfigUnresolvedReason.NoBillingPlan or SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete));
            var namedException = matchedRules[entry.CustomerNumber];
            var segmentApplied = false;
            var scheduleApplied = false;
            if (namedException is not null)
            {
                // Mapping differences may be overridden; missing/failed source evidence may not.
                var typeBlocked = (identities.GetValueOrDefault(entry.CustomerNumber) == CustomerIdentityKind.Cvr
                    || e18.Review.Customers.Any(c => c.OriginalProposedSegment == "SME"))
                    && entry.MeterPointTypeReadStatus != "Read";
                segmentApplied = !sourceBlocked && !ownChargeFailure && !typeBlocked;
                scheduleApplied = !sourceBlocked;
                if (segmentApplied) segment = namedException.Segment;
                if (scheduleApplied) schedule = namedException.Schedule;
                exceptions.Add(new(entry.CustomerNumber,string.IsNullOrWhiteSpace(entry.CustomerName)
                    ? names[entry.CustomerNumber]! : entry.CustomerName,"ConfiguredCustomerException",id,
                    row.ProposedSegment,row.ProposedSchedule,segment,schedule,segmentApplied,scheduleApplied,
                    namedException.Name,namedException.Name.EndsWith('*') ? "Wildcard" : "Exact"));
            }
            if (sourceBlocked) { segment = null; schedule = null; }
            if (ownChargeFailure) segment = null;
            var problems = row.Problems.Where(p => !Superseded(p,segmentApplied,scheduleApplied))
                .Concat(covered.Problems.Where(p => p == SettlementExemptionProposal.HistoricalWarning
                    || p.StartsWith("Customer proposals are blocked by metering point ",StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal).ToArray();
            var point = new SettlementConfigMeteringPointResult(id,row.ChangeOfSupplierDate,segment,schedule,problems);
            if (!points.TryGetValue(entry.CustomerNumber,out var members)) points[entry.CustomerNumber] = members = [];
            members.Add(point);
            if (!segmentApplied && own.SegmentConflicts is { Count: > 0 })
            {
                if (!ownSegmentConflicts.TryGetValue(entry.CustomerNumber,out var candidates))
                    ownSegmentConflicts[entry.CustomerNumber] = candidates = [];
                candidates.AddRange(own.SegmentConflicts.SelectMany(c => c.Candidates));
            }
        }

        var results = new List<SettlementConfigCustomerResult>();
        var scheduleConflicts = new List<SettlementScheduleConflict>();
        var segmentConflicts = new List<CustomerSegmentConflict>();
        foreach (var customer in inconsistentNames) names[customer] = null;
        foreach (var (customer,members) in points.OrderBy(kv => kv.Key,StringComparer.Ordinal))
        {
            var segmentValues = Values(members,p => p.ProposedSegment);
            var scheduleValues = Values(members,p => p.ProposedSchedule);
            var segment = Common(members,p => p.ProposedSegment,segmentValues);
            var schedule = Common(members,p => p.ProposedSchedule,scheduleValues);
            var sourceProblems = members.SelectMany(p => p.Problems.Where(problem =>
                problem.StartsWith("Customer proposals are blocked by metering point ",StringComparison.Ordinal)
                || problem.Contains("ambiguous historical charging contract parts",StringComparison.Ordinal)
                || problem.StartsWith("Xellent Charge_Abo read failed",StringComparison.Ordinal))
                .Select(problem => $"{p.MeteringPointIdentification}: {problem}"));
            var problems = sourceProblems.Concat(members.SelectMany(p => p.Problems.Select(problem => $"{p.MeteringPointIdentification}: {problem}")))
                .Distinct(StringComparer.Ordinal).Take(20).ToList();
            var problemCount = members.Sum(p => p.Problems.Count);
            if (problemCount > 20) problems.Add($"Plus {problemCount - 20} more point problems; see nested metering points.");
            if (scheduleValues.Length > 1)
            {
                problems.Add(ConflictText("schedule",members,p => p.ProposedSchedule));
                scheduleConflicts.Add(new(customer,names[customer],scheduleValues.Select(value => {
                    var ids = members.Where(p => string.Equals(p.ProposedSchedule,value,StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.MeteringPointIdentification).ToArray();
                    return new SettlementScheduleCandidate(value,true,ids.Select(id => entriesById[id].BillingPlanNum)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),ids.Length,ids);
                }).ToArray()));
            }
            if (segmentValues.Length > 1 || ownSegmentConflicts.ContainsKey(customer))
            {
                if (segmentValues.Length > 1) problems.Add(ConflictText("segment",members,p => p.ProposedSegment));
                var candidates = segmentValues.Select(value => {
                    var ids = members.Where(p => string.Equals(p.ProposedSegment,value,StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.MeteringPointIdentification).ToArray();
                    return new CustomerSegmentCandidate(string.Join(" + ",ids.Select(id => entriesById[id].ContractTemplate)
                        .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)),[value],true,ids.Length,ids);
                }).Concat(ownSegmentConflicts.GetValueOrDefault(customer) ?? []).ToArray();
                segmentConflicts.Add(new(customer,names[customer],ownSegmentConflicts.ContainsKey(customer)
                    ? segmentValues.Length > 1 ? CustomerSegmentConflictReason.Both : CustomerSegmentConflictReason.AmbiguousTemplate
                    : CustomerSegmentConflictReason.MultipleSegments,candidates,identities.GetValueOrDefault(customer)));
            }
            if (incompleteCustomers.Contains(customer)) { segment = null; schedule = null; }
            if (chargeFailures.Contains(customer)) segment = null;
            if (inconsistentNames.Contains(customer)) problems.Add("Conflicting Xellent customer names; the named customer exception was not applied.");
            results.Add(new(customer,names[customer],segment,schedule,problems,members));
        }
        results.AddRange(unknown);
        var schedules = results.Where(r => r.CustomerNumber is not null && r.ProposedSchedule is not null)
            .GroupBy(r => r.ProposedSchedule!,StringComparer.OrdinalIgnoreCase).Select(g => new SettlementScheduleGroup(g.Key,true,
                g.SelectMany(r => r.MeteringPoints).Select(p => entriesById[p.MeteringPointIdentification].BillingPlanNum).Distinct().ToArray(),
                g.Count(),g.Sum(r => r.MeteringPoints.Count),g.Select(Customer).ToArray())).OrderBy(g => g.Schedule,StringComparer.Ordinal).ToArray();
        var segmentsFinal = results.Where(r => r.CustomerNumber is not null && r.ProposedSegment is not null)
            .GroupBy(r => r.ProposedSegment!,StringComparer.OrdinalIgnoreCase).Select(g => new CustomerSegmentGroup(g.Key,true,
                g.SelectMany(r => r.MeteringPoints).Select(p => entriesById[p.MeteringPointIdentification].ContractTemplate).OfType<string>().Distinct().ToArray(),
                g.Count(),g.Sum(r => r.MeteringPoints.Count),g.Select(Customer).ToArray())).OrderBy(g => g.Segment,StringComparer.Ordinal).ToArray();
        var resultsByCustomer = results.Where(r => r.CustomerNumber is not null).ToDictionary(r => r.CustomerNumber!);
        var segmentConflictCustomers = segmentConflicts.Select(c => c.CustomerNumber).ToHashSet(StringComparer.Ordinal);
        var scheduleConflictCustomers = scheduleConflicts.Select(c => c.CustomerNumber).ToHashSet(StringComparer.Ordinal);
        var unresolvedSegments = results.Where(r => r.CustomerNumber is not null && r.ProposedSegment is null && !segmentConflictCustomers.Contains(r.CustomerNumber))
            .Select(r => new CustomerSegmentUnresolved(r.CustomerNumber!,r.MeteringPoints.Where(p => p.ProposedSegment is null)
                .Select(p => p.MeteringPointIdentification).DefaultIfEmpty(r.MeteringPoints[0].MeteringPointIdentification).ToArray())).ToArray();
        return document with { CustomerResults = results, MeteringPointCoverage = null, NamedCustomerExceptionEvidence = exceptions,
            Schedules = schedules, Segments = segmentsFinal, ScheduleConflicts = scheduleConflicts, SegmentConflicts = segmentConflicts,
            SegmentUnresolved = unresolvedSegments,
            CustomersWithoutSchedule = results.Where(r => r.CustomerNumber is not null && r.ProposedSchedule is null && !scheduleConflictCustomers.Contains(r.CustomerNumber))
                .Select(r => new CustomerWithoutSchedule(r.CustomerNumber!,r.CustomerName,r.MeteringPoints.Count,
                    r.MeteringPoints.Select(p => p.MeteringPointIdentification).ToArray(),"No complete mapped schedule proposal")).ToArray(),
            Unresolved = document.Unresolved.Select(u => u with { Segment = u.CustomerNumber is not null
                ? resultsByCustomer.GetValueOrDefault(u.CustomerNumber)?.ProposedSegment : null,
                CustomerHasOwnSegment = u.CustomerNumber is not null && resultsByCustomer.GetValueOrDefault(u.CustomerNumber)?.ProposedSegment is not null }).ToArray(),
            Review = document.Review?.Select(r => resultsByCustomer.TryGetValue(r.CustomerNumber,out var result)
                ? r with { ProposedSegment = result.ProposedSegment, ProposedSchedule = result.ProposedSchedule, UnresolvedReasons = result.Problems } : r).ToArray(),
            E18Review = document.E18Review is null ? null : document.E18Review with {
                Customers = document.E18Review.Customers.Select(c => c with {
                    FinalProposedSegment = resultsByCustomer.GetValueOrDefault(c.CustomerNumber)?.ProposedSegment,
                    Changed = c.Changed && c.FinalProposedSegment == resultsByCustomer.GetValueOrDefault(c.CustomerNumber)?.ProposedSegment }).ToArray() },
            ExemptionReview = document.ExemptionReview is null ? null : document.ExemptionReview with {
                Customers = document.ExemptionReview.Customers.Select(c => c with {
                    FinalProposedSegment = resultsByCustomer.GetValueOrDefault(c.CustomerNumber)?.ProposedSegment,
                    Changed = c.Changed && c.FinalProposedSegment == resultsByCustomer.GetValueOrDefault(c.CustomerNumber)?.ProposedSegment }).ToArray() },
            Totals = document.Totals with { Schedules = schedules.Length, Segments = segmentsFinal.Length,
                ScheduleConflicts = scheduleConflicts.Count, SegmentConflicts = segmentConflicts.Count,
                SegmentsAssigned = segmentsFinal.Sum(g => g.CustomerCount), SegmentUnresolved = unresolvedSegments.Sum(g => g.MeteringPointIdentifications.Count),
                CustomersWithoutSchedule = results.Count(r => r.CustomerNumber is not null && r.ProposedSchedule is null) - scheduleConflicts.Count,
                SchedulesFromConflictTable = 0, SegmentsFromMultiple = 0, SegmentsFromSettlRule = 0, UnresolvedWithSegment = document.Unresolved.Count(u =>
                    u.CustomerNumber is not null && resultsByCustomer.GetValueOrDefault(u.CustomerNumber)?.ProposedSegment is not null) } };
    }

    private static SettlementConfigCustomer Customer(SettlementConfigCustomerResult r) => new(r.CustomerNumber!,r.CustomerName,r.MeteringPoints.Count);
    private static string NormalizeName(string name) => string.Join(" ",name.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    private static bool HasConflictingNames(IEnumerable<string?> names,IReadOnlyList<SettlementCustomerException>? rules)
    {
        var known = names.OfType<string>().Where(n => !string.IsNullOrWhiteSpace(n)).Select(NormalizeName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (known.Length <= 1) return false;
        var prefixes = known.Select(name => SettlementCustomerExceptionRules.Match(rules,name)?.Name).ToArray();
        return prefixes.Any(p => p is null) || prefixes.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
    }
    private static bool Superseded(string problem,bool segment,bool schedule) =>
        segment && (problem.StartsWith("No mapped Xellent segment proposal",StringComparison.Ordinal)
            || problem.StartsWith("Unknown contract template mapping",StringComparison.Ordinal)
            || problem.StartsWith("Conflicting or ambiguous segment proposals",StringComparison.Ordinal)
            || problem == "One or more Xellent metering points have no usable segment basis")
        || schedule && (problem.StartsWith("No mapped Xellent schedule proposal",StringComparison.Ordinal)
            || problem.StartsWith("Unknown billing plan mapping",StringComparison.Ordinal)
            || problem.StartsWith("Conflicting schedule proposals",StringComparison.Ordinal));
    private static string[] Values(IEnumerable<SettlementConfigMeteringPointResult> members,Func<SettlementConfigMeteringPointResult,string?> get) =>
        members.Select(get).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    private static string? Common(IEnumerable<SettlementConfigMeteringPointResult> members,Func<SettlementConfigMeteringPointResult,string?> get,string[] values) =>
        values.Length == 1 && members.All(p => get(p) is not null) ? values[0] : null;
    private static string ConflictText(string field,IReadOnlyList<SettlementConfigMeteringPointResult> members,Func<SettlementConfigMeteringPointResult,string?> get) =>
        $"Conflicting {field} proposals: " + string.Join("; ",members.Where(p => get(p) is not null)
            .GroupBy(p => get(p)!,StringComparer.OrdinalIgnoreCase).Take(10).Select(g =>
                $"{g.Key} on metering points {string.Join(", ",g.Take(10).Select(p => p.MeteringPointIdentification))}"
                + (g.Count() > 10 ? $" (+{g.Count() - 10} more; see nested metering points)" : "")));
}
