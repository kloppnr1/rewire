namespace DataMigration.Domain.Customers;

public sealed record Invoice(
    string InvoiceId,
    string AccountNumber,
    DateTime InvoiceDate,
    DateTime DueDate,
    string DeliveryAddress,
    string InvoicingAddress,
    BillingCategory BillingCategory,
    PaymentMedia PaymentMedia,
    InvoiceStatus InvoiceStatus,
    decimal InvoiceAmount,
    decimal SumTax)
{
    public const int ExportHistoryYears = 5;
}
