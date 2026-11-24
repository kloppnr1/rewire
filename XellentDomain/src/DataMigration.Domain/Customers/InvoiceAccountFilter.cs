namespace DataMigration.Domain.Customers;

/// <summary>
/// Filters accounts for historical invoice export.
/// Only accounts whose most recent invoice is a Slutafregning (Final or PureFinal)
/// should have their invoices exported. Active customers (Aconto, Settlement etc.) are excluded.
/// </summary>
public static class InvoiceAccountFilter
{
    public static HashSet<string> GetEligibleAccounts(IEnumerable<Invoice> invoices)
    {
        var eligible = new HashSet<string>();
        var grouped = invoices
            .GroupBy(i => i.AccountNumber)
            .Select(g => new { Account = g.Key, MostRecent = g.OrderByDescending(i => i.InvoiceDate).First() });
        foreach (var account in grouped)
        {
            if (account.MostRecent.BillingCategory is BillingCategory.Final or BillingCategory.PureFinal)
                eligible.Add(account.Account);
        }
        return eligible;
    }

    public static DateTime GetHistoryCutoffDate(DateTime referenceDate, int historyYears = Invoice.ExportHistoryYears) =>
        new DateTime(referenceDate.Year - historyYears, 1, 1);
}
