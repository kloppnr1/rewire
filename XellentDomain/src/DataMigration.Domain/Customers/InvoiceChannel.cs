namespace DataMigration.Domain.Customers;

/// <summary>
/// How a customer receives invoices.
/// </summary>
public enum InvoiceChannel
{
    Pdf,
    Email,
    OioUbl,
    PbsBs,
    MobilePay
}
