using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlPriceListClient : SettlApiClientBase, ISettlPriceListClient
{
    public SettlPriceListClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlPriceListClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public Task<(List<PriceListDto>? PriceLists, string? Error)> ListPriceListsAsync(
        string countryCode, CancellationToken cancellationToken = default)
        => PaginateAsync<PriceListDto>(
            $"{Options.BillingBaseUrl}/api/v1/priceLists"
            + $"?CountryCode={Uri.EscapeDataString(countryCode)}&Limit=1000",
            cancellationToken);

    public Task<(List<ProductElementDto>? ProductElements, string? Error)> ListProductElementsAsync(
        string countryCode, CancellationToken cancellationToken = default)
        => PaginateAsync<ProductElementDto>(
            $"{Options.BillingBaseUrl}/api/v1/productElements"
            + $"?CountryCode={Uri.EscapeDataString(countryCode)}&Limit=1000",
            cancellationToken);

    public Task<(List<CustomerPriceListDto>? Associations, string? Error)> GetCustomerPriceListsAsync(
        Guid customerId, CancellationToken cancellationToken = default)
        => PaginateAsync<CustomerPriceListDto>(
            $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/pricelists?Limit=1000",
            cancellationToken);

    /// <summary>Follows <c>NextUri</c> until exhausted, accumulating every page's items.</summary>
    private async Task<(List<T>? Items, string? Error)> PaginateAsync<T>(
        string? url, CancellationToken cancellationToken)
    {
        var all = new List<T>();
        while (url != null)
        {
            var (response, error) = await SendAsync<FindResponse<T>>(
                HttpMethod.Get, url, null, JsonOptions, cancellationToken);

            if (error != null)
                return (null, error);

            if (response?.Items != null)
                all.AddRange(response.Items);

            url = response?.NextUri;
        }

        return (all, null);
    }
}
