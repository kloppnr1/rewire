using System.Text.Json.Serialization;

namespace DataMigration.OpsCli.Data;

public class MigrationStateDocument
{
    public List<MigratedAccount> Accounts { get; set; } = new();
}

public class MigratedAccount
{
    public string SettlAccountNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public List<string> DeliveryCategories { get; set; } = new();
    public List<string> SourceAccounts { get; set; } = new();
    public string GroupKey { get; set; } = string.Empty;
    /// <summary>
    /// Change-of-supplier date for this customer's earliest migrated metering point. Populated only
    /// by <c>generate-state-from-settl</c> (from <c>cosByCustomer</c>); the deprecated <c>generate-state</c>
    /// path has no COS data and leaves this at <c>default</c>.
    /// </summary>
    public DateOnly ChangeOfSupplierDate { get; set; }
    public List<InvoiceManifestItem> Invoices { get; set; } = new();
}

public class InvoiceManifestItem
{
    public string InvoiceId { get; set; } = string.Empty;
    public string SourceAccount { get; set; } = string.Empty;
    public string? PdfFileName { get; set; }
    [JsonIgnore]
    public string? PdfSourcePath { get; set; }
    public string BillingCategory { get; set; } = string.Empty;
    public string PaymentMedia { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }
    public string DeliveryAddress { get; set; } = string.Empty;
    public string InvoicingAddress { get; set; } = string.Empty;
    public string InvoiceStatus { get; set; } = string.Empty;
    public decimal InvoiceAmount { get; set; }
    public decimal SumTax { get; set; }
}
