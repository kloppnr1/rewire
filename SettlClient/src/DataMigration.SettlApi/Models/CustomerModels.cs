using System.Text.Json.Serialization;

namespace DataMigration.SettlApi.Models;

// === Request models ===

public class CreateCustomersRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<CreateCustomerDto> Customers { get; set; } = [];
}

public class CreateCustomerDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ExternalIdentifier { get; set; }
    public string CountryCode { get; set; } = "DK";
    public string? CustomerNumber { get; set; }
    public string CustomerType { get; set; } = "Person";
    public string? VatIdentificationNumber { get; set; }
    public string? NationalIdentificationNumber { get; set; }
    public CustomerAddressDto? Address { get; set; }
    public CustomerCrmDataDto? CrmData { get; set; }
    public CustomerOwnerDto? Owner { get; set; }
}

public class CustomerOwnerDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public Guid OwnerId { get; set; }
}

public class CustomerAddressDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public PostalAddressDto? PostalAddress { get; set; }
    public string? Phone { get; set; }
    public string? Cellphone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
}

public class PostalAddressDto
{
    public ConsumerNameDto? Name1 { get; set; }
    public ConsumerNameDto? Name2 { get; set; }
    public PhysicalAddressDto? PhysicalAddress { get; set; }
}

public class ConsumerNameDto
{
    public string? FullName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

public class PhysicalAddressDto
{
    public string? StreetCode { get; set; }
    public string? StreetName { get; set; }
    public string? BuildingNumber { get; set; }
    public string? BuildingFloor { get; set; }
    public string? RoomIdentification { get; set; }
    public string? CitySubDivisionName { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? MunicipalityCode { get; set; }
    public string CountryCode { get; set; } = "DK";
    public AddressIdentifierDto? AddressIdentifier { get; set; }
}

public class AddressIdentifierDto
{
    public string? Identification { get; set; }
    public string IdentificationKind { get; set; } = "Dar";
}

public class CustomerCrmDataDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }
    public string? CustomerGroupName { get; set; }
}

/// <summary>
/// Body for <c>PUT /api/v3/customers/{id}/national-identification-number</c>. v3 moved the national
/// identification number (CPR) OUT of the main customer body into this dedicated sub-resource — it is
/// the one customer-core leaf the wrapped v3 customer PUT cannot carry. Flat, single-field; the
/// endpoint requires a non-empty value (it cannot clear) and returns 204.
/// </summary>
public class UpdateCustomerNationalIdentificationNumberRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public string NationalIdentificationNumber { get; set; } = string.Empty;
}

// === Customer history ===

/// <summary>Customer version change-dates from GET /api/v2/customers/{id}/history.</summary>
public class CustomerHistoryResponse
{
    public List<DateTime>? ChangeDates { get; set; }
}

// === Customer segments ===

public class CustomerSegmentSettlementSettings
{
    public bool ExcludedFromFees { get; set; }
    public List<string> BillableTypes { get; set; } = [];
}

/// <summary>
/// Customer segment catalog entry from GET /api/v1/customerSegments.
/// <see cref="Id"/> is the GUID set on a customer's <c>CustomerSegmentId</c>.
/// </summary>
public class CustomerSegmentDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? DefaultForCustomerType { get; set; }
    public string? SupportedCustomerType { get; set; }
    public string? CurrencyCode { get; set; }
    public bool ShouldBeAutomaticallyProcessed { get; set; }
    public CustomerSegmentSettlementSettings? ElectricitySettlementSettings { get; set; }
    public CustomerSegmentSettlementSettings? HeatingSettlementSettings { get; set; }
    public CustomerSegmentSettlementSettings? WaterSettlementSettings { get; set; }
}

// === Pricelist search ===

/// <summary>
/// POST body for /api/v2/customers/{id}/pricelists/search. An empty list returns all pricelist
/// associations for the customer. A non-empty list filters to those association IDs.
/// </summary>
public class SearchCustomerPriceListRequest
{
    public List<Guid> CustomerPriceListAssociations { get; set; } = [];
}

public class SearchCustomerPriceListResponse
{
    public List<CustomerPriceListDto> CustomerPriceListAssociations { get; set; } = [];
}

// === Extra invoice lines ===

/// <summary>
/// An extra invoice line from GET /api/v2/customers/{id}/extraInvoiceLines.
/// Fields are populated on demand — extend when live data reveals additional fields.
/// </summary>
public class ExtraInvoiceLineDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public double? Amount { get; set; }
    public string? CurrencyCode { get; set; }
}

// === Response models ===

public class CustomerResponse
{
    public Guid Id { get; set; }
    public string? CustomerNumber { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string CustomerType { get; set; } = string.Empty;
    public string? VatIdentificationNumber { get; set; }
    public string? NationalIdentificationNumber { get; set; }
    public CustomerAddressDto? Address { get; set; }
    public CustomerOwnerDto? Owner { get; set; }
}

// === Metering point linking ===

public class AddCustomerMeteringPointsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public DateTime SnapshotTime { get; set; } = DateTime.UtcNow;
    public List<CustomerMeteringPointDto> CustomerMeteringPoints { get; set; } = [];
}

public class CustomerMeteringPointDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ExternalIdentifier { get; set; }
    public string CommodityType { get; set; } = "Electricity";
    public Guid MeteringPointId { get; set; }

    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }

    [JsonConverter(typeof(NullableDateOnlyUtcConverter))]
    public DateOnly? End { get; set; }
}

public class CustomerInfoDto
{
    public Guid Id { get; set; }
    public string? CustomerNumber { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string CustomerType { get; set; } = string.Empty;
    public string? VatIdentificationNumber { get; set; }
    public string? NationalIdentificationNumber { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
}

/// <summary>
/// A metering point within a customer's organisation (the customer itself + its subsidiaries),
/// as returned by GET /api/v2/customers/{id}/meteringPointsInOrganisation.
/// <see cref="CustomerId"/> is the subsidiary that actually owns the MP.
/// </summary>
public class CustomerOrganisationMeteringPointDto
{
    public Guid Id { get; set; }
    public Guid MeteringPointId { get; set; }
    public Guid CustomerId { get; set; }
    public string CommodityType { get; set; } = "Electricity";
}
