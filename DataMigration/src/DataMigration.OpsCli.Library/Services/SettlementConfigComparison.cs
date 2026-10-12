namespace DataMigration.OpsCli.Services;

/// <summary>
/// Puts every candidate schedule and segment rule next to each other, one row per customer, and
/// counts where they disagree. Pure — every Settl fact is passed in already read.
///
/// <para><b>Legacy diagnostic comparison.</b> Xellent now determines proposals; this block retains
/// alternative rule outputs for existing consumers. No source may fill a gap in another, and a source
/// that cannot decide leaves its field null with a note saying why. A blank field that silently
/// inherited another rule's answer would make the two rules look like they agree.</para>
///
/// <para>Agreement is judged only over the sources that produced a value: two sources agreeing is
/// <see cref="SettlementConfigAgreement.Agree"/>, one source alone is
/// <see cref="SettlementConfigAgreement.Undecided"/> — silence is not consensus.</para>
/// </summary>
public static class SettlementConfigComparison
{
    /// <param name="entries">Every resolved metering point; grouped by customer here.</param>
    /// <param name="identityByCustomer">Customer number → CVR/CPR/blank. Missing = blank.</param>
    /// <param name="settlementGroupsByCustomer">Customer number → the groups the customer is really
    /// in, read from Settl. Missing = in no group.</param>
    /// <param name="segmentFromTemplateByCustomer">The rule in force today
    /// (<see cref="CustomerSegmentGrouper"/>), flattened to customer → segment. Customers it reported
    /// as conflicts are absent, which is the correct "it could not decide".</param>
    /// <param name="settlementGroupNamesInSettl">Every active group name in Settl, used to tell a map
    /// entry that names a non-existent group from one that simply has no members here. Null when the
    /// listing was not read — the "which map entries match nothing in Settl" answer is then withheld
    /// rather than reported as "all of them match". That list is what caught the en-dash in
    /// "PPA - afregning", so it must not be able to lie.</param>
    /// <param name="settlContractsRead">Whether the Settl contract listing was read at all. False
    /// makes the Settl-driven segment rule decline instead of concluding "on no SME contract".</param>
    public static (SettlementConfigComparisonSummary Summary, IReadOnlyList<SettlementConfigCustomerRow> Rows) Build(
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, CustomerIdentityKind>? identityByCustomer,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? settlementGroupsByCustomer,
        IReadOnlyDictionary<string, string>? segmentFromTemplateByCustomer,
        IReadOnlyCollection<string>? settlementGroupNamesInSettl,
        SettlementConfig config,
        bool settlContractsRead = true)
    {
        var rows = new List<SettlementConfigCustomerRow>();
        var groupsSeenWithoutSchedule = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var customerGroup in entries.GroupBy(e => e.CustomerNumber, StringComparer.Ordinal))
        {
            var customerNumber = customerGroup.Key;
            var customerEntries = customerGroup.ToList();
            var identity = identityByCustomer is not null
                && identityByCustomer.TryGetValue(customerNumber, out var kind)
                ? kind
                : CustomerIdentityKind.Blank;

            // Null map = membership never read, and every customer must say so. A customer simply
            // absent from a map that WAS read is a member of nothing, which is a different fact.
            IReadOnlyList<string>? groups = settlementGroupsByCustomer is null
                ? null
                : settlementGroupsByCustomer.TryGetValue(customerNumber, out var found) ? found : [];
            foreach (var g in (groups ?? []).Where(g => config.GroupSchedules?.ContainsKey(g) != true))
                groupsSeenWithoutSchedule.Add(g);

            var notes = new List<string>();

            // Said first, because it explains every empty Xellent field below at once. Without it the
            // row reads as "Xellent had nothing to say about this customer", when the truth is that
            // Xellent has plenty to say — about the person who held her metering points before her.
            if (customerEntries.All(e => !e.CustomerInXellent))
                notes.Add("customer number is no Xellent account: created in Settl, so the metering points' Xellent contract is the previous holder's");

            // --- Schedule: Settl group vs. Xellent billing plan ---
            var fromGroup = SettlementGroupScheduleResolver.Resolve(groups, config.GroupSchedules);
            if (fromGroup.Note is not null) notes.Add($"schedule/group: {fromGroup.Note}");

            var billingPlans = Distinct(customerEntries.Select(e => e.BillingPlanNum));
            var (fromPlan, planNote) = ResolveScheduleFromBillingPlan(billingPlans, config.Schedules);
            if (planNote is not null) notes.Add($"schedule/billingPlan: {planNote}");

            // --- Segment: three rules ---
            // Gathered by the resolver itself, so the rule sees the same two inputs here as it does
            // where it places Settl-native customers in a segment before the grouping.
            var (settlContracts, hasExemption) = SettlDrivenSegmentResolver.Gather(customerEntries, settlContractsRead);

            var fromSettl = SettlDrivenSegmentResolver.Resolve(identity, settlContracts, hasExemption, config.SettlSegments);
            if (fromSettl.Note is not null) notes.Add($"segment/settl: {fromSettl.Note}");

            var fromTable = TemplateProductSegmentResolver.Resolve(identity, customerEntries, config.TemplateProducts);
            if (fromTable.Note is not null) notes.Add($"segment/templateProduct: {fromTable.Note}");

            string? fromTemplate = segmentFromTemplateByCustomer is not null
                && segmentFromTemplateByCustomer.TryGetValue(customerNumber, out var templateSegment)
                ? templateSegment
                : null;
            // A customer with no template at all is in no conflict either — pointing her at
            // segmentConflicts would send the reader looking for a row that is not there.
            if (fromTemplate is null)
            {
                notes.Add(customerEntries.All(e => !e.CustomerInXellent)
                    ? "segment/template: no template — see the note above"
                    : "segment/template: no single segment (see segmentConflicts)");
            }

            rows.Add(new SettlementConfigCustomerRow(
                customerNumber,
                customerEntries.Select(e => e.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                identity,
                customerEntries.Select(e => e.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Count(),
                groups,
                fromGroup.Schedule,
                billingPlans,
                fromPlan,
                Agreement([fromGroup.Schedule, fromPlan]),
                fromTemplate,
                fromSettl.Segment,
                fromTable.Segment,
                Agreement([fromTemplate, fromSettl.Segment, fromTable.Segment]),
                fromSettl.Sme,
                hasExemption,
                Distinct(customerEntries.Select(e => e.ContractTemplate)),
                Distinct(customerEntries.SelectMany(e => e.ProductNums ?? [])),
                settlContracts,
                fromTable.ExpectedContracts,
                notes));
        }

        rows = rows.OrderBy(r => r.CustomerNumber, StringComparer.Ordinal).ToList();

        // Map entries naming a group Settl does not have: a typo and a group that exists but is empty
        // are different problems, and only the full Settl name list can tell them apart. Withheld
        // (null, not empty) when that list was never read — an empty list here reads as "every entry
        // matches a real group", which is exactly the claim we cannot make.
        var namesNotInSettl = settlementGroupNamesInSettl is null
            ? null
            : (config.GroupSchedules ?? new Dictionary<string, string>())
                .Keys
                .Where(name => !settlementGroupNamesInSettl.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        var summary = new SettlementConfigComparisonSummary(
            rows.Count(r => r.ScheduleAgreement == SettlementConfigAgreement.Agree),
            rows.Count(r => r.ScheduleAgreement == SettlementConfigAgreement.Disagree),
            rows.Count(r => r.ScheduleAgreement == SettlementConfigAgreement.Undecided),
            rows.Count(r => r.SegmentAgreement == SettlementConfigAgreement.Agree),
            rows.Count(r => r.SegmentAgreement == SettlementConfigAgreement.Disagree),
            rows.Count(r => r.SegmentAgreement == SettlementConfigAgreement.Undecided),
            rows.Count(r => r.SettlementGroups is { Count: > 0 }),
            rows.Count(r => r.HasExemption == true),
            groupsSeenWithoutSchedule.ToList(),
            namesNotInSettl,
            SettlementGroupMembershipRead: settlementGroupsByCustomer is not null,
            SettlContractsRead: settlContractsRead);

        return (summary, rows);
    }

    /// <summary>
    /// The schedule the rule in force today gives. A customer whose metering points span billing
    /// plans that map to different schedules gets none — the same reason the segment rule refuses to
    /// pick: the value being compared is customer-level.
    /// </summary>
    private static (string? Schedule, string? Note) ResolveScheduleFromBillingPlan(
        IReadOnlyList<string> billingPlans,
        IReadOnlyDictionary<string, string>? scheduleMap)
    {
        if (billingPlans.Count == 0) return (null, "no billing plan");
        if (scheduleMap is null || scheduleMap.Count == 0) return (null, "schedules not configured");

        var unmapped = billingPlans.Where(p => !scheduleMap.ContainsKey(p)).ToList();
        var schedules = billingPlans
            .Where(scheduleMap.ContainsKey)
            .Select(p => scheduleMap[p])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var note = unmapped.Count > 0 ? "billing plan(s) not in schedules: " + string.Join(", ", unmapped) : null;

        return schedules.Count switch
        {
            1 => (schedules[0], note),
            0 => (null, note ?? "no billing plan mapped to a schedule"),
            _ => (null, note is null
                ? "billing plans yield several schedules: " + string.Join(", ", schedules)
                : note + "; billing plans yield several schedules: " + string.Join(", ", schedules))
        };
    }

    /// <summary>
    /// Agreement across the sources that actually produced a value. Fewer than two values means
    /// there was nothing to compare, which is <see cref="SettlementConfigAgreement.Undecided"/> —
    /// counting it as agreement would inflate the confidence the numbers are read with.
    /// </summary>
    private static SettlementConfigAgreement Agreement(IReadOnlyList<string?> values)
    {
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (present.Count < 2) return SettlementConfigAgreement.Undecided;
        return present.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? SettlementConfigAgreement.Agree
            : SettlementConfigAgreement.Disagree;
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.Ordinal)
        .ToList();
}
