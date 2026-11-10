using DataMigration.Domain.Common;

namespace DataMigration.Domain.Engros;

/// <summary>
/// Settlement basis (afregningsgrundlag) for a metering point's consumption in a period.
/// Bundles the hourly consumption + spot reference and the rate components active in the
/// period. Consumer reconstructs hour-by-hour cost by summing each <see cref="PriceComponent"/>
/// whose <c>Validity</c> covers the hour, multiplied by the hour's consumption.
///
/// <para><see cref="MeteringPointId"/> is a foreign key to <c>MeteringPoint.Identification</c>
/// in <c>DataMigration.Domain.MeteringPoints</c>.</para>
/// </summary>
public sealed record SettlementBasis(
    string MeteringPointId,
    Period Period,
    IReadOnlyList<ConsumptionHour> Hours,
    IReadOnlyList<PriceComponent> Components);
