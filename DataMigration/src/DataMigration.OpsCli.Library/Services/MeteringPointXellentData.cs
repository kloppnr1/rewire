namespace DataMigration.OpsCli.Services;

public sealed record MeteringPointXellentData(
    string? MeteringPointType,
    string? MeteringPointSubType,
    string? MeterReadingOccurrence,
    double? EstimatedAnnualVolumeKwh,
    IReadOnlyList<XellentMeter> Meters,
    string? JointInvoiceGroupId,
    string GridMarketBalanceArea,
    string GridAreaCode);

public sealed record XellentMeter(string? Id, double MultiplicationFactor);
