using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlPriceListClientTests
{
    [Fact]
    public async Task ListPriceListsAsync_deserializes_catalog_and_targets_priceLists_endpoint()
    {
        // Wire shape from featuretest GET /api/v1/priceLists.
        const string json =
            """
            {
              "Items": [
                { "Id": "e2ebc42d-e669-43f8-b548-d134dc77848f", "PriceListIdentifier": "1000", "CountryCode": "DK", "CommodityType": "Electricity", "Name": "ST 0500" }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);

        var (items, error) = await client.ListPriceListsAsync("DK");

        Assert.Null(error);
        Assert.NotNull(items);
        var pl = Assert.Single(items!);
        Assert.Equal(Guid.Parse("e2ebc42d-e669-43f8-b548-d134dc77848f"), pl.Id);
        Assert.Equal("ST 0500", pl.Name);
        Assert.Contains("/api/v1/priceLists", handler.LastUrl);
        Assert.Contains("CountryCode=DK", handler.LastUrl);
    }

    [Fact]
    public async Task ListProductElementsAsync_deserializes_code_and_id()
    {
        const string json =
            """
            {
              "Items": [
                { "Id": "c98f6c85-18aa-49a1-9721-6d88fbde25b7", "ProductElementCode": "El_Forbrug", "CountryCode": "DK" }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);

        var (items, error) = await client.ListProductElementsAsync("DK");

        Assert.Null(error);
        var pe = Assert.Single(items!);
        Assert.Equal(Guid.Parse("c98f6c85-18aa-49a1-9721-6d88fbde25b7"), pe.Id);
        Assert.Equal("El_Forbrug", pe.ProductElementCode);
        Assert.Contains("/api/v1/productElements", handler.LastUrl);
    }

    [Fact]
    public async Task GetCustomerPriceListsAsync_deserializes_association_including_period()
    {
        // Wire shape from featuretest GET /api/v2/customers/{id}/pricelists — note AssociationPeriod,
        // which the published swagger omits but the live API returns.
        const string json =
            """
            {
              "Items": [
                {
                  "PriceListId": "e2ebc42d-e669-43f8-b548-d134dc77848f",
                  "ProductElementId": "c98f6c85-18aa-49a1-9721-6d88fbde25b7",
                  "ContractId": "00000000-0000-0000-0000-000000000000",
                  "MarketBalanceAreaId": "00000000-0000-0000-0000-000000000000",
                  "AssociationPeriod": { "Start": "2022-12-31T23:00:00Z", "End": "2026-12-30T23:00:00Z" },
                  "Id": "b41e7c15-fd0e-4eee-8193-0f201d3dc50f"
                }
              ]
            }
            """;
        var customerId = Guid.Parse("b41e7c15-fd0e-4eee-8193-0f201d3dc50f");
        var (client, handler) = MakeClient(json);

        var (items, error) = await client.GetCustomerPriceListsAsync(customerId);

        Assert.Null(error);
        var a = Assert.Single(items!);
        Assert.Equal(Guid.Parse("e2ebc42d-e669-43f8-b548-d134dc77848f"), a.PriceListId);
        Assert.Equal(Guid.Parse("c98f6c85-18aa-49a1-9721-6d88fbde25b7"), a.ProductElementId);
        Assert.Equal(Guid.Empty, a.ContractId);
        Assert.NotNull(a.AssociationPeriod);
        Assert.Equal(new DateTimeOffset(2022, 12, 31, 23, 0, 0, TimeSpan.Zero), a.AssociationPeriod!.Start);
        Assert.Equal(new DateTimeOffset(2026, 12, 30, 23, 0, 0, TimeSpan.Zero), a.AssociationPeriod!.End);
        Assert.Contains($"/api/v2/customers/{customerId}/pricelists", handler.LastUrl);
    }

    [Fact]
    public async Task GetCustomerPriceListsAsync_returns_empty_when_no_associations()
    {
        var (client, _) = MakeClient("""{ "Items": [] }""");

        var (items, error) = await client.GetCustomerPriceListsAsync(Guid.NewGuid());

        Assert.Null(error);
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task ListPriceListsAsync_returns_error_on_http_failure()
    {
        var (client, _) = MakeClient("nope", HttpStatusCode.InternalServerError);

        var (items, error) = await client.ListPriceListsAsync("DK");

        Assert.Null(items);
        Assert.NotNull(error);
    }

    private static (SettlPriceListClient Client, CapturingHandler Handler) MakeClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseBody, status);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" });
        var client = new SettlPriceListClient(
            httpClient,
            new FakeAuthService(),
            options,
            NullLogger<SettlPriceListClient>.Instance);
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
