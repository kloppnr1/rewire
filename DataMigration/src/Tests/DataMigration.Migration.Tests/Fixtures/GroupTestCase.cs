namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Expected metering point data from Settl production (GET /api/v3/meteringPoints/{id}).
/// </summary>
public record ExpectedMeteringPoint(
    string MeteringPointIdentification,
    string InstallationNumber,
    // InstallationAddress
    string Street,
    string BuildingNumber,
    string PostalCode,
    string City,
    // Consumer
    string ConsumerType,
    string ConsumerName,
    string? ConsumerCvr,
    // MeterCharacteristics (null if no active meter)
    string? MeterIdentifier,
    string? MeterType,
    // Grid
    string? GridArea,
    // Settlement
    string SettlementMethod,
    // ConnectionInfo
    string ConnectionStatus);

public record GroupTestCase(
    string RepresentativeAccount,
    string GroupName,
    string[] MemberAccounts,
    // Forventet customer i Settl (GET /api/v2/customers/{id})
    string ExpectedCustomerName,
    string ExpectedCustomerType,
    string? ExpectedCvr,
    string ExpectedStreet,
    string ExpectedBuildingNumber,
    string ExpectedPostalCode,
    string ExpectedCity,
    string ExpectedEmail,
    // Forventet payer i Settl (GET /api/v1/Payers/{id})
    string ExpectedPayerId,
    string ExpectedPayerName,
    string ExpectedPayerStreet,
    string ExpectedPayerBuilding,
    string ExpectedPayerPostalCode,
    string ExpectedPayerCity,
    string ExpectedPayerType,
    string ExpectedPayerEmail,
    string ExpectedInvoiceChannel,
    string ExpectedPaymentChannel,
    string? ExpectedElectronicNumber,
    // Forventet contract i Settl
    string ExpectedContractName,
    // Forventede parent metering points i Settl (child MPs ignoreres)
    ExpectedMeteringPoint[] ExpectedMeteringPoints,
    // Kendte manuelle ændringer i Settl efter migration
    ExpectedDiff[]? KnownDiffs = null);
