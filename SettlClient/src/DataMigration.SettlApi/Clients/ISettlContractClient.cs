using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlContractClient
{
    Task<(List<ContractInfoDto>? Contracts, string? Error)> FindContractsAsync(
        string? commodityType = null, CancellationToken cancellationToken = default);

    Task<(List<ContractMeteringPointInfoDto>? MeteringPoints, string? Error)> GetContractMeteringPointsAsync(
        Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every contract a metering point is — or ever was — linked to, including
    /// links whose period has ended. Filters the global contracts list by MeteringPointId
    /// (honoured server-side). This is the way to surface ended links: the customer-scoped
    /// <see cref="GetCustomerMeteringPointContractsAsync"/> only returns contracts with a
    /// currently-active MP link.
    /// </summary>
    Task<(List<ContractInfoDto>? Contracts, string? Error)> FindContractsByMeteringPointAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    Task<(List<ContractLineInfoDto>? ContractLines, string? Error)> GetContractLinesAsync(
        Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a customer's contracts with their linked metering points in one call —
    /// the direct path, avoiding a global scan of all contracts.
    /// </summary>
    Task<(List<CustomerContractWithMeteringPointsDto>? Contracts, string? Error)> GetCustomerMeteringPointContractsAsync(
        Guid customerId, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> AddContractMeteringPointsAsync(
        Guid contractId, AddContractMeteringPointsRequest request, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> RemoveContractMeteringPointAsync(
        Guid contractId, Guid linkId, CancellationToken cancellationToken = default);
}
