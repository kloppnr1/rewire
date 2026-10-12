using DataMigration.Domain.Customers;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent;

public static class XellentInvoiceQueries
{
    /// <summary>
    /// Queries CUSTINVOICEJOUR for the given accounts and returns domain Invoice records.
    /// Results are not filtered — apply InvoiceAccountFilter separately.
    /// Large account arrays are split into chunks to stay within SQL Server's parameter limit.
    /// </summary>
    public static async Task<List<Invoice>> QueryAsync(
        XellentDbContext context,
        string[] accountNumbers,
        string dataAreaId,
        CancellationToken ct = default)
    {
        var result = new List<Invoice>();
        if (accountNumbers.Length == 0) return result;

        foreach (var chunk in accountNumbers.Chunk(2000))
        {
            var rows = await context.CustInvoiceJours
                .Where(i => i.Dataareaid == dataAreaId && chunk.Contains(i.Invoiceaccount))
                .Select(i => new
                {
                    i.Invoiceid,
                    i.Invoiceaccount,
                    i.Invoicedate,
                    i.Duedate,
                    i.Deliveryaddress,
                    i.Invoicingaddress,
                    i.ExuBillingcategory,
                    i.ExuPaymentmedia,
                    i.ExuCustfreeinvoicestatus,
                    i.Invoiceamountmst,
                    i.Sumtax
                })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                result.Add(new Invoice(
                    InvoiceId: row.Invoiceid.Trim(),
                    AccountNumber: row.Invoiceaccount.Trim(),
                    InvoiceDate: row.Invoicedate,
                    DueDate: row.Duedate,
                    DeliveryAddress: row.Deliveryaddress?.Trim() ?? string.Empty,
                    InvoicingAddress: row.Invoicingaddress?.Trim() ?? string.Empty,
                    BillingCategory: (BillingCategory)row.ExuBillingcategory,
                    PaymentMedia: (PaymentMedia)row.ExuPaymentmedia,
                    InvoiceStatus: (InvoiceStatus)row.ExuCustfreeinvoicestatus,
                    InvoiceAmount: row.Invoiceamountmst,
                    SumTax: row.Sumtax));
            }
        }

        return result;
    }
}
