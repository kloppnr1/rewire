using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SettlCustomerCompanyScopeTests
{
    [Fact]
    public void Meh_only_customer_and_mp_are_reported_for_exclusion()
    {
        var result = SettlCustomerCompanyScope.Select(
            new Dictionary<string, string> { ["mp-meh"] = "customer-meh" },
            Set(), Set("mp-meh"));

        Assert.Contains("customer-meh", result.ExcludedCustomers);
        Assert.Contains("mp-meh", result.OmittedMeteringPoints);
        Assert.Contains(result.Issues, i => i.Reason == "MehOnlySettlCustomer"
            && i.SettlCustomers == "customer-meh");
        Assert.Contains(result.Issues, i => i.Reason == "MehOnlyXellentMeteringPoint"
            && i.MeteringPoint == "mp-meh");
    }

    [Fact]
    public void Mixed_customer_keeps_for_mp_but_omits_meh_only_mp()
    {
        var result = SettlCustomerCompanyScope.Select(
            new Dictionary<string, string>
            {
                ["mp-for"] = "mixed", ["mp-meh"] = "mixed"
            }, Set("mp-for"), Set("mp-meh"));

        Assert.Empty(result.ExcludedCustomers);
        Assert.DoesNotContain("mp-for", result.OmittedMeteringPoints);
        Assert.Contains("mp-meh", result.OmittedMeteringPoints);
    }

    [Fact]
    public void Mp_with_both_company_origins_is_not_meh_only()
    {
        var result = SettlCustomerCompanyScope.Select(
            new Dictionary<string, string> { ["571313144100936619"] = "484535" },
            Set("571313144100936619"), Set("571313144100936619"));

        Assert.Empty(result.ExcludedCustomers);
        Assert.Empty(result.OmittedMeteringPoints);
    }

    [Fact]
    public void Settl_native_mp_is_not_inferred_to_be_meh()
    {
        var result = SettlCustomerCompanyScope.Select(
            new Dictionary<string, string> { ["mp-new"] = "new-customer" }, Set(), Set());

        Assert.Empty(result.ExcludedCustomers);
        Assert.Empty(result.OmittedMeteringPoints);
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
