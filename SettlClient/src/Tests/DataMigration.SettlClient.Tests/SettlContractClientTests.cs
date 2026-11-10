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

    [Fact]
    public async Task GetCustomerMeteringPointContractsAsync_reads_null_contract_and_link_ends_as_open_ended()
    {
        // Featuretest failed on Items[1].ContractEnd = null: Settl sends null for an open-ended
        // contract, and the per-MP End is nullable in the schema too.
        const string json =
            """
            {
              "Items": [
                {
                  "Id": "d7772d6c-4736-4fd2-a9cf-04963800d51f",
                  "CustomerId": "2b1d6f0e-6a55-4d4b-9a51-6f1b3c9f2a10",
                  "ContractName": "E_Spot_6",
                  "ContractStart": "2025-12-31T23:00:00+00:00",
                  "ContractEnd": "2026-06-30T22:00:00+00:00",
                  "MeteringPoints": [
                    {
                      "MeteringPointId": "79d684f9-cea7-4806-b4e8-504f2b6c15b1",
                      "MeteringPointIdentification": "571313179104952730",
                      "CommodityType": "Electricity",
                      "Start": "2025-12-31T23:00:00+00:00",
                      "End": "2026-06-30T22:00:00+00:00"
                    }
                  ]
                },
                {
                  "Id": "f74c7522-7bdc-4b23-ac35-b58a64eb5d36",
                  "CustomerId": "2b1d6f0e-6a55-4d4b-9a51-6f1b3c9f2a10",
                  "ContractName": "E_VE_Biomasse",
                  "ContractStart": "2026-06-30T22:00:00+00:00",
                  "ContractEnd": null,
                  "MeteringPoints": [
                    {
                      "MeteringPointId": "79d684f9-cea7-4806-b4e8-504f2b6c15b1",
                      "MeteringPointIdentification": "571313179104952730",
                      "CommodityType": "Electricity",
                      "Start": "2026-06-30T22:00:00+00:00",
                      "End": null
                    }
                  ]
                }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);
        var customerId = Guid.Parse("2b1d6f0e-6a55-4d4b-9a51-6f1b3c9f2a10");

        var (contracts, error) = await client.GetCustomerMeteringPointContractsAsync(customerId);

        Assert.Null(error);
        Assert.NotNull(contracts);
        Assert.Equal(2, contracts!.Count);

        Assert.Equal(new DateTimeOffset(2026, 6, 30, 22, 0, 0, TimeSpan.Zero), contracts[0].ContractEnd);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 22, 0, 0, TimeSpan.Zero), contracts[0].MeteringPoints[0].End);

        Assert.Null(contracts[1].ContractEnd);
        Assert.Null(contracts[1].MeteringPoints[0].End);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 22, 0, 0, TimeSpan.Zero), contracts[1].ContractStart);

        Assert.Contains($"/api/v1/contracts/customer/{customerId}/metering-point-contracts", handler.LastUrl);
    }

    [Fact]
    public async Task GetContractMeteringPointLinksAsync_filters_one_contract_by_meteringPointId_and_returns_periods()
    {
        const string json =
            """
            {
              "Items": [
                {
                  "Id": "0f5b8a3e-3c1e-4c7d-8e43-1b0f0b9a7c21",
                  "MeteringPointId": "79d684f9-cea7-4806-b4e8-504f2b6c15b1",
                  "MeteringPointIdentification": "571313179104952730",
                  "Category": "Electricity",
                  "Start": "2025-12-31T23:00:00+00:00",
                  "End": "2026-06-30T22:00:00+00:00"
                }
              ]
            }
            """;
        var (client, handler) = MakeClient(json);
        var contractId = Guid.Parse("d7772d6c-4736-4fd2-a9cf-04963800d51f");
        var meteringPointId = Guid.Parse("79d684f9-cea7-4806-b4e8-504f2b6c15b1");

        var (links, error) = await client.GetContractMeteringPointLinksAsync(contractId, meteringPointId);

        Assert.Null(error);
        Assert.NotNull(links);
        var link = Assert.Single(links!);
        Assert.Equal(Guid.Parse("0f5b8a3e-3c1e-4c7d-8e43-1b0f0b9a7c21"), link.Id);
        Assert.Equal(meteringPointId, link.MeteringPointId);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero), link.Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 22, 0, 0, TimeSpan.Zero), link.End);

        Assert.Contains($"/api/v1/contracts/{contractId}/meteringPoints?", handler.LastUrl);
        Assert.Contains($"MeteringPointId={meteringPointId}", handler.LastUrl);
    }

    [Fact]
    public async Task GetContractMeteringPointLinksAsync_returns_empty_list_when_not_linked()
    {
        var (client, _) = MakeClient("""{ "Items": [] }""");

        var (links, error) = await client.GetContractMeteringPointLinksAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(error);
        Assert.NotNull(links);
        Assert.Empty(links!);
    }

    [Fact]
    public async Task GetContractMeteringPointLinksAsync_returns_error_on_http_failure()
    {
        var (client, _) = MakeClient("nope", HttpStatusCode.InternalServerError);

        var (links, error) = await client.GetContractMeteringPointLinksAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(links);
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
