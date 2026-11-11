using System.Text.Json.Serialization;

namespace DataMigration.SettlApi.Models;

// === Request models (v3 metering point API — PascalCase, nested structure) ===

public class UpdateMeteringPointWithHistoricalDataRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public MeteringPointDto? MeteringPoint { get; set; }
}

public class CreateMeteringPointsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<MeteringPointDto> MeteringPoints { get; set; } = [];
}

/// <summary>
/// Body for POST /api/v4/meteringPoints — the electricity create endpoint as of 2026-08.
/// The v3 create route (<see cref="CreateMeteringPointsRequest"/>) is gone: it answers 404.
/// Verified against featuretest 2026-08-12 (v4 POST returns 201 on an empty list).
/// <para>Note the list property name: Settl calls it <c>MeteringPointCreateObjectDtos</c>,
/// not <c>MeteringPoints</c>.</para>
/// </summary>
public class CreateMeteringPointsV4Request
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<MeteringPointCreateObjectV4Dto> MeteringPointCreateObjectDtos { get; set; } = [];
}

/// <summary>
/// One metering point in a v4 create. Section-for-section the same payload as
/// <see cref="MeteringPointDto"/> — the very same section DTOs are reused — with two deltas:
/// <c>LastUpdated</c> is gone (we never sent it) and <c>InstallationAddress</c> is renamed to
/// <c>InstallationAddressCreateObject</c> and carries the DAR flags.
/// <para>Build it with <see cref="FromMeteringPoint"/> so <c>MeteringPointTranslator</c> stays
/// the single place that maps domain → Settl; this type only reshapes for the wire.</para>
/// </summary>
public class MeteringPointCreateObjectV4Dto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CountryCode { get; set; } = "DK";
    public MeteringPointIdentifiersDto? Identification { get; set; }
    public MeteringPointInstallationAddressCreateObjectV4Dto? InstallationAddressCreateObject { get; set; }
    public MeteringPointConsumerDto? Consumer { get; set; }
    public MeteringPointMeterCharacteristicsDto? MeterCharacteristics { get; set; }
    public MeteringPointGridDto? Grid { get; set; }
    public MeteringPointSettlementMethodDto? SettlementMethod { get; set; }
    public MeteringPointConnectionInfoDto? ConnectionInfo { get; set; }
    public MeteringPointDeliveryStatusDto? DeliveryStatus { get; set; }
    public string? JointInvoiceNumber { get; set; }

    /// <summary>
    /// Reshapes a translator-built <see cref="MeteringPointDto"/> into the v4 create shape.
    /// The DAR flags are per-run settings, not per-MP data, so they are passed in by the caller.
    /// </summary>
    public static MeteringPointCreateObjectV4Dto FromMeteringPoint(
        MeteringPointDto dto, bool enrichAddress, bool skipAddressValidation) => new()
        {
            Id = dto.Id,
            CountryCode = dto.CountryCode,
            Identification = dto.Identification,
            InstallationAddressCreateObject = dto.InstallationAddress == null
                ? null
                : new MeteringPointInstallationAddressCreateObjectV4Dto
                {
                    Start = dto.InstallationAddress.Start,
                    Address = dto.InstallationAddress.Address,
                    EnrichAddress = enrichAddress,
                    SkipAddressValidation = skipAddressValidation
                },
            Consumer = dto.Consumer,
            MeterCharacteristics = dto.MeterCharacteristics,
            Grid = dto.Grid,
            SettlementMethod = dto.SettlementMethod,
            ConnectionInfo = dto.ConnectionInfo,
            DeliveryStatus = dto.DeliveryStatus,
            JointInvoiceNumber = dto.JointInvoiceNumber
        };
}

