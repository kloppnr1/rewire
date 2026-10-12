namespace DataMigration.Domain.Customers;

/// <summary>
/// One billing party under a <see cref="Customer"/>. A customer always has at least one
/// payer (the primary / default), and may have additional payers when contracts carry
/// distinct e-invoice EANs — each unique non-empty contract-level EAN that differs from
/// the customer-level EAN becomes its own extra payer.
///
/// <para><see cref="Identifier"/> is the string written as PayerIdentifier on the Settl
/// payer DTO: <c>AccountNumber</c> for single-payer accounts, and
/// <c>{AccountNumber}-{n}</c> for fan-out accounts. <see cref="ContractNumbers"/> lists
/// the contracts routed to this payer. <see cref="InvoiceChannel"/> and
/// <see cref="PaymentChannel"/> are computed per-payer by PayerMapper from the payer's
/// own EAN and the customer's PBS/MobilePay state.</para>
/// </summary>
public sealed record Payer(
    string Identifier,
    bool IsPrimary,
    IReadOnlyList<string> ContractNumbers,
    BuyerInvoicingReferences? BuyerInvoicing,
    InvoiceChannel InvoiceChannel,
    PaymentChannel PaymentChannel);
