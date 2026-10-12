using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlContractClientTests
{
    [Fact]
    public async Task FindContractsByMeteringPointAsync_filters_by_meteringPointId_and_returns_items()
    {
        const string json =
            """
            {
              "Items": [
                { "Id": "d7772d6c-4736-4fd2-a9cf-04963800d51f", "ContractName": "E_Spot_6", "CommodityType": "Electricity" },
                { "Id": "f74c7522-7bdc-4b23-ac35-b58a64eb5d36", "ContractName": "E_VE_Biomasse", "CommodityType": "Electricity" }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);
        var meteringPointId = Guid.Parse("79d684f9-cea7-4806-b4e8-504f2b6c15b1");

        var (contracts, error) = await client.FindContractsByMeteringPointAsync(meteringPointId);

        Assert.Null(error);
        Assert.NotNull(contracts);
        Assert.Equal(2, contracts!.Count);
        Assert.Contains(contracts, c => c.ContractName == "E_Spot_6");

        Assert.Contains("/api/v1/Contracts", handler.LastUrl);
        Assert.Contains($"MeteringPointId={meteringPointId}", handler.LastUrl);
    }

    [Fact]
    public async Task FindContractsByMeteringPointAsync_returns_empty_list_when_no_matches()
    {
        var (client, _) = MakeClient("""{ "Items": [] }""");

        var (contracts, error) = await client.FindContractsByMeteringPointAsync(Guid.NewGuid());

        Assert.Null(error);
        Assert.NotNull(contracts);
        Assert.Empty(contracts!);
    }

    [Fact]
    public async Task FindContractsByMeteringPointAsync_returns_error_on_http_failure()
    {
        var (client, _) = MakeClient("nope", HttpStatusCode.InternalServerError);

        var (contracts, error) = await client.FindContractsByMeteringPointAsync(Guid.NewGuid());

        Assert.Null(contracts);
        Assert.NotNull(error);
    }

    private static (SettlContractClient Client, CapturingHandler Handler) MakeClient(
        string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseBody, status);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" });
        var client = new SettlContractClient(
            httpClient,
            new FakeAuthService(),
            options,
            NullLogger<SettlContractClient>.Instance);
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