public class MeteringPointDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CountryCode { get; set; } = "DK";
    public MeteringPointIdentifiersDto? Identification { get; set; }
    public MeteringPointInstallationAddressDto? InstallationAddress { get; set; }
    public MeteringPointConsumerDto? Consumer { get; set; }
    public MeteringPointMeterCharacteristicsDto? MeterCharacteristics { get; set; }
    public MeteringPointGridDto? Grid { get; set; }
    public MeteringPointSettlementMethodDto? SettlementMethod { get; set; }
    public MeteringPointConnectionInfoDto? ConnectionInfo { get; set; }
    public MeteringPointDeliveryStatusDto? DeliveryStatus { get; set; }
    public string? JointInvoiceNumber { get; set; }

    /// <summary>Excel-only field — not sent to the API.</summary>
    [JsonIgnore]
    public string? AdditionalInfo1 { get; set; }
}

public class MeteringPointIdentifiersDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? MeteringPointIdentification { get; set; }
    public string? InstallationNumber { get; set; }
}

public class MeteringPointInstallationAddressDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public PhysicalAddressDto? Address { get; set; }
}

/// <summary>
/// Installation-address section of a v4 create. Same Start+Address as the v3 section, plus the
/// two DAR switches Settl added with address validation:
/// <c>EnrichAddress</c> lets Settl fill in municipality code, street code and DAR identifier when
/// they can be derived from the other address fields; <c>SkipAddressValidation</c> allows the
/// create even when no DAR entry matches the address exactly. Validation is on in featuretest only.
/// </summary>
public class MeteringPointInstallationAddressCreateObjectV4Dto : MeteringPointInstallationAddressDto
{
    public bool EnrichAddress { get; set; } = true;
    public bool SkipAddressValidation { get; set; }
}

public class MeteringPointConsumerDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? CustomerType { get; set; }
    public MeteringPointConsumerPartyDto? FirstConsumerParty { get; set; }
    public MeteringPointConsumerPartyDto? SecondConsumerParty { get; set; }
    public List<MeteringPointContactPointDto>? ContactPoints { get; set; }
}

public class MeteringPointCommunicationLineDto
{
    public string? Type { get; set; }
    public string? Value { get; set; }
    public string? Description { get; set; }
}

public class MeteringPointContactPointDto
{
    public MeteringPointContactPointCategoryDto? Category { get; set; }
    public MeteringPointContactPointAddressDto? Address { get; set; }
    public List<MeteringPointCommunicationLineDto>? CommunicationLines { get; set; }
}

public class MeteringPointContactPointCategoryDto
{
    public string? Category { get; set; }
}

public class MeteringPointContactPointAddressDto
{
    public PostalAddressDto? PostalAddress { get; set; }
}

public class MeteringPointConsumerPartyDto
{
    public ConsumerNameDto? Name { get; set; }
    public string? VatIdentificationNumber { get; set; }
    public string? NationalIdentificationNumber { get; set; }
}

public class MeteringPointMeterCharacteristicsDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    [JsonConverter(typeof(NullableDateOnlyUtcConverter))]
    public DateOnly? End { get; set; }
    public string? MeteringPointType { get; set; }
    public string? MeteringPointSubType { get; set; }
    public QuantityDto? EstimatedAnnualVolume { get; set; }
    public List<MeteringPointMeterDto> Meters { get; set; } = [];
}

public class MeteringPointMeterDto
{
    public string? MeterIdentifier { get; set; }
    public string? MeterType { get; set; }
    public int DigitsOfPrecision { get; set; }
    public double? MultiplicationFactor { get; set; }
    public int Decimals { get; set; }
    /// <summary>Settl's market API sends Ampere as text (Market-Internal OpenAPI 2026-06-15). Null is left out of a create body.</summary>
    public string? Ampere { get; set; }
    public string? MeterReadingOccurrence { get; set; }
    public string? ProductionType { get; set; }
}

public class MeteringPointGridDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string LifeTime { get; set; } = "UntilNextHistoricalEntry";
    public Guid? MarketBalanceAreaId { get; set; }
    public Guid? MeteringGridAreaId { get; set; }

    // Not serialized — used internally to resolve UUIDs before sending
    [JsonIgnore] public string? MarketBalanceAreaName { get; set; }
    [JsonIgnore] public string? GridAreaCode { get; set; }
}

