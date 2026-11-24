using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlDebtorClient : SettlApiClientBase, ISettlDebtorClient
{
    public SettlDebtorClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlDebtorClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public async Task<(CustomerAccountInfoDto? Result, string? Error)> GetCustomerAccountInfoAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.DebtorsBaseUrl}/api/v1/debtorAccountEntries/customerAccountInfo?CustomerId={customerId}";
        return await SendAsync<CustomerAccountInfoDto>(HttpMethod.Get, url, null, cancellationToken);
    }

    public async Task<(List<DebtorAccountEntryDto> Items, int TotalCount, string? Error)> FindEntriesForCustomerAsync(
        Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.DebtorsBaseUrl}/api/v1/debtorAccountEntries/findEntriesForCustomer" +
                  $"?CustomerId={customerId}&Skip={skip}&Limit={limit}&DoCount=true";

        var (response, error) = await SendAsync<FindDebtorEntriesResponse>(HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return ([], 0, error);

        return (response?.Items ?? [], response?.CountResult?.Matches ?? 0, null);
    }

    public async Task<(DebtorAccountEntryDto? Entry, string? Error)> GetEntryByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.DebtorsBaseUrl}/api/v1/debtorAccountEntries/{id}";
        return await SendAsync<DebtorAccountEntryDto>(HttpMethod.Get, url, null, cancellationToken);
    }
}
