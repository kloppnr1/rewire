namespace DataMigration.OpsCli.Services;

/// <summary>Proposes customer segments from Xellent identity, templates and dated charging
/// subscriptions. Settl fields and legacy Settl-rule arguments are comparison inputs only.
/// Private charging follows Eje, then Leje, then Privat after a successful ownership read.</summary>
public static class CustomerSegmentGrouper
{
    private const string OwnedChargeSegment = "Privat_Opladning_Eje";
    private const string RentedChargeSegment = "Privat_Opladning_Leje";

    /// <param name="MultipleSegmentCustomers">Customers placed by the <c>multipleSegments</c> answer
    /// instead of by their own metering points agreeing — visible so the number can be watched.</param>
    /// <param name="SettlRuleCustomers">Legacy counter, always empty: Settl never assigns a proposal.</param>
    public sealed record CustomerSegmentResult(
        IReadOnlyList<string> UnmappedContractTemplates,
        IReadOnlyList<CustomerSegmentGroup> Segments,
        IReadOnlyList<CustomerSegmentConflict> Conflicts,
        IReadOnlyList<CustomerSegmentUnresolved> Unresolved,
        IReadOnlyList<string> MultipleSegmentCustomers,
        IReadOnlyList<string> SettlRuleCustomers);

    /// <param name="identityByCustomer">Customer number → the identification found in Xellent. A
    /// customer missing from the map is treated as <see cref="CustomerIdentityKind.Blank"/>: no
    /// identifier was found, which is exactly the blank case.</param>
    /// <param name="multipleSegments">Where to put a customer whose metering points are each
    /// determined but land on different segments. Null leaves them as conflicts.</param>
    /// <param name="segmentByCustomerFromSettlRule">Legacy comparison input; ignored for proposals.</param>
    /// <param name="segmentByCustomerFromXellentCharge">Customer number → ownership from Charge_Abo
    /// products active at COS-1. This is the ownership source, independent of Settl.</param>
    public static CustomerSegmentResult Build(
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, SegmentRule>? segmentMap,
        IReadOnlyDictionary<string, CustomerIdentityKind>? identityByCustomer = null,
        string? multipleSegments = null,
        IReadOnlyDictionary<string, string>? segmentByCustomerFromSettlRule = null,
        IReadOnlyDictionary<string, string>? segmentByCustomerFromXellentCharge = null,
        IReadOnlySet<string>? chargeReadFailedCustomers = null)
    {
        // Case-insensitive on everything that comes out of Xellent (templates, and thereby the
        // fallback segment names): the same template can be spelled with different casing and is
        // still one segment. Customer numbers stay ordinal — they are ids, not free text.
        var unmappedTemplates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<CustomerSegmentUnresolved>();
        var conflicts = new List<CustomerSegmentConflict>();
        var fromMultiple = new List<string>();
        var fromSettlRule = new List<string>();

        // segment → customer → MP ids, plus which templates fed each segment.
        var bySegment = new Dictionary<string, List<(SettlementConfigCustomer Customer, bool Mapped, IReadOnlyList<string> Templates)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var customerGroup in entries.GroupBy(e => e.CustomerNumber, StringComparer.Ordinal))
        {
            var name = customerGroup.Select(e => e.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var identity = identityByCustomer != null
                && identityByCustomer.TryGetValue(customerGroup.Key, out var found)
                ? found
                : CustomerIdentityKind.Blank;

            // Retain each missing template as an unresolved Xellent source gap.
            var withoutTemplate = customerGroup
                .Where(e => string.IsNullOrWhiteSpace(e.ContractTemplate))
                .Select(e => e.MeteringPointIdentification)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var candidates = customerGroup
                .Where(e => !string.IsNullOrWhiteSpace(e.ContractTemplate))
                .GroupBy(e => e.ContractTemplate!, StringComparer.OrdinalIgnoreCase)
                .Select(templateGroup =>
                {
                    // Unmapped covers both "no entry for the template" and "entry that says nothing
                    // about this identification" — either way the segment is undecided, and the raw
                    // template name keeps the customer visible instead of dropping it.
                    //
                    // The lookup key is normalised the way the segments table itself is keyed, so a
                    // Xellent template spelled "V Privat" finds the entry written "V_Privat" and the
                    // other way round. Only the matching is normalised — the template reported back
                    // stays the raw Xellent spelling, so the operator sees what Xellent holds.
                    var candidateSegments = segmentMap != null
                        && segmentMap.TryGetValue(
                            SettlementConfigKeys.NormalizeTemplate(templateGroup.Key), out var rule)
                        ? rule.For(identity)
                        : [];
                    var mapped = candidateSegments.Count > 0;
                    if (!mapped) unmappedTemplates.Add(templateGroup.Key);

                    var mpIds = templateGroup
                        .Select(e => e.MeteringPointIdentification)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToList();

                    return new CustomerSegmentCandidate(
                        templateGroup.Key,
                        mapped ? candidateSegments : [templateGroup.Key],
                        mapped,
                        mpIds.Count,
                        mpIds);
                })
                .OrderBy(c => c.ContractTemplate, StringComparer.Ordinal)
                .ToList();

            if (chargeReadFailedCustomers?.Contains(customerGroup.Key) == true)
            {
                unresolved.Add(new CustomerSegmentUnresolved(customerGroup.Key,
                    customerGroup.Select(e => e.MeteringPointIdentification).Distinct().Order().ToList()));
                continue;
            }

            if (identity != CustomerIdentityKind.Cvr
                && candidates.Count > 0 && candidates.All(c => c.Mapped) && withoutTemplate.Count == 0
                && segmentByCustomerFromXellentCharge is not null
                && segmentByCustomerFromXellentCharge.TryGetValue(customerGroup.Key, out var xellentChargeSegment))
            {
                if (!bySegment.TryGetValue(xellentChargeSegment, out var chargeMembers))
                    bySegment[xellentChargeSegment] = chargeMembers = [];
                chargeMembers.Add((
                    new SettlementConfigCustomer(customerGroup.Key, name, customerGroup.Count()),
                    candidates.All(c => c.Mapped),
                    candidates.Select(c => c.ContractTemplate).ToList()));
                continue;
            }

            if (candidates.Count == 0)
            {
                unresolved.Add(new CustomerSegmentUnresolved(customerGroup.Key, withoutTemplate));
                continue;
            }

            if (withoutTemplate.Count > 0)
                unresolved.Add(new CustomerSegmentUnresolved(customerGroup.Key, withoutTemplate));

            var ambiguous = candidates.Any(c => c.Segments.Count > 1);
            var determined = candidates
                .Where(c => c.Segments.Count == 1)
                .Select(c => c.Segments[0])
                // Casing alone must not make a customer look like it spans two segments.
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var determinedChargeSegments = determined
                .Where(s => s.StartsWith("Privat_Opladning_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // One determined segment is the customer's own answer. Several determined segments is the
            // disagreement the file answers on our behalf — but only when the file says so, never
            // when a template was ambiguous to begin with, and never when one of the "segments" is
            // just an unmapped template's own name. The last one matters: a template deliberately
            // left without an answer would otherwise be swallowed by the disagreement rule and land
            // the customer in a segment, which is the opposite of leaving it undecided.
            var resolved = determined.Count switch
            {
                1 => determined[0],
                > 1 when !ambiguous && candidates.All(c => c.Mapped)
                    && identity != CustomerIdentityKind.Cvr
                    && determinedChargeSegments.Count > 0 => determinedChargeSegments.Contains(OwnedChargeSegment, StringComparer.OrdinalIgnoreCase)
                        ? OwnedChargeSegment : RentedChargeSegment,
                > 1 when !ambiguous && candidates.All(c => c.Mapped)
                    && identity == CustomerIdentityKind.Cpr
                    && determinedChargeSegments.Count == 0
                    && determined.Contains("Privat", StringComparer.OrdinalIgnoreCase) => "Privat",
                > 1 when !ambiguous && candidates.All(c => c.Mapped)
                    && identity == CustomerIdentityKind.Cpr && determinedChargeSegments.Count > 1 => null,
                > 1 when !ambiguous && candidates.All(c => c.Mapped) => multipleSegments,
                _ => null
            };

            if (ambiguous || resolved is null)
            {
                conflicts.Add(new CustomerSegmentConflict(
                    customerGroup.Key,
                    name,
                    (ambiguous, determined.Count > 1) switch
                    {
                        (true, true) => CustomerSegmentConflictReason.Both,
                        (true, false) => CustomerSegmentConflictReason.AmbiguousTemplate,
                        _ => CustomerSegmentConflictReason.MultipleSegments
                    },
                    candidates,
                    identity));
                continue;
            }

            var segment = resolved;
            // Kept apart so the run can say how many customers landed in a segment by the file's
            // standing answer rather than by their own metering points agreeing.
            if (determined.Count > 1 && string.Equals(segment, multipleSegments, StringComparison.OrdinalIgnoreCase))
                fromMultiple.Add(customerGroup.Key);

            if (!bySegment.TryGetValue(segment, out var members))
                bySegment[segment] = members = [];

            members.Add((
                new SettlementConfigCustomer(
                    customerGroup.Key,
                    name,
                    candidates.Sum(c => c.MeteringPointCount)),
                candidates.All(c => c.Mapped),
                candidates.Select(c => c.ContractTemplate).ToList()));
        }

        var groups = bySegment
            .Select(kv => new CustomerSegmentGroup(
                kv.Key,
                kv.Value.All(m => m.Mapped),
                kv.Value.SelectMany(m => m.Templates).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                kv.Value.Count,
                kv.Value.Sum(m => m.Customer.MeteringPointCount),
                kv.Value.Select(m => m.Customer).OrderBy(c => c.CustomerNumber, StringComparer.Ordinal).ToList()))
            .OrderBy(g => g.Segment, StringComparer.Ordinal)
            .ToList();

        return new CustomerSegmentResult(
            unmappedTemplates.ToList(),
            groups,
            conflicts.OrderBy(c => c.CustomerNumber, StringComparer.Ordinal).ToList(),
            unresolved.OrderBy(u => u.CustomerNumber, StringComparer.Ordinal).ToList(),
            fromMultiple.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            fromSettlRule.OrderBy(c => c, StringComparer.Ordinal).ToList());
    }
}
