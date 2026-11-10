using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlSettlementGroupClient : SettlApiClientBase, ISettlSettlementGroupClient
{
    public SettlSettlementGroupClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlSettlementGroupClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public async Task<(SettlementGroupInfoDto? Group, string? Error)> FindByNameAsync(
        string countryCode, string groupName, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups"
                  + $"?CountryCode={Uri.EscapeDataString(countryCode)}"
                  + $"&GroupName={Uri.EscapeDataString(groupName)}"
                  + "&Limit=10";

        var (response, error) = await SendAsync<FindResponse<SettlementGroupInfoDto>>(
            HttpMethod.Get, url, null, JsonOptions, cancellationToken);

        if (error != null)
            return (null, error);

        // Servers may return prefix matches — filter exact (case-insensitive) and prefer Active.
        var match = response?.Items?
            .Where(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.IsActive)
            .FirstOrDefault();

        return (match, null);
    }

    public async Task<(List<SettlementGroupInfoDto>? Groups, string? Error)> FindByMeteringPointAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups"
                  + $"?MeteringPointId={meteringPointId}"
                  + "&Limit=100";

        var (response, error) = await SendAsync<FindResponse<SettlementGroupInfoDto>>(
            HttpMethod.Get, url, null, JsonOptions, cancellationToken);

        if (error != null)
            return (null, error);

        return (response?.Items ?? [], null);
    }

    public async Task<(List<SettlementGroupInfoDto>? Groups, string? Error)> ListAllAsync(
        string countryCode, CancellationToken cancellationToken = default)
    {
        var all = new List<SettlementGroupInfoDto>();
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups"
                  + $"?CountryCode={Uri.EscapeDataString(countryCode)}"
                  + "&Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<SettlementGroupInfoDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }

    public async Task<(HashSet<Guid>? MeteringPointIds, string? Error)> ListMeteringPointIdsAsync(
        Guid settlementGroupId, CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups/{settlementGroupId}/meteringPoints?Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<SettlementGroupMeteringPointDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                foreach (var item in response.Items)
                    ids.Add(item.Id);

            url = response?.NextUri;
        }

        return (ids, null);
    }

    public async Task<(bool Success, string? Error)> AddMeteringPointsAsync(
        Guid settlementGroupId,
        AddSettlementGroupMeteringPointsRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups/{settlementGroupId}/meteringPoints";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(HashSet<Guid>? CustomerIds, string? Error)> ListCustomerIdsAsync(
        Guid settlementGroupId, CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups/{settlementGroupId}/customers?Limit=1000";

        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<SettlementGroupCustomerDto>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                foreach (var item in response.Items)
                    ids.Add(item.Id);

            url = response?.NextUri;
        }

        return (ids, null);
    }

    public async Task<(bool Success, string? Error)> AddCustomersAsync(
        Guid settlementGroupId,
        AddSettlementGroupCustomersRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/settlementGroups/{settlementGroupId}/customers";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }
}
