namespace DataMigration.Domain.Customers;

/// <summary>
/// Billing-channel state resolved from Xellent for a customer. Used by PayerMapper to
/// derive per-payer <see cref="InvoiceChannel"/> and <see cref="PaymentChannel"/>:
/// MobilePay trumps PBS, which trumps EAN, which trumps email, which falls back to PBS_BS/FIK.
///
/// <para><see cref="DefaultEan"/> is the customer-level EAN that becomes the default
/// (primary) payer's electronic number. Extra payers are keyed by contract-level EANs
/// that differ from this value.</para>
/// </summary>
public sealed record CustomerBilling(
    string? DefaultEan,
    PbsAgreement? Pbs,
    MobilePaySubscription? MobilePay);
