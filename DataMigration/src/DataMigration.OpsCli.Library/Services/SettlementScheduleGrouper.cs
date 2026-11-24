namespace DataMigration.OpsCli.Services;

/// <summary>Proposes schedules from Xellent billing plans across the whole customer.
/// Exact configured conflict rules may select a winner. Settl group arguments are retained
/// for compatibility and never determine a proposal.</summary>
public static class SettlementScheduleGrouper
{
    /// <param name="scheduleByCustomerFromGroup">Legacy comparison input; ignored for proposals.</param>
    /// <param name="noteByCustomerFromGroup">Legacy comparison input; ignored for proposals.</param>
    /// <param name="conflictRules">Approved rules matching the exact mapped candidate set.</param>
    public static SettlementConfigDocument Build(
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyCollection<SettlementConfigUnresolved> unresolved,
        IReadOnlyDictionary<string, string>? scheduleMap,
        int meteringPointsInSettl,
        string environment,
        string dataAreaId,
        DateTime generatedAt,
        IReadOnlyDictionary<string, string>? scheduleByCustomerFromGroup = null,
        IReadOnlyDictionary<string, string>? noteByCustomerFromGroup = null,
        IReadOnlyList<ScheduleConflictRule>? conflictRules = null)
    {
        // Case-insensitive throughout: Xellent spells the same plan code several ways ("Måned." /
        // "måned."), and they are one period type. That has to hold for unmapped codes too — folding
        // only inside the map would leave casing variants of an unknown code as separate schedules.
        var unmappedPlans = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var scheduled = entries
            .Select(e =>
            {
                // Null schedule, not an empty one: this metering point's Xellent contract belonged to
                // the previous holder, so it says nothing about the customer on the row. An empty
                // string would group as a schedule of its own and be reported as an unmapped plan.
                if (!e.CustomerInXellent || string.IsNullOrWhiteSpace(e.BillingPlanNum)) return (Entry: e, Schedule: (string?)null, Mapped: true);

                var mapped = scheduleMap != null
                    && scheduleMap.TryGetValue(e.BillingPlanNum, out var name)
                    && !string.IsNullOrWhiteSpace(name);
                if (!mapped) unmappedPlans.Add(e.BillingPlanNum);
                return (Entry: e, Schedule: (string?)(mapped ? scheduleMap![e.BillingPlanNum] : e.BillingPlanNum), Mapped: mapped);
            })
            .ToList();

        var byCustomer = scheduled
            .GroupBy(x => x.Entry.CustomerNumber, StringComparer.Ordinal)
            .ToList();

        // Customers no Xellent plan speaks for. Kept apart from the conflicts below: a conflict is
        // two sources disagreeing, this is no source at all, and only the group can answer it.
        var withoutPlan = byCustomer
            .Where(g => g.All(x => x.Schedule is null))
            .Select(g => g.Key)
            .ToList();

        // Customers whose metering points land on more than one schedule. Pulled out before the
        // grouping rather than after: a conflicted customer must not be counted in any schedule, or
        // the group counts say something the data does not support.
        var disagreeing = byCustomer
            .Where(g => g.Where(x => x.Schedule is not null).Select(x => x.Schedule).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => new SettlementScheduleConflict(
                g.Key,
                g.Select(x => x.Entry.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                [.. g.Where(x => x.Schedule is not null)
                    .GroupBy(x => x.Schedule!, StringComparer.OrdinalIgnoreCase)
                    // Most metering points first: the candidate a human is most likely to pick, and
                    // the one a majority rule would land on if we ever adopt one.
                    .OrderByDescending(sg => sg.Select(x => x.Entry.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Count())
                    .ThenBy(sg => sg.Key, StringComparer.Ordinal)
                    .Select(sg => new SettlementScheduleCandidate(
                        sg.Key,
                        sg.All(x => x.Mapped),
                        [.. sg.Select(x => x.Entry.BillingPlanNum).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                        sg.Select(x => x.Entry.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Count(),
                        [.. sg.Select(x => x.Entry.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))]))
            .OrderBy(c => c.CustomerNumber, StringComparer.Ordinal)
            .ToList();

        // Only configured Xellent conflict rules may select a winner.
        var placedByConflictRule = new Dictionary<string, string>(StringComparer.Ordinal);
        var placedByConflictTable = new HashSet<string>(StringComparer.Ordinal);
        if (conflictRules is { Count: > 0 })
        {
            foreach (var conflict in disagreeing.Where(c => !placedByConflictRule.ContainsKey(c.CustomerNumber)))
            {
                var candidateSchedules = conflict.Candidates.Select(c => c.Schedule).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rule = conflictRules.FirstOrDefault(r =>
                    r.Schedules.Count == candidateSchedules.Count
                    && r.Schedules.All(s => candidateSchedules.Contains(s)));
                if (rule is not null && conflict.Candidates.All(c => c.Mapped))
                {
                    placedByConflictRule[conflict.CustomerNumber] = rule.Winner;
                    placedByConflictTable.Add(conflict.CustomerNumber);
                }
            }
        }

        var conflicts = disagreeing
            .Where(c => !placedByConflictRule.ContainsKey(c.CustomerNumber))
            .ToList();
        var conflicted = conflicts.Select(c => c.CustomerNumber).ToHashSet(StringComparer.Ordinal);

        var groups = scheduled
            .Where(x => !conflicted.Contains(x.Entry.CustomerNumber))
            // Use the approved winner for the whole customer.
            .Select(x => placedByConflictRule.TryGetValue(x.Entry.CustomerNumber, out var fromGroup)
                ? x with { Schedule = fromGroup }
                : x)
            // What is left with no schedule is a customer with neither a Xellent plan nor a group.
            // She is counted in the totals below rather than filed under a schedule she never named.
            .Where(x => x.Schedule is not null)
            .GroupBy(x => x.Schedule!, StringComparer.OrdinalIgnoreCase)
            .Select(scheduleGroup =>
            {
                var customers = scheduleGroup
                    .GroupBy(x => x.Entry.CustomerNumber, StringComparer.Ordinal)
                    .Select(customerGroup => new SettlementConfigCustomer(
                        customerGroup.Key,
                        customerGroup.Select(x => x.Entry.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                        customerGroup.Select(x => x.Entry.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Count()))
                    .OrderBy(c => c.CustomerNumber, StringComparer.Ordinal)
                    .ToList();

                // Retain all Xellent plans as the audit basis, including rule-resolved conflicts.
                var fromPlan = scheduleGroup.ToList();

                return new SettlementScheduleGroup(
                    scheduleGroup.Key,
                    // A schedule is only "mapped" when every plan code feeding it came from the map.
                    fromPlan.All(x => x.Mapped),
                    fromPlan.Select(x => x.Entry.BillingPlanNum).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                    customers.Count,
                    customers.Sum(c => c.MeteringPointCount),
                    customers);
            })
            .OrderBy(g => g.Schedule, StringComparer.Ordinal)
            .ToList();

        // Kunder som intet navngav et skema for. Tallet alene kunne ikke bruges til noget: for at
        // finde ud af hvem det var, måtte man trække skema-gruppernes kundenumre fra segmenternes.
        // Grunden kommer ordret fra gruppe-resolveren, som allerede skelner mellem "i ingen gruppe",
        // "i en gruppe der ikke står i groupSchedules" og "i grupper der peger på flere skemaer" —
        // tre svar der kræver tre forskellige handlinger.
        var withoutSchedule = withoutPlan
            .Where(c => !placedByConflictRule.ContainsKey(c))
            .Select(customer =>
            {
                var rows = byCustomer.First(g => string.Equals(g.Key, customer, StringComparison.Ordinal));
                var mps = rows
                    .Select(x => x.Entry.MeteringPointIdentification)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                return new CustomerWithoutSchedule(
                    customer,
                    rows.Select(x => x.Entry.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    mps.Count,
                    mps,
                    "no usable Xellent billing plan");
            })
            .OrderBy(c => c.CustomerNumber, StringComparer.Ordinal)
            .ToList();

        var totals = new SettlementConfigTotals(
            MeteringPointsInSettl: meteringPointsInSettl,
            MeteringPointsResolved: entries.Select(e => e.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Count(),
            UniqueCustomers: entries.Select(e => e.CustomerNumber).Distinct(StringComparer.Ordinal).Count(),
            Schedules: groups.Count,
            Unresolved: unresolved.Count,
            ScheduleConflicts: conflicts.Count,
            // Only the tie-breaks. A customer with no plan of her own was not placed by her group
            // instead of by her metering points — there was never anything else to place her by, and
            // counting her here would read as a disagreement that never happened.
            SchedulesFromSettlementGroup: 0,
            SchedulesFromConflictTable: placedByConflictTable.Count,
            CustomersNotInXellent: entries
                .Where(e => !e.CustomerInXellent)
                .Select(e => e.CustomerNumber)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            CustomersWithoutSchedule: withoutSchedule.Count);

        return new SettlementConfigDocument(
            generatedAt,
            environment,
            dataAreaId,
            totals,
            unmappedPlans.ToList(),
            groups,
            // Every unresolved MP is listed with its reason — nothing disappears silently.
            unresolved
                .OrderBy(u => u.MeteringPointIdentification, StringComparer.Ordinal)
                .ToList(),
            ScheduleConflicts: conflicts,
            CustomersWithoutSchedule: withoutSchedule);
    }
}
