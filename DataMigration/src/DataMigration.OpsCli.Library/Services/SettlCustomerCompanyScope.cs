namespace DataMigration.OpsCli.Services;

internal sealed record SettlCustomerCompanyScopeResult(
    IReadOnlySet<string> ExcludedCustomers,
    IReadOnlySet<string> OmittedMeteringPoints,
    IReadOnlyList<SourceAccountOwnershipIssue> Issues);

/// <summary>
/// A Settl electricity MP does not say which Xellent company it came from. Keep customers with
/// any FOR or blank-company legacy origin, omit MEH-only MPs, and leave Settl-native MPs
/// untouched rather than calling them MEH. A customer whose only Xellent company origin is MEH
/// is out of this FOR invoice state.
/// </summary>
internal static class SettlCustomerCompanyScope
{
    internal static SettlCustomerCompanyScopeResult Select(
        IReadOnlyDictionary<string, string> settlCustomerByMp,
        IReadOnlySet<string> inScopeMeteringPoints,
        IReadOnlySet<string> mehMeteringPoints)
    {
        var byCustomer = settlCustomerByMp
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray(), StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var omitted = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<SourceAccountOwnershipIssue>();

        foreach (var (customer, mps) in byCustomer.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var hasInScopeOrigin = mps.Any(inScopeMeteringPoints.Contains);
            var hasMeh = mps.Any(mehMeteringPoints.Contains);
            if (!hasInScopeOrigin && hasMeh)
            {
                excluded.Add(customer);
                issues.Add(new SourceAccountOwnershipIssue(
                    "MehOnlySettlCustomer", "", string.Join('|', mps.Order(StringComparer.Ordinal)), customer));
            }
            foreach (var mp in mps.Where(mp => mehMeteringPoints.Contains(mp)
                && !inScopeMeteringPoints.Contains(mp)))
            {
                omitted.Add(mp);
                issues.Add(new SourceAccountOwnershipIssue(
                    "MehOnlyXellentMeteringPoint", "", mp, customer));
            }
        }

        return new SettlCustomerCompanyScopeResult(excluded, omitted, issues);
    }
}
