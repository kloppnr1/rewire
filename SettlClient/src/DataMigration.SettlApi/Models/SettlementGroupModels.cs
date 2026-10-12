namespace DataMigration.SettlApi.Models;

/// <summary>
/// Settlement Group info as returned by Settl billing v1 — used to look up groups by name
/// before adding metering points to them.
/// </summary>
public class SettlementGroupInfoDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public string? CountryCode { get; set; }
    public string? GroupName { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// MP-membership row from GET /api/v1/settlementGroups/{id}/meteringPoints.
/// Settl returns only the MP-GUID — no identification, no commodity, no name.
/// </summary>
public class SettlementGroupMeteringPointDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
}

/// <summary>
/// Request payload for POST /api/v1/settlementGroups/{id}/meteringPoints.
/// </summary>
public class AddSettlementGroupMeteringPointsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<Guid> MeteringPoints { get; set; } = [];
    public string CommodityType { get; set; } = string.Empty;
}

/// <summary>
/// Customer-membership row from GET /api/v1/settlementGroups/{id}/customers.
/// Id is the Settl CustomerId (same pattern as <see cref="SettlementGroupMeteringPointDto"/>).
/// </summary>
public class SettlementGroupCustomerDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public bool IncludeSubsidiaries { get; set; }
}

/// <summary>
/// Entry inside <see cref="AddSettlementGroupCustomersRequest"/>.
/// </summary>
public class SettlementGroupCustomerEntry
{
    public Guid CustomerId { get; set; }
    public bool IncludeCustomerSubsidiaries { get; set; }
}

/// <summary>
/// Request payload for POST /api/v1/settlementGroups/{id}/customers.
/// </summary>
public class AddSettlementGroupCustomersRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<SettlementGroupCustomerEntry> Customers { get; set; } = [];
}
