namespace DataMigration.SettlApi.Lookup;

/// <summary>
/// A single metering point to look up in Settl by identification. Only
/// <see cref="MpIdentification"/> is required — the optional customer context
/// (<see cref="SettlCustomerNumber"/>, <see cref="RepresentativeAccount"/>) lets a
/// caller that already knows the owning customer (e.g. a profile-driven batch) fill
/// the customer columns, while a generic caller can pass just the identification.
/// </summary>
public sealed record SettlMpLookupRequest(
    string MpIdentification,
    string? SettlCustomerNumber = null,
    string? RepresentativeAccount = null);

/// <summary>
/// Essential customer + metering-point properties projected from Settl. Presentation-free —
/// consumers serialize it (JSON for web, CSV for ops) as needed.
/// </summary>
public sealed record SettlMeteringPointView
{
    // Customer context
    public string? RepresentativeAccount { get; init; }
    public string? SettlCustomerNumber { get; init; }
    public Guid? SettlCustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? Cvr { get; init; }
    public string? CustomerType { get; init; }
    public string? CustomerPostalCode { get; init; }
    public string? CustomerCity { get; init; }
    public string? CustomerEmail { get; init; }

    // Metering point
    public string MpIdentification { get; init; } = "";
    public Guid? SettlMeteringPointId { get; init; }
    public bool FoundInSettl { get; init; }
    public string? MeteringPointType { get; init; }
    public string? MeteringPointSubType { get; init; }
    public string? GridMarketBalanceArea { get; init; }
    public string? GridArea { get; init; }
    public DateTime? DeliveryStart { get; init; }
    public bool IsChild { get; init; }
    public string? Currency { get; init; }
    public string? JointInvoiceNumber { get; init; }
    public string? ElectronicNumber { get; init; }

    // Full billing/payment configuration of the metering point (current entries). The flat
    // Currency/JointInvoiceNumber/ElectronicNumber above are kept for CSV/back-compat; these
    // expose the complete config sections for the detail view.
    public SettlMpBillingConfigView? BillingConfiguration { get; init; }
    public SettlMpPaymentConfigView? PaymentConfiguration { get; init; }

    // Payers linked to this metering point (resolved via the payer↔MP relation)
    public IReadOnlyList<SettlPayerView> Payers { get; init; } = [];
}

/// <summary>Billing-configuration section of a metering point (current entry).</summary>
public sealed record SettlMpBillingConfigView
{
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public string? LifeTime { get; init; }
    public bool JointInvoice { get; init; }
    public string? JointInvoiceNumber { get; init; }
    public string? Currency { get; init; }
    public bool NextInvoiceManual { get; init; }
    public bool AutomaticApprovalOfDraft { get; init; }
}

/// <summary>Payment-configuration section of a metering point (current entry).</summary>
public sealed record SettlMpPaymentConfigView
{
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public string? LifeTime { get; init; }
    public string? BillingType { get; init; }
    public string? ElectronicNumber { get; init; }
    public string? InvoiceCcEmail { get; init; }
    public string? InvoiceCcElectronicNumber { get; init; }
    public string? OrderNumber { get; init; }
    public string? AgreementIdentifier { get; init; }
    public string? OrderIdentifier { get; init; }
    public string? BookkeepingReference { get; init; }
    public string? BuyerReference { get; init; }
}

/// <summary>A payer associated with a metering point (essential billing-identity fields).</summary>
public sealed record SettlPayerView
{
    public string? PayerIdentification { get; init; }
    public bool IsPrimaryCustomerPayer { get; init; }
    public string? Name { get; init; }
    public string? ElectronicNumber { get; init; }
    public string? InvoiceChannel { get; init; }
    public string? PaymentChannel { get; init; }
    public string? Email { get; init; }
}
