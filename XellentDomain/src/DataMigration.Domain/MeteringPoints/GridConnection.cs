namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// Grid-connected characteristics of an electricity metering point. Present only on
/// MPs registered in DataHub (electricity); null for installation-based MPs (water,
/// heat, EV charging) where these concepts don't apply.
/// </summary>
public sealed record GridConnection(
    PriceArea PriceArea,
    string GridAreaCode,
    SettlementMethod SettlementMethod,
    MeteringPointKind Kind,
    string? AdditionalInfo);
