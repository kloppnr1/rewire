namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// What an electricity metering point measures. Aligned with DataHub's MeteringPointType.
/// </summary>
public enum MeteringPointKind
{
    Consumption,
    Production,
    NetProduction,
    SupplyToGrid,
    ConsumptionFromGrid,
    OwnProduction,
    ElectricalHeating,
    NetConsumption,
    OtherConsumption,
    OtherProduction,
    Effect,
    WholesaleServicesInformation
}
