namespace DataMigration.OpsCli.Services;

public sealed record SettlementMeterPointTypePeriod(string? Code, DateOnly Start, DateOnly End);
public sealed record SettlementMeterPointTypeEvidence(string? Code, string ReadStatus)
{
    /// <summary>Xellent period ends are inclusive; 1900-01-01 means open ended.</summary>
    public static SettlementMeterPointTypeEvidence Resolve(IEnumerable<SettlementMeterPointTypePeriod> periods, DateOnly on)
    {
        var active = periods.Where(p => p.Start <= on && (p.End == new DateOnly(1900, 1, 1) || p.End >= on)).ToArray();
        if (active.Length == 0 || active.All(p => string.IsNullOrWhiteSpace(p.Code))) return new(null, "Missing");
        if (active.Length != 1) return new(null, "Ambiguous");
        return new(active[0].Code!.Trim(), "Read");
    }
}
public sealed record SettlementConfigE18Customer(string CustomerNumber, string? OriginalProposedSegment,
    string? FinalProposedSegment, bool Changed, IReadOnlyList<string> MeteringPointIdentifications);
public sealed record SettlementConfigE18Issue(string MeteringPointIdentification, string CustomerNumber, string Reason);
public sealed record SettlementConfigE18Review(string Source, IReadOnlyList<SettlementConfigE18Customer> Customers,
    IReadOnlyList<SettlementConfigE18Issue> Issues);

/// <summary>Dated Xellent PROD means E18. D18/OtherProduction is a different type.
/// This rule is applied only after historical ownership and portfolio validation.</summary>
public static class SettlementE18Proposal
{
    public const string Segment = "Erhverv_ProduktionE18_UG";
    public static (CustomerSegmentGrouper.CustomerSegmentResult Segments, SettlementConfigE18Review Review) Apply(
        CustomerSegmentGrouper.CustomerSegmentResult original, IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, CustomerIdentityKind> identities)
    {
        var baseline = original.Segments.Where(g => g.Mapped).SelectMany(g => g.Customers.Select(c => (c.CustomerNumber, g.Segment)))
            .ToDictionary(c => c.CustomerNumber, c => c.Segment, StringComparer.Ordinal);
        var e18 = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var customers = new List<SettlementConfigE18Customer>();
        var issues = new List<SettlementConfigE18Issue>();
        foreach (var group in entries.GroupBy(e => e.CustomerNumber, StringComparer.Ordinal))
        {
            var oldSegment = baseline.GetValueOrDefault(group.Key);
            var eligible = identities.GetValueOrDefault(group.Key) == CustomerIdentityKind.Cvr || oldSegment == "SME";
            foreach (var entry in group.Where(e => e.MeterPointTypeReadStatus != "Read"))
                issues.Add(new(entry.MeteringPointIdentification, group.Key, "MeterPointType" + entry.MeterPointTypeReadStatus));
            if (!eligible) continue;
            var points = group.Where(e => e.CustomerInXellent && e.MeterPointTypeReadStatus == "Read"
                && string.Equals(e.MeterPointTypeCode, "PROD", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.MeteringPointIdentification).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string? final = oldSegment;
            if (points.Length > 0) { e18.Add(group.Key); final = Segment; }
            else if (group.Any(e => e.MeterPointTypeReadStatus != "Read")) { blocked.Add(group.Key); final = null; }
            customers.Add(new(group.Key, oldSegment, final, !string.Equals(oldSegment, final, StringComparison.OrdinalIgnoreCase), points));
        }
        var groups = original.Segments.Select(g => g with {
            Customers = g.Customers.Where(c => !e18.Contains(c.CustomerNumber) && !blocked.Contains(c.CustomerNumber)).ToArray() })
            .Where(g => g.Customers.Count > 0).Select(g => g with {
                CustomerCount = g.Customers.Count, MeteringPointCount = g.Customers.Sum(c => c.MeteringPointCount) }).ToList();
        if (e18.Count > 0)
        {
            var productionEntries = entries.Where(e => e18.Contains(e.CustomerNumber)).ToArray();
            var members = productionEntries.GroupBy(e => e.CustomerNumber, StringComparer.Ordinal).Select(g => new SettlementConfigCustomer(
                g.Key, g.Select(e => e.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)), g.Count()))
                .OrderBy(c => c.CustomerNumber, StringComparer.Ordinal).ToArray();
            groups.Add(new(Segment, true, productionEntries.Select(e => e.ContractTemplate).OfType<string>().Distinct().Order().ToArray(),
                members.Length, members.Sum(c => c.MeteringPointCount), members));
        }
        var unresolved = original.Unresolved.Where(u => !e18.Contains(u.CustomerNumber)).ToList();
        foreach (var customer in blocked)
            if (!unresolved.Any(u => u.CustomerNumber == customer))
                unresolved.Add(new(customer, entries.Where(e => e.CustomerNumber == customer).Select(e => e.MeteringPointIdentification).Distinct().Order().ToArray()));
        return (original with {
            Segments = groups.OrderBy(g => g.Segment, StringComparer.Ordinal).ToArray(),
            Conflicts = original.Conflicts.Where(c => !e18.Contains(c.CustomerNumber) && !blocked.Contains(c.CustomerNumber)).ToArray(),
            Unresolved = unresolved.OrderBy(u => u.CustomerNumber, StringComparer.Ordinal).ToArray(),
            MultipleSegmentCustomers = original.MultipleSegmentCustomers.Where(c => !e18.Contains(c) && !blocked.Contains(c)).ToArray()
        }, new("XellentEgsMeterpointTypecodeperiods", customers.OrderBy(c => c.CustomerNumber, StringComparer.Ordinal).ToArray(),
            issues.OrderBy(i => i.MeteringPointIdentification, StringComparer.Ordinal).ToArray()));
    }
}
