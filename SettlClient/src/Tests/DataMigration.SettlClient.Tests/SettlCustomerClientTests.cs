using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlCustomerClientTests
{
    [Fact]
    public async Task SearchCustomersAsync_builds_fulltext_query_and_returns_items()
    {
        const string json =
            """
            {
              "Items": [
                { "Id": "aaaaaaaa-0000-0000-0000-000000000001", "CustomerNumber": "100200", "FullName": "Acme A/S", "City": "Randers" }
              ],
              "CountResult": { "Count": 1 }
            }
            """;
        var (client, handler) = MakeClient(json);

        var (items, total, error) = await client.SearchCustomersAsync(fullText: "Acme Randers");

        Assert.Null(error);
        Assert.Single(items);
        Assert.Equal(1, total);
        Assert.Equal("Acme A/S", items[0].FullName);
        Assert.Contains("/api/v2/customers", handler.LastUrl);
        Assert.Contains("FullTextSearch=Acme", handler.LastUrl);
        Assert.Contains("DoCount=true", handler.LastUrl);
    }

    [Fact]
    public async Task SearchCustomersAsync_includes_name_and_cvr_filters_when_supplied()
    {
        var (client, handler) = MakeClient("""{ "Items": [] }""");

        await client.SearchCustomersAsync(name: "Acme", vatIdentificationNumber: "36665030");

        Assert.Contains("Name=Acme", handler.LastUrl);
        Assert.Contains("VatIdentificationNumber=36665030", handler.LastUrl);
    }

    [Fact]
    public async Task SearchCustomersAsync_returns_error_on_http_failure()
    {
        var (client, _) = MakeClient("nope", HttpStatusCode.InternalServerError);

        var (items, _, error) = await client.SearchCustomersAsync(fullText: "x");

        Assert.Empty(items);
        Assert.NotNull(error);
    }

    private static (SettlCustomerClient Client, CapturingHandler Handler) MakeClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseBody, status);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" });
        var client = new SettlCustomerClient(
            httpClient,
            new FakeAuthService(),
            options,
            NullLogger<SettlCustomerClient>.Instance);
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
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
