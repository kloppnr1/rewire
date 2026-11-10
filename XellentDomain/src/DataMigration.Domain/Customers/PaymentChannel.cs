namespace DataMigration.Domain.Customers;

/// <summary>
/// How a customer pays invoices.
/// </summary>
public enum PaymentChannel
{
    Fik,
    Bs,
    EInvoiceFik,
    MobilePay
}
