namespace DataMigration.Domain.Customers;

/// <summary>
/// A customer's contract for a single commodity. <see cref="Start"/> is when the contract
/// began (or will begin — future-dated contracts are supported). <see cref="Billing"/>
/// captures when this contract is invoiced; different contracts on the same customer
/// can have different schedules. The list of <see cref="MeteringPoints"/> includes both
/// currently-active and scheduled future assignments; historical assignments are not
/// represented.
///
/// <para><see cref="BuyerInvoicing"/> holds the contract-level fakturareferencer
/// (EAN + ordre/aftale/køber-reference). Contracts under the same (merged) customer
/// can have different values; SettlClient fans these out into one Settl payer per
/// unique non-null <c>ElectronicNumber</c>. Null when the contract has no
/// fakturareferencer configured.</para>
/// </summary>
public sealed record Contract(
    string Number,
    string Description,
    DateOnly Start,
    Commodity Commodity,
    string CustomerNumber,
    BillingSchedule Billing,
    IReadOnlyList<MeteringPointAssignment> MeteringPoints,
    string? PaymentTerm = null,
    BuyerInvoicingReferences? BuyerInvoicing = null);
