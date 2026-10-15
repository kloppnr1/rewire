using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlContractClient : SettlApiClientBase, ISettlContractClient
{
    public SettlContractClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlContractClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public async Task<(List<ContractInfoDto>? Contracts, string? Error)> FindContractsAsync(
        string? commodityType = null, CancellationToken cancellationToken = default)
    {
        // Settl /internal-billing/api/v1/Contracts returnerer kun Electricity-kontrakter
        // når CommodityType-filteret udelades (silent udeladelse — dokumenteret adfærd).
        // Pass commodityType (Electricity/Water/Gas) for at få den rigtige liste.
        var url = $"{Options.BillingBaseUrl}/api/v1/Contracts?Limit=1000&DoCount=true";
        if (!string.IsNullOrEmpty(commodityType))
            url += $"&CommodityType={Uri.EscapeDataString(commodityType)}";

        var (response, error) = await SendAsync<FindResponse<ContractInfoDto>>(
            HttpMethod.Get, url, null, JsonOptions, cancellationToken);

        if (error != null)
            return (null, error);

        return (response?.Items ?? [], null);
    }

    public async Task<(List<ContractMeteringPointInfoDto>? MeteringPoints, string? Error)> GetContractMeteringPointsAsync(
        Guid contractId, CancellationToken cancellationToken = default)
    {
        // Paginate to fetch ALL linked MPs — production contracts can have thousands
        var all = new List<ContractMeteringPointInfoDto>();
        var url = $"{Options.BillingBaseUrl}/api/v1/contracts/{contractId}/meteringPoints?Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<ContractMeteringPointInfoDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }

    public async Task<(List<ContractMeteringPointInfoDto>? Links, string? Error)> GetContractMeteringPointLinksAsync(
        Guid contractId, Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        // Same route as GetContractMeteringPointsAsync, filtered server-side to one metering point.
        // A metering point can have several links (periods) on the same contract, so page anyway.
        var all = new List<ContractMeteringPointInfoDto>();
        var url = $"{Options.BillingBaseUrl}/api/v1/contracts/{contractId}/meteringPoints"
            + $"?MeteringPointId={meteringPointId}&Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<ContractMeteringPointInfoDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }

    public async Task<(List<ContractInfoDto>? Contracts, string? Error)> FindContractsByMeteringPointAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        // Filter the global contracts list by metering point. Unlike the customer-scoped
        // metering-point-contracts endpoint (which only returns contracts with a currently
        // ACTIVE MP link), this returns every contract the MP is or was linked to — including
        // ended links. The MeteringPointId query filter is honoured server-side.
        var all = new List<ContractInfoDto>();
        var url = $"{Options.BillingBaseUrl}/api/v1/Contracts?MeteringPointId={meteringPointId}&Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<ContractInfoDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }

    public async Task<(List<ContractLineInfoDto>? ContractLines, string? Error)> GetContractLinesAsync(
        Guid contractId, CancellationToken cancellationToken = default)
    {
        // Paginate to fetch ALL contract lines — a contract can have many price-element lines
        var all = new List<ContractLineInfoDto>();
        var url = $"{Options.BillingBaseUrl}/api/v1/Contracts/{contractId}/ContractLines?Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<ContractLineInfoDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }

    public async Task<(List<CustomerContractWithMeteringPointsDto>? Contracts, string? Error)> GetCustomerMeteringPointContractsAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        // Direct per-customer lookup — returns each contract with its linked MPs in one call.
        // CommodityTypes is required at runtime ("At least one commodity type must be specified");
        // request all commodities so consumers get the full contract set.
        var all = new List<CustomerContractWithMeteringPointsDto>();
        var url = $"{Options.BillingBaseUrl}/api/v1/contracts/customer/{customerId}/metering-point-contracts"
            + "?Limit=1000&CommodityTypes=Electricity&CommodityTypes=Gas&CommodityTypes=Water&CommodityTypes=DistrictHeating";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<CustomerContractWithMeteringPointsDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }

    public async Task<(bool Success, string? Error)> AddContractMeteringPointsAsync(
        Guid contractId, AddContractMeteringPointsRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/contracts/{contractId}/meteringPoints";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> RemoveContractMeteringPointAsync(
        Guid contractId, Guid linkId, CancellationToken cancellationToken = default)
    {
        var transactionId = Guid.NewGuid();
        var url = $"{Options.BillingBaseUrl}/api/v1/contracts/{contractId}/meteringPoints/{linkId}?TransactionId={transactionId}";
        return await DeleteAsync(url, cancellationToken);
    }
}
