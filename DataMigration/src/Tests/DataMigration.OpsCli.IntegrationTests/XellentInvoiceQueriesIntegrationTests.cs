using DataMigration.Core;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.XellentData.Entities;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>The invoice query carries each invoice's contract, which the held-back test needs.</summary>
public sealed class XellentInvoiceQueriesIntegrationTests : IDisposable
{
    private readonly FakeXellent _xellent = new();

    public void Dispose() => _xellent.Dispose();

    private static CustInvoiceJour Row(string id, string contract, long recId) => new()
    {
        Dataareaid = MigrationConstants.DataAreaId, Invoiceid = id, Invoiceaccount = "410001",
        ExuContractnum = contract, Invoicedate = new DateTime(2025, 6, 1), Duedate = new DateTime(2025, 6, 15),
        ExuBillingcategory = 2, ExuPaymentmedia = 2, ExuCustfreeinvoicestatus = 2, Recid = recId,
    };

    [Fact]
    public async Task Each_invoice_carries_its_trimmed_contract_number_or_null()
    {
        _xellent.Add(Row("INV1", "C410001 ", 1), Row("INV2", "", 2));
        await using var context = _xellent.NewContext();

        var invoices = await XellentInvoiceQueries.QueryAsync(context, ["410001"], MigrationConstants.DataAreaId);

        Assert.Equal("C410001", invoices.Single(i => i.InvoiceId == "INV1").ContractNumber);
        Assert.Null(invoices.Single(i => i.InvoiceId == "INV2").ContractNumber);
    }
}
