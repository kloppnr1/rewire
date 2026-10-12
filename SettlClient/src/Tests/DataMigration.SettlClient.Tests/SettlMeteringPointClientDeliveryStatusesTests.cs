using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlMeteringPointClientDeliveryStatusesTests
{
    [Fact]
    public async Task GetDeliveryStatusesAsync_uses_market_v3_url_and_deserializes_statuses()
    {
        const string json =
            """
            {
              "id": "79d684f9-cea7-4806-b4e8-504f2b6c15b1",
              "externalIdentifier": "ext-123",
              "uri": "/api/v3/meteringPoints/79d684f9-cea7-4806-b4e8-504f2b6c15b1",
              "links": [
                { "relation": "self", "uri": "/api/v3/meteringPoints/79d684f9-cea7-4806-b4e8-504f2b6c15b1/deliveryStatuses" }
              ],
              "deliveryStatuses": [
                {
                  "inDelivery": true,
                  "period": {
                    "start": "2025-01-01T00:00:00+01:00",
                    "end": "2025-06-01T00:00:00+02:00"
                  },
                  "startReason": "MoveIn",
                  "stopReason": "ChangeOfSupplier"
                },
                {
                  "inDelivery": false,
                  "period": null,
                  "startReason": "Unknown",
                  "stopReason": "MoveOut"
                }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);
        var meteringPointId = Guid.Parse("79d684f9-cea7-4806-b4e8-504f2b6c15b1");

        var (response, error) = await client.GetDeliveryStatusesAsync(meteringPointId);

        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(
            $"https://test.example/internal-market/api/v3/meteringPoints/{meteringPointId}/deliveryStatuses",
            handler.LastUrl);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("ext-123", response!.ExternalIdentifier);
        Assert.Single(response.Links!);
        Assert.Equal(2, response.DeliveryStatuses!.Count);
        Assert.Equal(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)),
            response.DeliveryStatuses[0].Period!.Start);
        Assert.True(response.DeliveryStatuses[0].InDelivery);
        Assert.Equal(DeliveryStartReason.MoveIn, response.DeliveryStatuses[0].StartReason);
        Assert.Equal(DeliveryStopReason.ChangeOfSupplier, response.DeliveryStatuses[0].StopReason);
        Assert.Null(response.DeliveryStatuses[1].Period);
    }

    [Fact]
    public async Task GetDeliveryStatusesAsync_returns_error_on_http_failure()
    {
        var (client, _) = MakeClient("delivery status failure", HttpStatusCode.BadGateway);

        var (response, error) = await client.GetDeliveryStatusesAsync(Guid.NewGuid());

        Assert.Null(response);
        Assert.Equal("HTTP 502: delivery status failure", error);
    }

    [Fact]
    public async Task GetDeliveryStatusesRawAsync_returns_the_unchanged_response_body()
    {
        const string json = """{"deliveryStatuses":[{"inDelivery":true,"unknownField":"kept"}]}""";
        var (client, handler) = MakeClient(json);
        var meteringPointId = Guid.Parse("79d684f9-cea7-4806-b4e8-504f2b6c15b1");

        var (raw, error) = await client.GetDeliveryStatusesRawAsync(meteringPointId);

        Assert.Null(error);
        Assert.Equal(json, raw);
        Assert.Equal(
            $"https://test.example/internal-market/api/v3/meteringPoints/{meteringPointId}/deliveryStatuses",
            handler.LastUrl);
    }

    private static (SettlMeteringPointClient Client, CapturingHandler Handler) MakeClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseBody, status);
        var client = new SettlMeteringPointClient(
            new HttpClient(handler),
            new FakeAuthService(),
            Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" }),
            NullLogger<SettlMeteringPointClient>.Instance);
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
        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            LastMethod = request.Method;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
