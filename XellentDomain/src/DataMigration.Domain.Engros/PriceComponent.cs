using DataMigration.Domain.Common;

namespace DataMigration.Domain.Engros;

/// <summary>
/// One rate component active for a period. Applied per hour as
/// <c>Rates.AtHour(time.Hour) × kWh</c> for hours falling inside <see cref="Validity"/>.
///
/// <para>The same <see cref="Code"/> may appear multiple times in a <see cref="SettlementBasis"/>
/// if the rate changed during the period — each entry has its own <see cref="Validity"/>.</para>
/// </summary>
public sealed record PriceComponent(
    PriceComponentKind Kind,
    string Code,
    string Description,
    string? OwnerGln,
    HourlyRates Rates,
    Period Validity);
