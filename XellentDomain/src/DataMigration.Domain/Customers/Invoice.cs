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

    /// <summary>Trimmed Xellent EXU_CONTRACTNUM: the contract the invoice was billed on. Null when
    /// the invoice has none, or when it was read from data saved before the field existed.</summary>
    public string? ContractNumber { get; init; }
}
