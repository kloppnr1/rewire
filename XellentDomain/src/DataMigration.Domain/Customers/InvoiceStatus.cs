namespace DataMigration.Domain.Customers;

public enum InvoiceStatus
{
    None = 0,
    Invoiced = 2,
    Credited = 3
}

public static class InvoiceStatusExtensions
{
    public static string ToLabel(this InvoiceStatus status) => status switch
    {
        InvoiceStatus.Invoiced => "Faktureret",
        InvoiceStatus.Credited => "Krediteret",
        _ => string.Empty
    };
}
