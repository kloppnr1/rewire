using DataMigration.Domain.Common;

namespace DataMigration.Domain.TimeSeries;

/// <summary>
/// Raw measured time-series for a metering point. Holds the original readings exactly as
/// recorded in the source system — not aggregated, not priced. Pricing/cost reconstruction
/// happens in <c>DataMigration.Domain.Engros.SettlementBasis</c>, which references this
/// data conceptually but is built and stored separately.
///
/// <para><see cref="MeteringPointId"/> is a foreign key to
/// <c>MeteringPoint.Identification</c> in <c>DataMigration.Domain.MeteringPoints</c>.
/// <see cref="Period"/> is the interval the points cover.</para>
/// </summary>
public sealed record MeteringPointTimeSeries(
    string MeteringPointId,
    TimeResolution Resolution,
    string QuantityCode,
    Period Period,
    IReadOnlyList<TimeSeriesPoint> Points);
