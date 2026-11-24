using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlTimeSeriesClient : SettlApiClientBase, ISettlTimeSeriesClient
{
    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    public SettlTimeSeriesClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlTimeSeriesClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public async Task<(bool Success, string? Error)> PostPointsAsync(
        Guid timeSeriesGuid, SettlTimeSeriesRequestDto payload, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.MarketTenantBaseUrl}/v1/time-series/{timeSeriesGuid}/points";
        return await PostAsync(url, payload, JsonOptions, cancellationToken);
    }

    public async Task<(SettlTimeSeriesPointsResponseDto? Points, string? Error)> GetPointsAsync(
        Guid timeSeriesGuid,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        // Settl forventer Start/End som Danish-midnight-UTC (Z-suffix) — samme regel
        // som EIP-payloaden. Bruger DateOnlyUtcConverter's pattern inline.
        var url = $"{Options.MarketTenantBaseUrl}/v1/time-series/{timeSeriesGuid}/points"
            + $"?Start={Uri.EscapeDataString(FormatDanishMidnightUtc(start))}"
            + $"&End={Uri.EscapeDataString(FormatDanishMidnightUtc(end))}";

        return await SendAsync<SettlTimeSeriesPointsResponseDto>(
            HttpMethod.Get, url, null, JsonOptions, cancellationToken);
    }

    public async Task<(SettlPointsSummaryDto? Summary, string? Error)> GetPointsSummaryAsync(
        Guid timeSeriesGuid,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        var (response, error) = await GetPointsAsync(timeSeriesGuid, start, end, cancellationToken);
        if (error != null) return (null, error);

        var points = response?.Points;
        if (points == null) return (new SettlPointsSummaryDto { Count = 0 }, null);

        var summary = new SettlPointsSummaryDto
        {
            Count = points.Count,
            From = points.Count > 0 ? new DateTimeOffset(points.Min(p => p.Time), TimeSpan.Zero) : null,
            To = points.Count > 0 ? new DateTimeOffset(points.Max(p => p.Time), TimeSpan.Zero) : null,
        };
        return (summary, null);
    }

    private static string FormatDanishMidnightUtc(DateOnly value)
    {
        var danishMidnight = new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(danishMidnight, DanishTimeZone);
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
