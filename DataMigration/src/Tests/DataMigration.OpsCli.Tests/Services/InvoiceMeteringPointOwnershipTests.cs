using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class InvoiceMeteringPointOwnershipTests
{
    [Fact]
    public void Invoices_from_one_source_account_can_belong_to_different_customers()
    {
        var first = new InvoiceMeteringPointOwnership.InvoiceKey("101253", "5270675");
        var second = new InvoiceMeteringPointOwnership.InvoiceKey("101253", "5270676");
        var contracts = new Dictionary<InvoiceMeteringPointOwnership.InvoiceKey, string[]>
        {
            [first] = ["C1"], [second] = ["C2"]
        };
        var mps = new Dictionary<string, string[]> { ["C1"] = ["MP1"], ["C2"] = ["MP2"] };
        var customers = new Dictionary<string, string> { ["MP1"] = "952461", ["MP2"] = "9101254" };

        Assert.Equal("952461", InvoiceMeteringPointOwnership.Resolve(first, contracts, mps, customers).Customer);
        Assert.Equal("9101254", InvoiceMeteringPointOwnership.Resolve(second, contracts, mps, customers).Customer);
    }

    [Fact]
    public void Missing_or_nonunique_links_never_choose_a_customer()
    {
        var key = new InvoiceMeteringPointOwnership.InvoiceKey("101253", "5270675");
        var contracts = new Dictionary<InvoiceMeteringPointOwnership.InvoiceKey, string[]> { [key] = ["C1"] };
        var mps = new Dictionary<string, string[]> { ["C1"] = ["MP1", "MP2"] };
        var customers = new Dictionary<string, string> { ["MP1"] = "A" };

        var result = InvoiceMeteringPointOwnership.Resolve(key, contracts, mps, customers);
        Assert.Null(result.Customer);
        Assert.Equal("ContractHasNoUniqueParentMeteringPoint", result.Reason);

        mps["C1"] = ["MP3"];
        result = InvoiceMeteringPointOwnership.Resolve(key, contracts, mps, customers);
        Assert.Null(result.Customer);
        Assert.Equal("MeteringPointHasNoInScopeSettlCustomer", result.Reason);
    }
}
