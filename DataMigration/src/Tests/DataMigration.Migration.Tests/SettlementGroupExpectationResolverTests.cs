using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Products;
using DataMigration.Migration.Services;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Locks the local (no-Settl) per-customer settlement-group resolution: a customer's contract
/// billing-plan codes are mapped to the expected Settl Settlement Group name(s) via the profile's
/// SettlementGroupMap, so the report can show the expected group(s) in (offline) dry-run. Keys per
/// customer because Settl groups are customer-scoped (the customer, not the MP, is added).
/// </summary>
public class SettlementGroupExpectationResolverTests
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["Måned"] = "Månedlige opgørelser",
        ["Kvartal"] = "Kvartalsvis Opgørelse",
    };

    [Fact]
    public void ResolveByCustomer_empty_map_returns_empty()
    {
        var customers = new[] { CustomerWith("acct-1", ContractWith("c1", "Måned", "MP-1")) };

        var result = SettlementGroupExpectationResolver.ResolveByCustomer(customers, new Dictionary<string, string>());

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveByCustomer_maps_account_to_distinct_groups()
    {
        var customers = new[]
        {
            CustomerWith("acct-1",
                ContractWith("c1", "Måned", "MP-1"),
                ContractWith("c2", "Kvartal", "MP-2")),
            CustomerWith("acct-2",
                ContractWith("c3", "Måned", "MP-3")),
        };

        var result = SettlementGroupExpectationResolver.ResolveByCustomer(customers, Map);

        Assert.Equal(new[] { "Månedlige opgørelser", "Kvartalsvis Opgørelse" }, result["acct-1"]);
        Assert.Equal(new[] { "Månedlige opgørelser" }, result["acct-2"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ResolveByCustomer_dedups_repeated_group_across_contracts()
    {
        var customers = new[]
        {
            CustomerWith("acct-1",
                ContractWith("c1", "Måned", "MP-1"),
                ContractWith("c2", "Måned", "MP-2")),
        };

        var result = SettlementGroupExpectationResolver.ResolveByCustomer(customers, Map);

        Assert.Equal(new[] { "Månedlige opgørelser" }, result["acct-1"]);
    }

    [Fact]
    public void ResolveByCustomer_omits_customers_without_mapped_plans()
    {
        var customers = new[]
        {
            CustomerWith("acct-1", ContractWith("c1", "UkendtPlan", "MP-1")),
        };

        var result = SettlementGroupExpectationResolver.ResolveByCustomer(customers, Map);

        Assert.Empty(result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Contract ContractWith(string number, string planCode, params string[] mps) =>
        new(
            Number: number,
            Description: "",
            Start: new DateOnly(2026, 1, 1),
            Commodity: Commodity.Electricity,
            CustomerNumber: "acct",
            Billing: new BillingSchedule(BillingFrequency.Monthly, planCode),
            MeteringPoints: mps.Select(mp => new MeteringPointAssignment(
                MeteringPointId: mp,
                Product: new Product(Code: "P", Name: "P", Commodity: Commodity.Electricity),
                Start: new DateOnly(2026, 1, 1),
                EndDate: new DateOnly(1900, 1, 1))).ToArray());

    private static Customer CustomerWith(string account, params Contract[] contracts) =>
        BaseCustomer() with { AccountNumber = account, Contracts = contracts };

    private static Customer BaseCustomer() =>
        new(
            AccountNumber: "acct",
            Name: "acct",
            AdditionalName: null,
            Type: CustomerType.Person,
            Cvr: null,
            Cpr: null,
            Address: new Address("", null, null, null, "", "", null, null, null, "DK", null),
            Contact: ContactInfo.Empty,
            Billing: new CustomerBilling(DefaultEan: null, Pbs: null, MobilePay: null),
            Contracts: Array.Empty<Contract>());
}
