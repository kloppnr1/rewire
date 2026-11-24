using System.Text.Json.Nodes;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlPayerClient : SettlApiClientBase, ISettlPayerClient
{
    public SettlPayerClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlPayerClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public async Task<(bool Success, string? Error)> CreateAsync(
        CreatePayersRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers";
        return await PostAsync(url, request, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> AddMeteringPointsAsync(
        Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}/MeteringPoints";
        return await PostAsync(url, request, cancellationToken);
    }

    public async Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(
        Guid payerId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}";
        if (effectiveDate.HasValue)
            url += $"?EffectiveDate={effectiveDate.Value:yyyy-MM-ddTHH:mm:ssZ}";
        return await SendAsync<PayerResponse>(HttpMethod.Get, url, null, cancellationToken);
    }

    public async Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(
        Guid payerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}/History";
        return await SendAsync<PayerHistoryResponse>(HttpMethod.Get, url, null, cancellationToken);
    }

    public async Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(
        string payerIdentification, string countryCode = "DK", CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers?PayerIdentification={Uri.EscapeDataString(payerIdentification)}&CountryCode={countryCode}&Limit=1";

        var (response, error) = await SendAsync<FindResponse<PayerInfoDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return (null, error);

        if (response?.Items is { Count: > 0 })
            return (response.Items[0], null);

        return (null, null);
    }

    public async Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid payerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}/MeteringPoints?Limit=1000";

        var (response, error) = await SendAsync<FindResponse<PayerMeteringPointDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return (null, error);

        return (response?.Items ?? [], null);
    }

    public async Task<(string? Json, string? Error)> GetRawByIdAsync(
        Guid payerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}";
        return await GetRawAsync(url, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> UpdateRawAsync(
        Guid payerId, string updateRequestJson, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}";
        return await PutRawAsync(url, updateRequestJson, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> UpdatePayerBillingAsync(
        Guid payerId, IReadOnlyDictionary<string, JsonNode?> billingPatches, CancellationToken cancellationToken = default)
    {
        // Encapsulates Settl's payer-update CONTRACT so consumers don't hand-roll it: GET the payer raw,
        // overwrite the named PayerBilling leaves, and PUT it back in Settl's FLAT shape (fields at top
        // level + TransactionId, PayerInformation null — see GetRawByIdAsync/UpdateRawAsync docs). A wrapped
        // { TransactionId, Payer } body or a round-tripped PayerInformation deletes the customer↔payer link.
        var (json, getErr) = await GetRawByIdAsync(payerId, cancellationToken);
        if (json == null)
            return (false, $"GET {getErr}");

        var root = JsonNode.Parse(json)?.AsObject();
        var billing = root?["PayerBilling"]?.AsObject();
        if (root == null || billing == null)
            return (false, "response has no PayerBilling");

        foreach (var (leaf, value) in billingPatches)
        {
            // Settl GET/PUT asymmetry: GET returns 'OrganizationIdentifier' but PUT requires 'VatIdentificationNumber'
            var putKey = leaf == "OrganizationIdentifier" ? "VatIdentificationNumber" : leaf;
            billing[putKey] = value?.DeepClone();
        }

        root.Remove("Uri");
        root.Remove("Links");
        root["PayerInformation"] = null;
        root["TransactionId"] = Guid.NewGuid().ToString();

        return await UpdateRawAsync(payerId, root.ToJsonString(), cancellationToken);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(
        Guid payerId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/{payerId}?TransactionId={Guid.NewGuid()}";
        return await DeleteAsync(url, cancellationToken);
    }

    public async Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(
        int limit = 100, int skip = 0, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers?CountryCode=DK&Limit={limit}&Skip={skip}&DoCount=true";

        var (response, error) = await SendAsync<FindResponse<PayerInfoDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return ([], 0, error);

        var totalCount = response?.CountResult?.Count ?? 0;
        return (response?.Items ?? [], totalCount, null);
    }

    public async Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(
        Guid customerId, string countryCode = "DK", CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/Payers?CustomerId={customerId}&CountryCode={countryCode}&Limit=1000";

        var (response, error) = await SendAsync<FindResponse<PayerInfoDto>>(
            HttpMethod.Get, url, null, cancellationToken);

        if (error != null)
            return (null, error);

        return (response?.Items ?? [], null);
    }

    public async Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(
        IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken cancellationToken = default)
    {
        if (meteringPointIds.Count == 0)
            return ([], null);

        var url = $"{Options.BillingBaseUrl}/api/v1/Payers/payersForMeteringPoints";
        var body = new FindPayersForMeteringPointsRequest
        {
            MeteringPointIds = meteringPointIds.ToList(),
            SearchDate = searchDate,
            Limit = meteringPointIds.Count,
        };

        var (response, error) = await SendAsync<List<PayerForMeteringPointsDto>>(
            HttpMethod.Post, url, body, cancellationToken);

        if (error != null)
            return (null, error);

        return (response ?? [], null);
    }
}
