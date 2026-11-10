using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlCustomerClient : SettlApiClientBase, ISettlCustomerClient
{
    public SettlCustomerClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlCustomerClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public async Task<(bool Success, string? Error)> CreateAsync(
        CreateCustomersRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers";
        return await PostAsync(url, request, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> AddMeteringPointsAsync(
        Guid customerId, AddCustomerMeteringPointsRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/meteringPoints";
        return await PostAsync(url, request, cancellationToken);
    }

    public async Task<(CustomerResponse? Customer, string? Error)> GetByIdAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}";
        return await SendAsync<CustomerResponse>(HttpMethod.Get, url, null, cancellationToken);
    }

    public async Task<(string? Json, string? Error)> GetRawByIdV3Async(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v3/customers/{customerId}";
        return await GetRawAsync(url, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> UpdateV3RawAsync(
        Guid customerId, string updateRequestJson, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v3/customers/{customerId}";
        return await PutRawAsync(url, updateRequestJson, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> UpdateNationalIdentificationNumberV3Async(
        Guid customerId, string nationalIdentificationNumber, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v3/customers/{customerId}/national-identification-number";
        var body = new UpdateCustomerNationalIdentificationNumberRequest
        {
            NationalIdentificationNumber = nationalIdentificationNumber,
        };
        return await PutAsync(url, body, JsonOptions, cancellationToken);
    }

    public async Task<(CustomerInfoDto? Customer, string? Error)> SearchByCustomerNumberAsync(
        string customerNumber, string countryCode = "DK", CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers?CustomerNumber={Uri.EscapeDataString(customerNumber)}&CountryCode={countryCode}&Limit=1";

        var (response, error) = await SendAsync<FindResponse<CustomerInfoDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return (null, error);

        if (response?.Items is { Count: > 0 })
            return (response.Items[0], null);

        return (null, null);
    }

    public async Task<(List<CustomerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/meteringPoints?Limit=1000";

        var (response, error) = await SendAsync<FindResponse<CustomerMeteringPointDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return (null, error);

        return (response?.Items ?? [], null);
    }

    public async Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> FindCustomersAsync(
        int limit = 1000, int skip = 0, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers?CountryCode=DK&Limit={limit}&Skip={skip}&DoCount=true";

        var (response, error) = await SendAsync<FindResponse<CustomerInfoDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return ([], 0, error);

        var totalCount = response?.CountResult?.Count ?? 0;
        return (response?.Items ?? [], totalCount, null);
    }

    public async Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> SearchCustomersAsync(
        string? fullText = null,
        string? name = null,
        string? vatIdentificationNumber = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { "CountryCode=DK", $"Limit={limit}", "DoCount=true" };
        if (!string.IsNullOrWhiteSpace(fullText))
            query.Add($"FullTextSearch={Uri.EscapeDataString(fullText)}");
        if (!string.IsNullOrWhiteSpace(name))
            query.Add($"Name={Uri.EscapeDataString(name)}");
        if (!string.IsNullOrWhiteSpace(vatIdentificationNumber))
            query.Add($"VatIdentificationNumber={Uri.EscapeDataString(vatIdentificationNumber)}");

        var url = $"{Options.BillingBaseUrl}/api/v2/customers?{string.Join("&", query)}";

        var (response, error) = await SendAsync<FindResponse<CustomerInfoDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return ([], 0, error);

        return (response?.Items ?? [], response?.CountResult?.Count ?? 0, null);
    }

    public async Task<(List<CustomerOrganisationMeteringPointDto>? Items, string? Error)> GetOrganisationMeteringPointsAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/meteringPointsInOrganisation";

        var (response, error) = await SendAsync<List<CustomerOrganisationMeteringPointDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return (null, error);

        return (response ?? [], null);
    }

    public async Task<(CustomerHistoryResponse? History, string? Error)> GetHistoryAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/history";
        return await SendAsync<CustomerHistoryResponse>(HttpMethod.Get, url, null, cancellationToken);
    }

    public async Task<(List<CustomerSegmentDto>? Segments, string? Error)> FindCustomerSegmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/customerSegments";
        return await SendAsync<List<CustomerSegmentDto>>(HttpMethod.Get, url, null, cancellationToken);
    }

    public async Task<(SearchCustomerPriceListResponse? Result, string? Error)> SearchPriceListsAsync(
        Guid customerId, SearchCustomerPriceListRequest? request = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/pricelists/search";
        return await SendAsync<SearchCustomerPriceListResponse>(
            HttpMethod.Post, url, request ?? new SearchCustomerPriceListRequest(), cancellationToken);
    }

    public async Task<(List<ExtraInvoiceLineDto> Items, int TotalCount, string? Error)> GetExtraInvoiceLinesAsync(
        Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/{customerId}/extraInvoiceLines"
                  + $"?countryCode=DK&Limit={limit}&Skip={skip}";

        var (response, error) = await SendAsync<FindResponse<ExtraInvoiceLineDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return ([], 0, error);

        return (response?.Items ?? [], response?.CountResult?.Count ?? 0, null);
    }
}
