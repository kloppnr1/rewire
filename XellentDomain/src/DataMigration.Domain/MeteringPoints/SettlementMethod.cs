namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// How an electricity metering point is settled in DataHub.
/// </summary>
public enum SettlementMethod
{
    Flex,
    Profiled,
    NonProfiled
}
