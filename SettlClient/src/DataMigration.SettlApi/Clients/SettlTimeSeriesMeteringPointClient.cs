using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlTimeSeriesMeteringPointClient : SettlMeteringPointClient, ISettlTimeSeriesMeteringPointClient
{
    private string? _deliveryCategory;

    public SettlTimeSeriesMeteringPointClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlMeteringPointClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    // Re-implement so GetTimeSeriesAsync / AddExternallyInvoicedPeriodsAsync can access delivery category.
    void ISettlMeteringPointClient.SetDeliveryCategory(string deliveryCategory)
    {
        _deliveryCategory = deliveryCategory;
        base.SetDeliveryCategory(deliveryCategory);
    }

    public async Task<(List<TimeSeriesInfoDto>? TimeSeries, string? Error)> GetTimeSeriesAsync(
        Guid meteringPointGuid, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointGuid}/timeSeries";
        var (response, error) = await SendAsync<List<TimeSeriesInfoDto>>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
        return (response ?? [], error);
    }

    public async Task<(bool Success, string? Error)> AddExternallyInvoicedPeriodsAsync(
        Guid meteringPointId, AddExternallyInvoicedPeriodsRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints/{meteringPointId}/externallyInvoicedPeriods";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }
}
