using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class InvoiceStateComparerTests
{
    [Fact]
    public void Reports_source_and_invoice_moves_additions_removals_and_customer_changes()
    {
        var before = State(
            Account("A", new DateOnly(2026, 5, 1), ["100", "200"], [("100", "INV1"), ("200", "INV2")]),
            Account("B", new DateOnly(2026, 6, 1), [], []),
            Account("OLD", new DateOnly(2026, 1, 1), [], []));
        var after = State(
            Account("A", new DateOnly(2026, 5, 2), [], []),
            Account("B", new DateOnly(2026, 6, 1), ["100", "300"], [("100", "INV1"), ("300", "INV3")]),
            Account("NEW", new DateOnly(2026, 7, 1), [], []));

        var differences = InvoiceStateComparer.Compare(before, after);

        Assert.Contains(differences, d => d.Kind == "Customer" && d.Change == "Added" && d.SettlCustomer == "NEW");
        Assert.Contains(differences, d => d.Kind == "Customer" && d.Change == "Removed" && d.SettlCustomer == "OLD");
        Assert.Contains(differences, d => d.Kind == "Customer" && d.Change == "CosChanged"
            && d.SettlCustomer == "A" && d.Before == "2026-05-01" && d.After == "2026-05-02");
        Assert.Contains(differences, d => d.Kind == "SourceAccount" && d.Change == "Moved"
            && d.SourceAccount == "100" && d.Before == "A" && d.After == "B");
        Assert.Contains(differences, d => d.Kind == "Invoice" && d.Change == "Moved"
            && d.SourceAccount == "100" && d.InvoiceId == "INV1" && d.Before == "A" && d.After == "B");
        Assert.Contains(differences, d => d.Kind == "SourceAccount" && d.Change == "Removed" && d.SourceAccount == "200");
        Assert.Contains(differences, d => d.Kind == "Invoice" && d.Change == "Removed" && d.InvoiceId == "INV2");
        Assert.Contains(differences, d => d.Kind == "SourceAccount" && d.Change == "Added" && d.SourceAccount == "300");
        Assert.Contains(differences, d => d.Kind == "Invoice" && d.Change == "Added" && d.InvoiceId == "INV3");
    }

    [Fact]
    public void Duplicate_invoice_ownership_is_visible_even_if_both_states_have_it()
    {
        var duplicate = State(
            Account("A", default, ["100"], [("100", "INV1")]),
            Account("B", default, ["100"], [("100", "INV1")]));

        var differences = InvoiceStateComparer.Compare(duplicate, duplicate);

        Assert.Contains(differences, d => d.Kind == "SourceAccount"
            && d.Change == "AmbiguousOwnership" && d.Before == "A|B" && d.After == "A|B");
        Assert.Contains(differences, d => d.Kind == "Invoice"
            && d.Change == "AmbiguousOwnership" && d.Before == "A|B" && d.After == "A|B");
    }

    [Fact]
    public void Unchanged_states_have_only_the_csv_header()
    {
        var state = State(Account("A", new DateOnly(2026, 5, 1), ["100"], [("100", "INV1")]));

        var differences = InvoiceStateComparer.Compare(state, state);

        Assert.Empty(differences);
        Assert.Equal("Kind;Change;SettlCustomer;SourceAccount;InvoiceId;Before;After" + Environment.NewLine,
            InvoiceStateComparer.ToCsv(differences));
    }

    private static MigrationStateDocument State(params MigratedAccount[] accounts) => new() { Accounts = [.. accounts] };

    private static MigratedAccount Account(
        string number,
        DateOnly cos,
        string[] sources,
        (string Source, string Id)[] invoices) => new()
    {
        SettlAccountNumber = number,
        ChangeOfSupplierDate = cos,
        SourceAccounts = [.. sources],
        Invoices = [.. invoices.Select(i => new InvoiceManifestItem
        {
            SourceAccount = i.Source,
            InvoiceId = i.Id
        })]
    };
}
