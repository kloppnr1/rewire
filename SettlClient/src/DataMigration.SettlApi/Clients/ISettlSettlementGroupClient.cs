using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlSettlementGroupClient
{
    /// <summary>
    /// Look up a single Settlement Group by exact GroupName (case-insensitive on the server) +
    /// CountryCode. Returns null when no match. Multiple matches → returns the first.
    /// </summary>
    Task<(SettlementGroupInfoDto? Group, string? Error)> FindByNameAsync(
        string countryCode, string groupName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the Settlement Groups a metering point currently belongs to (in any commodity).
    /// Used for idempotency: skip add when MP is already in the target group.
    /// </summary>
    Task<(List<SettlementGroupInfoDto>? Groups, string? Error)> FindByMeteringPointAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// List all Settlement Groups for a country (paginated). Used for upfront validation —
    /// build a name→GUID map once instead of one FindByNameAsync per target group, and emit
    /// the available names if a target is missing.
    /// </summary>
    Task<(List<SettlementGroupInfoDto>? Groups, string? Error)> ListAllAsync(
        string countryCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// List all MP-GUIDs currently in a Settlement Group (paginated 100 per page). Used for
    /// bulk idempotency-checks: fetch once per group instead of one FindByMeteringPoint per MP.
    /// </summary>
    Task<(HashSet<Guid>? MeteringPointIds, string? Error)> ListMeteringPointIdsAsync(
        Guid settlementGroupId, CancellationToken cancellationToken = default);

    /// <summary>Add metering points to a Settlement Group.</summary>
    Task<(bool Success, string? Error)> AddMeteringPointsAsync(
        Guid settlementGroupId,
        AddSettlementGroupMeteringPointsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all customer-GUIDs currently in a Settlement Group (paginated). Customer-based
    /// membership is preferred over MP-based for Verdo — Settl rejects/misbehaves when MPs
    /// are direct group-members.
    /// </summary>
    Task<(HashSet<Guid>? CustomerIds, string? Error)> ListCustomerIdsAsync(
        Guid settlementGroupId, CancellationToken cancellationToken = default);

    /// <summary>Add customers to a Settlement Group.</summary>
    Task<(bool Success, string? Error)> AddCustomersAsync(
        Guid settlementGroupId,
        AddSettlementGroupCustomersRequest request,
        CancellationToken cancellationToken = default);
}
