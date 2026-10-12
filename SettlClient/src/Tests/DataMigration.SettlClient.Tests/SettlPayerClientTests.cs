using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlPayerClientTests
{
    [Fact]
    public async Task FindPayersByCustomerAsync_filters_by_customerId_and_returns_items()
    {
        const string json =
            """
            {
              "Items": [
                { "Id": "aaaaaaaa-0000-0000-0000-000000000001", "CustomerId": "818f33e6-184b-4e8c-a0b2-a9ca9517fdc1", "CountryCode": "DK", "IsPrimaryCustomerPayer": true },
                { "Id": "aaaaaaaa-0000-0000-0000-000000000002", "CustomerId": "818f33e6-184b-4e8c-a0b2-a9ca9517fdc1", "CountryCode": "DK", "IsPrimaryCustomerPayer": false }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);
        var customerId = Guid.Parse("818f33e6-184b-4e8c-a0b2-a9ca9517fdc1");

        var (items, error) = await client.FindPayersByCustomerAsync(customerId);

        Assert.Null(error);
        Assert.NotNull(items);
        Assert.Equal(2, items!.Count);
        Assert.All(items, p => Assert.Equal(customerId, p.CustomerId));

        Assert.Contains("/api/v1/Payers", handler.LastUrl);
        Assert.Contains($"CustomerId={customerId}", handler.LastUrl);
    }

    [Fact]
    public async Task FindPayersByCustomerAsync_returns_empty_list_when_no_matches()
    {
        var (client, _) = MakeClient("""{ "Items": [] }""");

        var (items, error) = await client.FindPayersByCustomerAsync(Guid.NewGuid());

        Assert.Null(error);
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task FindPayersByCustomerAsync_returns_error_on_http_failure()
    {
        var (client, _) = MakeClient("nope", HttpStatusCode.InternalServerError);

        var (items, error) = await client.FindPayersByCustomerAsync(Guid.NewGuid());

        Assert.Null(items);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("9999-12-31T23:59:58.999Z")]
    [InlineData("9999-12-31T23:59:58.999+00:00")]
    public async Task GetMeteringPointsAsync_preserves_historical_single_day_and_current_open_end(string openEnd)
    {
        var json = $$"""
            {
              "Items": [
                {
                  "MeteringPointId": "aaaaaaaa-0000-0000-0000-000000000001",
                  "Start": "2026-08-31T22:00:00Z",
                  "End": "2026-09-01T22:00:00Z"
                },
                {
                  "MeteringPointId": "aaaaaaaa-0000-0000-0000-000000000001",
                  "Start": "2026-09-01T22:00:00Z",
                  "End": "{{openEnd}}"
                }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);
        var payerId = Guid.Parse("818f33e6-184b-4e8c-a0b2-a9ca9517fdc1");

        var (items, error) = await client.GetMeteringPointsAsync(payerId);

        Assert.Null(error);
        Assert.NotNull(items);
        Assert.Equal(2, items.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), items[0].Start);
        Assert.Equal(new DateOnly(2026, 9, 2), items[0].End);
        Assert.Equal(new DateOnly(2026, 9, 2), items[1].Start);
        Assert.Equal(DateOnly.MaxValue, items[1].End);
        Assert.Contains($"/api/v1/Payers/{payerId}/MeteringPoints?Limit=1000", handler.LastUrl);
    }

    private static (SettlPayerClient Client, CapturingHandler Handler) MakeClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseBody, status);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" });
        var client = new SettlPayerClient(
            httpClient,
            new FakeAuthService(),
            options,
            NullLogger<SettlPayerClient>.Instance);
        return (client, handler);
    }

    private sealed class FakeAuthService : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("test-token");

        public void InvalidateToken() { }
    }

    private sealed class CapturingHandler(string responseBody, HttpStatusCode status) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
