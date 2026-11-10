namespace DataMigration.Domain.Engros;

/// <summary>
/// Hourly consumption with the Nord Pool spot reference and the rate the consumer
/// actually paid for energy this hour. <see cref="EnergyRate"/> is the calculated price
/// — spot + handelsomkostninger + supplier-margin baked together — and varies per concrete
/// date-hour (so it lives on <see cref="ConsumptionHour"/> rather than as a
/// <see cref="PriceComponent"/> with <see cref="HourlyRates"/>, which models clock-hour-of-day
/// variation, not date-hour variation).
/// </summary>
public sealed record ConsumptionHour(
    DateTime Hour,
    decimal Kwh,
    decimal SpotPrice,
    decimal EnergyRate);
