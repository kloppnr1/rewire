using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Engros;

/// <summary>
/// A subscription or fixed fee charged per billing period (not per kWh). Does NOT
/// participate in <see cref="SettlementBasis"/> — the settlement basis is per-kWh
/// only. Modeled in <c>Domain.Engros</c> so consumers showing the full invoicing
/// picture (basis + subscriptions) find subscription info in the same place as
/// the per-kWh rate components.
///
/// <para>Conceptually attached to a metering point or contract; the consumer
/// queries them per <c>(MeteringPointId, Period)</c> or per contract depending
/// on use-case.</para>
///
/// <para><see cref="BillingFrequency"/> here means *how often this specific
/// subscription is charged* — distinct from
/// <c>Customer.Billing.Frequency</c> which means *how often the customer is
/// billed overall*. Both reuse the same enum from <c>Domain.Customers</c>.</para>
/// </summary>
public sealed record SubscriptionFee(
    string Code,
    string Description,
    string? OwnerGln,
    decimal Amount,
    BillingFrequency BillingFrequency,
    Period Validity);
