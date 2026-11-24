using System.Text.Json.Serialization;

namespace DataMigration.SettlApi.Models;

// === Request models ===

public class CreatePayersRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<CreatePayerDto> Payers { get; set; } = [];
}

public class CreatePayerDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ExternalIdentifier { get; set; }
    public string CountryCode { get; set; } = "DK";
    public PayerInformationDto? PayerInformation { get; set; }
    public CreatePayerBillingDto PayerBilling { get; set; } = new();
    public bool AutomaticallyApproveInvoices { get; set; }
    public Guid CustomerId { get; set; }
    public bool IsPrimaryCustomerPayer { get; set; } = true;
}

public class PayerInformationDto
{
    /// <summary>
    /// Version boundary. Settl maintains a timeline of PayerInformation versions; a PUT carrying a
    /// different Start creates a NEW version rather than updating the existing entry. Do NOT diff or
    /// apply this field as content — use <see cref="ISettlPayerClient.GetHistoryAsync"/> to enumerate
    /// versions and fetch each with <see cref="ISettlPayerClient.GetByIdAsync(Guid, DateTime?, CancellationToken)"/>.
    /// </summary>
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? PayerIdentifier { get; set; }
    public PostalAddressDto? PostalAddress { get; set; }
}

public class CreatePayerBillingDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? Email { get; set; }
    public string? ElectronicNumber { get; set; }
    public string InvoiceChannel { get; set; } = "PDF";
    public string PaymentChannel { get; set; } = "FIK";
    public string? PayerType { get; set; }
    public string? InvoiceText1 { get; set; }
    public string? InvoiceText2 { get; set; }
    public string? Notes { get; set; }
    public string? Operator { get; set; }
    public string? BuyerReferenceIdentifier { get; set; }
    public string? AgreementIdentifier { get; set; }
    public string? OrderIdentifier { get; set; }
    public string? BookkeepingReference { get; set; }
    public string? VatIdentificationNumber { get; set; }
    public string? NationalIdentificationNumber { get; set; }
}

// === Update models ===
// (No typed payer-update DTO: Settl's payer PUT expects a FLAT body the GET shape doesn't match — the
// only working update path is ISettlPayerClient.UpdateRawAsync with a hand-built flat JSON body. A typed
// wrapped { TransactionId, Payer } request deleted the customer↔payer link, so it was removed 2026-06-13.)

// === Response models ===

public class PayerResponse
{
    public Guid Id { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public bool IsPrimaryCustomerPayer { get; set; }
    public PayerInformationDto? PayerInformation { get; set; }
    public PayerBillingDto? PayerBilling { get; set; }
}

public class PayerBillingDto
{
    /// <summary>Version boundary — same semantics as <see cref="PayerInformationDto.Start"/>.</summary>
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? Email { get; set; }
    public string? ElectronicNumber { get; set; }
    public string InvoiceChannel { get; set; } = string.Empty;
    public string PaymentChannel { get; set; } = string.Empty;
    public string? PayerType { get; set; }
    public string? InvoiceText1 { get; set; }
    public string? InvoiceText2 { get; set; }
    public string? Notes { get; set; }
    public string? Operator { get; set; }
    public string? BuyerReferenceIdentifier { get; set; }
    public string? AgreementIdentifier { get; set; }
    public string? OrderIdentifier { get; set; }
    public string? BookkeepingReference { get; set; }
    public string? OrganizationIdentifier { get; set; }
    public string? NationalIdentificationNumber { get; set; }
}

// === History ===

/// <summary>
/// Response from GET /api/v1/Payers/{id}/History. Payer (like billing MP) is versioned:
/// each entry in <see cref="ChangeDates"/> marks the Start of one PayerInformation +
/// PayerBilling version. Fetch a specific version with
/// <c>GET /api/v1/Payers/{id}?EffectiveDate={date}</c>.
/// </summary>
public class PayerHistoryResponse
{
    public List<DateTime>? ChangeDates { get; set; }
}

// === Metering point linking ===

public class AddPayerMeteringPointsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public DateTime SnapshotTime { get; set; } = DateTime.UtcNow;
    public List<PayerMeteringPointDto> MeteringPoints { get; set; } = [];
}

public class PayerMeteringPointDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ExternalIdentifier { get; set; }
    public Guid MeteringPointId { get; set; }
    public string CommodityType { get; set; } = "Electricity";

    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }

    [JsonConverter(typeof(NullableDateOnlyUtcConverter))]
    public DateOnly? End { get; set; }
}

public class PayerInfoDto
{
    public Guid Id { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public bool IsPrimaryCustomerPayer { get; set; }
    public string? PayerIdentification { get; set; }
    public string? InvoiceChannel { get; set; }
    public string? PaymentChannel { get; set; }
    public string? ElectronicNumber { get; set; }
    public string? Email { get; set; }
}

// === Reverse lookup: payers (and thereby the owning customer) for metering points ===

/// <summary>Request for POST /api/v1/Payers/payersForMeteringPoints.</summary>
public class FindPayersForMeteringPointsRequest
{
    public int? Limit { get; set; }
    public int? Skip { get; set; }
    public bool DoCount { get; set; }
    public DateTime? SearchDate { get; set; }
    public List<Guid> MeteringPointIds { get; set; } = [];
}

/// <summary>
/// One payer associated with one or more of the requested metering points.
/// <see cref="PayerResponse.CustomerId"/> is the bridge MP → owning customer.
/// </summary>
public class PayerForMeteringPointsDto
{
    public PayerResponse? Payer { get; set; }
    public List<PayerMeteringPointDto>? MeteringPoints { get; set; }
}
