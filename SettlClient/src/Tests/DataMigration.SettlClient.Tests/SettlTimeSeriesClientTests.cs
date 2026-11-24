using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlTimeSeriesClientTests
{
    private const string PointsJson =
        """
        {
          "QuantityUnit": "kWh",
          "Points": [
            { "Time": "2024-01-01T00:00:00", "Quantity": 1.5, "Quality": "Measured" },
            { "Time": "2024-01-02T00:00:00", "Quantity": 2.5, "Quality": "Estimated" }
          ]
        }
        """;

    [Fact]
    public async Task GetPointsAsync_returns_raw_points_and_unit()
    {
        var (client, handler) = MakeClient(PointsJson);

        var (points, error) = await client.GetPointsAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new DateOnly(2024, 1, 1),
            new DateOnly(2024, 1, 31));

        Assert.Null(error);
        Assert.NotNull(points);
        Assert.Equal("kWh", points!.QuantityUnit);
        Assert.Equal(2, points.Points.Count);
        Assert.Equal(1.5m, points.Points[0].Quantity);
        Assert.Equal("Estimated", points.Points[1].Quality);

        // Verifér at den rammer /v1/time-series/{guid}/points med Start+End query-params.
        Assert.Contains("/v1/time-series/11111111-1111-1111-1111-111111111111/points", handler.LastUrl);
        Assert.Contains("Start=", handler.LastUrl);
        Assert.Contains("End=", handler.LastUrl);
    }

    [Fact]
    public async Task GetPointsSummaryAsync_summarizes_count_and_window()
    {
        var (client, _) = MakeClient(PointsJson);

        var (summary, error) = await client.GetPointsSummaryAsync(
            Guid.NewGuid(), new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Null(error);
        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Count);
        Assert.Equal(new DateTime(2024, 1, 1), summary.From!.Value.DateTime);
        Assert.Equal(new DateTime(2024, 1, 2), summary.To!.Value.DateTime);
    }

    [Fact]
    public async Task GetPointsSummaryAsync_returns_zero_count_on_empty_body()
    {
        var (client, _) = MakeClient("");

        var (summary, error) = await client.GetPointsSummaryAsync(
            Guid.NewGuid(), new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        Assert.Null(error);
        Assert.NotNull(summary);
        Assert.Equal(0, summary!.Count);
    }

    private static (SettlTimeSeriesClient Client, CapturingHandler Handler) MakeClient(string responseBody)
    {
        var handler = new CapturingHandler(responseBody);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" });
        var client = new SettlTimeSeriesClient(
            httpClient,
            new FakeAuthService(),
            options,
            NullLogger<SettlTimeSeriesClient>.Instance);
        return (client, handler);
    }

    private sealed class FakeAuthService : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("test-token");

        public void InvalidateToken() { }
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
