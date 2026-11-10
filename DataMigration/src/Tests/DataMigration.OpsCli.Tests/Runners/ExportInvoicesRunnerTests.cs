using DataMigration.Domain.Customers;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Runners;
using OpsAccountGrouping = DataMigration.OpsCli.Data.AccountGrouping;

namespace DataMigration.OpsCli.Tests.Runners;

public class BuildManifestEntriesTests
{
    private static GenerateStateRunner CreateRunner() =>
        new("unused");

    private static Invoice MakeInvoice(
        string account,
        string invoiceId,
        BillingCategory billingCategory = BillingCategory.Final) =>
        new(
            InvoiceId: invoiceId,
            AccountNumber: account,
            InvoiceDate: new DateTime(2025, 6, 1),
            DueDate: new DateTime(2025, 7, 1),
            DeliveryAddress: "Fiktivgadek 1",
            InvoicingAddress: "Fiktivgadek 1",
            BillingCategory: billingCategory,
            PaymentMedia: PaymentMedia.Pbs,
            InvoiceStatus: InvoiceStatus.Invoiced,
            InvoiceAmount: 1234.56m,
            SumTax: 308.64m);

    [Fact]
    public void Groups_invoices_by_settl_account()
    {
        var runner = CreateRunner();
        var invoices = new List<Invoice>
        {
            MakeInvoice("100001", "INV001"),
            MakeInvoice("100001", "INV002"),
            MakeInvoice("100002", "INV003")
        };
        var eligible = new HashSet<string> { "100001", "100002" };
        var groupings = new Dictionary<string, OpsAccountGrouping>();

        var result = runner.BuildManifestEntries(invoices, eligible, groupings);

        Assert.Equal(2, result.Count);
        Assert.Single(result, e => e.SettlAccountNumber == "100001" && e.Invoices.Count == 2);
        Assert.Single(result, e => e.SettlAccountNumber == "100002" && e.Invoices.Count == 1);
    }

    [Fact]
    public void Merges_accounts_via_grouping()
    {
        var runner = CreateRunner();
        var invoices = new List<Invoice>
        {
            MakeInvoice("100001", "INV001"),
            MakeInvoice("100002", "INV002")
        };
        var eligible = new HashSet<string> { "100001", "100002" };
        var groupings = new Dictionary<string, OpsAccountGrouping>
        {
            ["100001"] = new("100001", "100001", "CVR-GRP"),
            ["100002"] = new("100002", "100001", "CVR-GRP")
        };

        var result = runner.BuildManifestEntries(invoices, eligible, groupings);

        var entry = Assert.Single(result);
        Assert.Equal("100001", entry.SettlAccountNumber);
        Assert.Equal(2, entry.Invoices.Count);
        Assert.Equal(new List<string> { "100001", "100002" }, entry.SourceAccounts);
        Assert.Equal("CVR-GRP", entry.GroupKey);
    }

    [Fact]
    public void Uses_account_as_group_key_when_no_grouping()
    {
        var runner = CreateRunner();
        var invoices = new List<Invoice> { MakeInvoice("100001", "INV001") };
        var eligible = new HashSet<string> { "100001" };

        var result = runner.BuildManifestEntries(invoices, eligible, new Dictionary<string, OpsAccountGrouping>());

        var entry = Assert.Single(result);
        Assert.Equal("100001", entry.GroupKey);
    }

    [Fact]
    public void Empty_invoices_returns_empty_manifest()
    {
        var runner = CreateRunner();

        var result = runner.BuildManifestEntries(
            new List<Invoice>(),
            new HashSet<string>(),
            new Dictionary<string, OpsAccountGrouping>());

        Assert.Empty(result);
    }

    [Fact]
    public void Billing_category_mapped_to_label_in_manifest()
    {
        var runner = CreateRunner();
        var invoices = new List<Invoice> { MakeInvoice("100001", "INV001", BillingCategory.Final) };
        var eligible = new HashSet<string> { "100001" };

        var result = runner.BuildManifestEntries(invoices, eligible, new Dictionary<string, OpsAccountGrouping>());

        Assert.Equal("Slutafregning", result[0].Invoices[0].BillingCategory);
    }
}

public class MergeInvoicesIntoStateTests
{
    [Fact]
    public void Merge_updates_matching_account_invoices()
    {
        var state = new MigrationStateDocument
        {
            Accounts =
            [
                new MigratedAccount { SettlAccountNumber = "100001", Invoices = [] },
                new MigratedAccount { SettlAccountNumber = "100002", Invoices = [] }
            ]
        };

        var newEntries = new List<MigratedAccount>
        {
            new MigratedAccount { SettlAccountNumber = "100001", Invoices = [new InvoiceManifestItem { InvoiceId = "INV001" }] }
        };

        GenerateStateRunner.MergeInvoicesIntoState(state, newEntries);

        Assert.Single(state.Accounts[0].Invoices);
        Assert.Equal("INV001", state.Accounts[0].Invoices[0].InvoiceId);
    }

    [Fact]
    public void Merge_preserves_invoices_on_accounts_not_in_new_entries()
    {
        var state = new MigrationStateDocument
        {
            Accounts =
            [
                new MigratedAccount
                {
                    SettlAccountNumber = "100001",
                    Invoices = [new InvoiceManifestItem { InvoiceId = "EXISTING" }]
                },
                new MigratedAccount { SettlAccountNumber = "100002", Invoices = [] }
            ]
        };

        var newEntries = new List<MigratedAccount>
        {
            new MigratedAccount { SettlAccountNumber = "100002", Invoices = [new InvoiceManifestItem { InvoiceId = "INV002" }] }
        };

        GenerateStateRunner.MergeInvoicesIntoState(state, newEntries);

        Assert.Single(state.Accounts[0].Invoices);
        Assert.Equal("EXISTING", state.Accounts[0].Invoices[0].InvoiceId);
        Assert.Single(state.Accounts[1].Invoices);
        Assert.Equal("INV002", state.Accounts[1].Invoices[0].InvoiceId);
    }

    [Fact]
    public void Merge_with_empty_new_entries_leaves_state_unchanged()
    {
        var state = new MigrationStateDocument
        {
            Accounts =
            [
                new MigratedAccount
                {
                    SettlAccountNumber = "100001",
                    Invoices = [new InvoiceManifestItem { InvoiceId = "INV001" }]
                }
            ]
        };

        GenerateStateRunner.MergeInvoicesIntoState(state, new List<MigratedAccount>());

        Assert.Single(state.Accounts[0].Invoices);
        Assert.Equal("INV001", state.Accounts[0].Invoices[0].InvoiceId);
    }
}
