using System.Text.Json.Serialization;

namespace DataMigration.SettlApi.Models;

// === Response models ===

public class ContractInfoDto
{
    public Guid Id { get; set; }
    public string? ContractName { get; set; }
    public string? ContractCategory { get; set; }
    public string? OrderNumber { get; set; }
    public string? Description { get; set; }
    public string? CommodityType { get; set; }
    public string? CountryCode { get; set; }
    public TimePeriodDto? ContractPeriod { get; set; }
}

public class CustomerContractDto
{
    public Guid ContractId { get; set; }
    public Guid CustomerId { get; set; }
    public string? ContractName { get; set; }
    public string? ContractDescription { get; set; }
    public DateTimeOffset ContractStart { get; set; }
    public DateTimeOffset? ContractEnd { get; set; }
    public string? CommodityType { get; set; }
}

public class ContractMeteringPointInfoDto
{
    public Guid Id { get; set; }
    public Guid MeteringPointId { get; set; }
    public string? InstallationNumber { get; set; }
    public string? MeteringPointIdentification { get; set; }
    public string? Category { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
}

/// <summary>
/// En kundes kontrakt med dens linkede målepunkter — hentes i ÉT kald via
/// GET /api/v1/contracts/customer/{customerId}/metering-point-contracts.
/// Undgår at scanne alle kontrakter globalt for at finde en kundes kontrakter.
/// </summary>
public class CustomerContractWithMeteringPointsDto
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string? ContractName { get; set; }
    public string? ContractDescription { get; set; }
    public DateTimeOffset ContractStart { get; set; }
    public DateTimeOffset ContractEnd { get; set; }
    public List<CustomerContractMeteringPointDto> MeteringPoints { get; set; } = [];
}

public class CustomerContractMeteringPointDto
{
    public Guid MeteringPointId { get; set; }
    public string? MeteringPointIdentification { get; set; }
    public string? CommodityType { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
}

/// <summary>
/// En kontraktlinje (pris-element) på en kontrakt — Settls modellering af en
/// kontrakts prisstruktur. Hentes via GET /api/v1/Contracts/{contractId}/ContractLines.
/// Pris-/mængde-felterne (<see cref="UnitPrice"/>, <see cref="FixedNumber"/>, Coverage osv.)
/// leveres af samme list-endpoint — de er linjens faktiske opsætning.
/// </summary>
public class ContractLineInfoDto
{
    public Guid Id { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string? ProductElementCode { get; set; }
    public string? ProductName { get; set; }
    public Guid PriceListId { get; set; }
    public Guid MarketPriceListId { get; set; }
    public Guid MarketBalanceAreaId { get; set; }
    public string? Category { get; set; }

    /// <summary>Linjens stykpris med valuta + mængde-enhed/-art (fx 10,00 DKK pr. Month).</summary>
    public PriceUnit? UnitPrice { get; set; }
    public double? MinimumPrice { get; set; }
    public double? MaximumPrice { get; set; }
    public double Coverage { get; set; }
    public string? Text { get; set; }
    /// <summary>Fast antal (fx 1,00 PCS) — bruges af abonnement-lignende linjer.</summary>
    public Quantity? FixedNumber { get; set; }
    public double VolumeFlex { get; set; }
    public double ProfitSplit { get; set; }
    public double VolumeFlexibility { get; set; }
    /// <summary>Billing-reporting-type som rå streng (enum on the wire).</summary>
    public string? ReportingType { get; set; }
}

/// <summary>Stykpris fra Settl: <see cref="Value"/> i <see cref="CurrencyUnit"/> pr.
/// <see cref="QuantityUnit"/>/<see cref="QuantityKind"/>. Enums leveres som strenge.</summary>
public class PriceUnit
{
    public string? QuantityUnit { get; set; }
    public string? QuantityKind { get; set; }
    public string? CurrencyUnit { get; set; }
    public double Value { get; set; }
}

/// <summary>En mængde fra Settl: <see cref="Value"/> i <see cref="Unit"/>/<see cref="Kind"/>
/// (fx 1,00 PCS). Enums leveres som strenge.</summary>
public class Quantity
{
    public string? Unit { get; set; }
    public string? Kind { get; set; }
    public double Value { get; set; }
}

// === Request models ===

public class AddContractMeteringPointsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<ContractMeteringPointDto> ContractMeteringPoints { get; set; } = [];
}

public class ContractMeteringPointDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ExternalIdentifier { get; set; }
    public Guid MeteringPointId { get; set; }
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    [JsonConverter(typeof(NullableDateOnlyPlusOneConverter))]
    public DateOnly? End { get; set; }
    public string? InstallationNumber { get; set; }
    public string? MeteringPointIdentification { get; set; }
    public string? Category { get; set; }
}