public class MeteringPointSettlementMethodDto
{
    public string? SettlementMethod { get; set; }
}

public class MeteringPointConnectionInfoDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? ConnectionStatus { get; set; }
}

public class MeteringPointDeliveryStatusDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public bool InDelivery { get; set; }
}

// === Shared period model (used by contract models) ===

public class TimePeriodDto
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
}

public class GetDeliveryStatusesResponse
{
    public Guid Id { get; set; }
    public string? ExternalIdentifier { get; set; }
    public string? Uri { get; set; }
    public List<RelLinkDto>? Links { get; set; }
    public List<DeliveryStatusDto>? DeliveryStatuses { get; set; }
}

public class RelLinkDto
{
    public string? Relation { get; set; }
    public string? Uri { get; set; }
}

public class DeliveryStatusDto
{
    public bool InDelivery { get; set; }
    public TimePeriodDto? Period { get; set; }
    public DeliveryStartReason StartReason { get; set; }
    public DeliveryStopReason StopReason { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryStartReason
{
    Na,
    Unknown,
    MoveIn,
    ChangeOfSupplier
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryStopReason
{
    Na,
    Unknown,
    MoveOut,
    ChangeOfSupplier
}

// === Response models ===

public class MeteringPointInfoDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public string MeteringPointIdentification { get; set; } = string.Empty;
    public string? InstallationNo { get; set; }
    public string? CountryCode { get; set; }
    public string? SettlementMethodCode { get; set; }
    public string? ConnectionStatus { get; set; }
    public string? MeteringPointType { get; set; }
    public string? MeteringPointSubType { get; set; }
    public string? MeteringPointRelationType { get; set; }
    public string? FuelType { get; set; }
    public DateTime? LastUpdated { get; set; }
    public bool? InDelivery { get; set; }
    public bool? RelatesToProduction { get; set; }
    public string? ExternalIdentifier { get; set; }
    public string? AdditionalInfo1 { get; set; }
    public string? AdditionalInfo2 { get; set; }
    public string? PlantName { get; set; }
    public Guid? MarketBalanceAreaId { get; set; }
    public Guid? MeteringGridAreaId { get; set; }
    public QuantityDto? EstimatedAnnualVolume { get; set; }
    public PhysicalAddressDto? InstallationAddress { get; set; }
    public ResLinkDto? ParentMeteringPoint { get; set; }
    public List<MeteringPointMeterInfoDto>? Meters { get; set; }
    public TimePeriodDto? CurrentOrNextDeliveryPeriod { get; set; }
    public TimePeriodDto? MostRecentCompletedDeliveryPeriod { get; set; }
}

public class ResLinkDto
{
    public Guid? Id { get; set; }
    public string? Uri { get; set; }
}

public class MeteringPointMeterInfoDto
{
    public Guid? Id { get; set; }
    public string? MeterIdentification { get; set; }
}

/// <summary>Current effective start dates per section, as returned by GET /api/v3/meteringPoints/{id}.</summary>
public class MeteringPointCurrentDto
{
    public Guid Id { get; set; }
    public MeteringPointCurrentIdentificationDto? Identification { get; set; }
    public MeteringPointCurrentSectionDto? Consumer { get; set; }
    public MeteringPointCurrentSectionDto? InstallationAddress { get; set; }
    public MeteringPointCurrentMeterCharacteristicsDto? MeterCharacteristics { get; set; }
    public MeteringPointCurrentSectionDto? ConnectionInfo { get; set; }
    public MeteringPointCurrentSectionDto? DeliveryStatus { get; set; }
    /// <summary>Current Grid state. Settl returns MarketBalanceAreaId + MeteringGridAreaId here.</summary>
    public MeteringPointGridDto? Grid { get; set; }
    public List<MeteringPointCurrentGridDto>? HistoricalGrids { get; set; }
    public MeteringPointRelationDto? Relation { get; set; }
}

public class MeteringPointRelationDto
{
    public Guid? ParentMeteringPointId { get; set; }
}

public class MeteringPointCurrentIdentificationDto
{
    public string? MeteringPointIdentification { get; set; }
}

public class MeteringPointCurrentGridDto
{
    public DateTime? Start { get; set; }
    public Guid? MarketBalanceAreaId { get; set; }
    public Guid? MeteringGridAreaId { get; set; }
}

public class MeteringPointCurrentSectionDto
{
    public DateTime? Start { get; set; }
}

// === Market v3 history ===

/// <summary>
/// Response from GET /api/v3/meteringPoints/{id}/history (market v3).
/// Each <c>*Dates</c> list contains the version-boundary Start dates for that section.
/// Fetch a specific version of a section with GET /api/v3/meteringPoints/{id}?EffectiveDate={date}.
/// <para><c>ChangeDates</c> is the union of all section dates.</para>
/// </summary>
public class MeteringPointV3HistoryResponse
{
    public List<DateTime>? ConsumerDates { get; set; }
    public List<DateTime>? IdentificationDates { get; set; }
    public List<DateTime>? InstallationAddressDates { get; set; }
    public List<DateTime>? DistributionSystemOperatorDates { get; set; }
    public List<DateTime>? RenewableDates { get; set; }
    public List<DateTime>? MeterCharacteristicsDates { get; set; }
    public List<DateTime>? SettlementMethodDates { get; set; }
    public List<DateTime>? GridDates { get; set; }
    public List<DateTime>? StatusInfoDates { get; set; }
    public List<DateTime>? ConnectionInfoDates { get; set; }
    public List<DateTime>? DeliveryStatusDates { get; set; }
    public List<DateTime>? MiscellaneousDates { get; set; }
    public List<DateTime>? NotesDates { get; set; }
    public List<DateTime>? RelationDates { get; set; }
    public List<DateTime>? TaxationDates { get; set; }
    public List<DateTime>? EquipmentDates { get; set; }
    public List<DateTime>? BalanceResponsibleDates { get; set; }
    public List<DateTime>? BalanceDates { get; set; }
    public List<DateTime>? RegisterConfigurationDates { get; set; }
    /// <summary>Union of all section dates — the full MP change timeline.</summary>
    public List<DateTime>? ChangeDates { get; set; }
}

// === Market v4 full-detail models ===

/// <summary>
/// Base for every section in the market v4 response. Each section is independently versioned:
/// <c>Start</c> is the version boundary (a PUT with a different Start creates a new timeline entry),
/// <c>End</c> is set by Settl when the next version begins, <c>LifeTime</c> is always
/// <c>"UntilNextHistoricalEntry"</c> for non-terminal entries.
/// </summary>
public class MeteringPointV4SectionDto
{
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public string? LifeTime { get; set; }
    public bool MarketUpdate { get; set; }
}

public class MeteringPointV4ConsumerPartyDto
{
    public MeteringPointV4NameDto? Name { get; set; }
    public string? VatIdentificationNumber { get; set; }
    /// <summary>CPR of the consumer party, as returned by GET /api/v4/meteringPoints/{id}.</summary>
    public string? NationalIdentificationNumber { get; set; }
}

public class MeteringPointV4NameDto
{
    public string? FullName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

public class MeteringPointV4ConsumerDto : MeteringPointV4SectionDto
{
    public string? CustomerType { get; set; }
    public string? Language { get; set; }
    public MeteringPointV4ConsumerPartyDto? FirstConsumerParty { get; set; }
    public MeteringPointV4ConsumerPartyDto? SecondConsumerParty { get; set; }
}

public class MeteringPointV4IdentificationDto : MeteringPointV4SectionDto
{
    public string? MeteringPointIdentification { get; set; }
    public string? InstallationNumber { get; set; }
}

public class MeteringPointV4InstallationAddressDto : MeteringPointV4SectionDto
{
    /// <summary>Uses <see cref="PhysicalAddressDto"/> — same shape as customer/payer address.</summary>
    public PhysicalAddressDto? Address { get; set; }
    public string? InstallationAddressNote { get; set; }
}

public class MeteringPointV4GridDto : MeteringPointV4SectionDto
{
    public Guid? MarketBalanceAreaId { get; set; }
    public Guid? MeteringGridAreaId { get; set; }
    public Guid? BalanceSupplierId { get; set; }
    public string? SupplierChangeType { get; set; }
}

public class MeteringPointV4SettlementMethodDto : MeteringPointV4SectionDto
{
    public string? SettlementMethod { get; set; }
}

public class MeteringPointV4MeterCharacteristicsDto : MeteringPointV4SectionDto
{
    public string? MeteringPointType { get; set; }
    public QuantityDto? EstimatedAnnualVolume { get; set; }
}

public class MeteringPointV4ConnectionInfoDto : MeteringPointV4SectionDto
{
    public string? ConnectionStatus { get; set; }
}

public class MeteringPointV4DeliveryStatusDto : MeteringPointV4SectionDto
{
    public bool InDelivery { get; set; }
    public string? StartReason { get; set; }
    public string? StopReason { get; set; }
}

public class MeteringPointV4NotesDto : MeteringPointV4SectionDto
{
    public string? AdditionalInfo1 { get; set; }
    public string? AdditionalInfo2 { get; set; }
    public string? Remark { get; set; }
}

/// <summary>
/// Consumer national ids (CPR) for a metering point at a given time. Settl's
/// <c>ConsumerPartyWithNationalIdentificationNumberDto</c>, fetched via
/// GET /api/v4/meteringPoints/{id}/nationalIdentificationNumber?EffectiveDate={date}.
/// The plain v4 GET never returns these values.
/// </summary>
public class MeteringPointConsumerNationalIdsDto
{
    public string? FirstConsumerPartyNationalIdentificationNumber { get; set; }
    public string? SecondConsumerPartyNationalIdentificationNumber { get; set; }
}

/// <summary>
/// Full market v4 MP response. All sections are independently versioned — see
/// <see cref="MeteringPointV4SectionDto"/> for the Start/End semantics.
/// Fetch via GET /api/v4/meteringPoints/{id}?EffectiveDate={date}.
/// History (per-section date lists) via GET /api/v3/meteringPoints/{id}/history.
/// </summary>
public class MeteringPointV4Dto
{
    public Guid Id { get; set; }
    public string? ExternalIdentifier { get; set; }
    public string? CountryCode { get; set; }
    public bool Closed { get; set; }
    public DateTime? LastUpdated { get; set; }
    public MeteringPointV4ConsumerDto? Consumer { get; set; }
    public MeteringPointV4IdentificationDto? Identification { get; set; }
    public MeteringPointV4InstallationAddressDto? InstallationAddress { get; set; }
    public MeteringPointV4GridDto? Grid { get; set; }
    public MeteringPointV4SettlementMethodDto? SettlementMethod { get; set; }
    public MeteringPointV4MeterCharacteristicsDto? MeterCharacteristics { get; set; }
    public MeteringPointV4ConnectionInfoDto? ConnectionInfo { get; set; }
    public MeteringPointV4DeliveryStatusDto? DeliveryStatus { get; set; }
    public MeteringPointV4NotesDto? Notes { get; set; }
}

// === Billing API models (v1 billing metering point API — PascalCase) ===

public class BillingMeteringPointInfoDto
{
    public Guid Id { get; set; }
    public string? MeteringPointIdentification { get; set; }
    public bool? JointInvoice { get; set; }
    public string? JointInvoiceNumber { get; set; }
}

public class BillingMeteringPointHistoryResponse
{
    public List<DateTime>? BillingConfigurationDates { get; set; }
    /// <summary>Effective dates of each historical PaymentConfiguration entry, mirroring
    /// <see cref="BillingConfigurationDates"/>. The two timelines version independently.</summary>
    public List<DateTime>? PaymentConfigurationDates { get; set; }
}

/// <summary>Full billing MP as returned by GET /api/v1/meteringPoints/{id}.</summary>
public class BillingMeteringPointCurrentDto
{
    public Guid Id { get; set; }
    public BillingConfigurationDto? BillingConfiguration { get; set; }
    public MeteringPointPaymentConfigurationDto? PaymentConfiguration { get; set; }
    public List<BillingExemptionDto>? Exemptions { get; set; }
}

/// <summary>
/// Billing exemption attached to a metering point. Currently only ExemptionType="ProductElement"
/// is supported by Settl. Used to model "uden gebyr" contracts where a specific ProductElement
/// (e.g. invoice fee) is fully waived for that MP.
/// </summary>
public class BillingExemptionDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExemptionType { get; set; } = "ProductElement";
    public Guid ProductElementId { get; set; }
    public double ExemptionPercentage { get; set; }
}

public class AddMeteringPointExemptionsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<BillingExemptionDto> Exemptions { get; set; } = [];
}

public class ProductElementInfoDto
{
    public Guid Id { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
}

public class BillingConfigurationDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? LifeTime { get; set; }
    [JsonConverter(typeof(NullableDateOnlyUtcConverter))]
    public DateOnly? End { get; set; }
    public bool JointInvoice { get; set; }
    public string? JointInvoiceNumber { get; set; }
    public string? Currency { get; set; }
    public bool NextInvoiceManual { get; set; }
    public bool AutomaticApprovalOfDraft { get; set; }
}

/// <summary>
/// Payment-config section of the billing-MP update payload. Holds EAN
/// (<see cref="ElectronicNumber"/>), invoice CC fields, and assorted
/// identifiers. Nullable on the request; only populated sections are applied.
/// </summary>
public class MeteringPointPaymentConfigurationDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string LifeTime { get; set; } = "UntilNextHistoricalEntry";
    [JsonConverter(typeof(NullableDateOnlyUtcConverter))]
    public DateOnly? End { get; set; }
    public string? InvoiceCcEmail { get; set; }
    public string? InvoiceCcElectronicNumber { get; set; }
    public string? BillingType { get; set; }
    public string? OrderNumber { get; set; }
    public string? ElectronicNumber { get; set; }
    public string? AgreementIdentifier { get; set; }
    public string? OrderIdentifier { get; set; }
    public string? BookkeepingReference { get; set; }
    public string? BuyerReference { get; set; }
}

public class UpdateBillingMeteringPointRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public BillingConfigurationDto? BillingConfiguration { get; set; }
    /// <summary>
    /// Payment-config sub-section. Used by fix-electronic-number and any other
    /// runner that touches EAN / invoice-CC / identifier fields.
    /// </summary>
    public MeteringPointPaymentConfigurationDto? PaymentConfiguration { get; set; }
}

// === Annual consumptions models (market API v3) ===

public class UpdateAnnualConsumptionsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public QuantityPeriodItem? AnnualConsumptions { get; set; }
}

public class QuantityPeriodItem
{
    public TimePeriodDto? TimePeriod { get; set; }
    public QuantityDto? Quantity { get; set; }
}

public class QuantityDto
{
    public string? Unit { get; set; }
    public string? Kind { get; set; }
    public double Value { get; set; }
}

public class EstimatedAnnualVolumeDto
{
    public string? Unit { get; set; }
    public string? Kind { get; set; }
    public double Value { get; set; }
    public bool IsEmpty { get; set; }
}

public class MeteringPointCurrentMeterCharacteristicsDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? MeteringPointType { get; set; }
    public string? MeteringPointSubType { get; set; }
    public EstimatedAnnualVolumeDto? EstimatedAnnualVolume { get; set; }
    public List<MeteringPointMeterDto> Meters { get; set; } = [];
}

// === Grid lookup models (market API v1/v2) ===

public class MarketBalanceAreaInfoDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
}

public class MeteringGridAreaInfoDto
{
    public Guid Id { get; set; }
    // Only populated when fetched via detail endpoint, not the list endpoint
    public string? MeteringGridAreaIdentification { get; set; }
}
