using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.Tests.Runners;

public class GenerateStateFromSettlNoInvoicesTests
{
    [Fact]
    public void No_invoices_keeps_source_accounts_and_only_sets_customer_cos()
    {
        var account = new MigratedAccount
        {
            SettlAccountNumber = "952461",
            SourceAccounts = ["101253", "101254"],
            Invoices = []
        };
        var state = new MigrationStateDocument { Accounts = [account] };
        var cos = new DateOnly(2026, 6, 1);

        GenerateStateFromSettlRunner.PopulateNoInvoiceDates(
            state, new Dictionary<string, DateOnly> { ["952461"] = cos });

        Assert.Equal(cos, account.ChangeOfSupplierDate);
        Assert.Equal(["101253", "101254"], account.SourceAccounts);
        Assert.Empty(account.Invoices);
    }
}
