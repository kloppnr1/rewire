using System.Net;
using System.Text;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlClient.Tests;

public class SettlMeteringPointClientBillingTests
{
    [Fact]
    public async Task UpdateBillingAsync_serializes_BuyerReference_as_camelCase_wire_field()
    {
        var (client, handler) = MakeClient();
        var request = new UpdateBillingMeteringPointRequest
        {
            PaymentConfiguration = new MeteringPointPaymentConfigurationDto
            {
                Start = new DateOnly(2026, 1, 1),
                OrderIdentifier = "ORD-1",
                BuyerReference = "ACME Procurement"
            }
        };

        var (success, error) = await client.UpdateBillingAsync(Guid.NewGuid(), request);

        Assert.True(success, error);
        // CamelCaseJsonOptions → PascalCase property serializes to camelCase wire field,
        // matching the existing siblings (orderIdentifier) the Settl billing API accepts.
        Assert.Contains("\"buyerReference\":\"ACME Procurement\"", handler.LastBody);
        Assert.DoesNotContain("BuyerReference", handler.LastBody);
    }

    [Fact]
    public async Task GetBillingByIdAsync_reads_PascalCase_BuyerReference_from_response()
    {
        const string json =
            """
            { "PaymentConfiguration": { "BuyerReference": "ACME Procurement", "OrderIdentifier": "ORD-1" } }
            """;
        var (client, _) = MakeClient(json);

        var (billing, error) = await client.GetBillingByIdAsync(Guid.NewGuid());

        Assert.Null(error);
        Assert.Equal("ACME Procurement", billing!.PaymentConfiguration!.BuyerReference);
    }

    [Fact]
    public async Task BlankDuplicateSecondConsumerNameAsync_blanks_when_names_match()
    {
        const string json =
            """
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Consumer": {
                "Start": "2026-01-01T23:00:00Z",
                "FirstConsumerParty": {
                  "Name": { "FullName": "Jens Jensen", "FirstName": "Jens", "LastName": "Jensen" },
                  "VatIdentificationNumber": null,
                  "NationalIdentificationNumber": "1111111111"
                },
                "SecondConsumerParty": {
                  "Name": { "FullName": "Jens Jensen", "FirstName": "Jens", "LastName": "Jensen" },
                  "VatIdentificationNumber": "DK12345678",
                  "NationalIdentificationNumber": "2222222222"
                }
              },
              "MeterCharacteristics": { "MeteringPointType": "E17" }
            }
            """;
        var (client, handler) = MakeClient(json);
        var meteringPointId = Guid.NewGuid();

        var (success, error) = await client.BlankDuplicateSecondConsumerNameAsync(meteringPointId);

        Assert.True(success, error);
        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.LastBody)!;
        var patch = body["MeteringPointPatches"]![0]!;
        Assert.Equal(meteringPointId.ToString(), (string?)patch["Id"]);
        // PatchTime is round-tripped from the existing Consumer.Start — anchors the PATCH to the
        // current historical entry instead of opening a new one.
        Assert.Equal("2026-01-01T23:00:00Z", (string?)patch["PatchTime"]);
        var second = patch["ConsumerPatch"]!["SecondConsumerParty"]!;
        Assert.Null((string?)second["Name"]!["FullName"]);
        Assert.Null((string?)second["Name"]!["FirstName"]);
        Assert.Null((string?)second["Name"]!["LastName"]);
        // NationalIdentificationNumber ignores null on this endpoint — empty string is the
        // sentinel that actually clears it (confirmed against featuretest 2026-09-09).
        Assert.Equal(string.Empty, (string?)second["NationalIdentificationNumber"]);
        // Nothing outside SecondConsumerParty is sent — FirstConsumerParty is untouched.
        Assert.Null(patch["ConsumerPatch"]!["FirstConsumerParty"]);
    }

    [Fact]
    public async Task BlankDuplicateSecondConsumerNameAsync_is_noop_when_names_differ()
    {
        const string json =
            """
            {
              "Consumer": {
                "FirstConsumerParty": { "Name": { "FullName": "Jens Jensen", "FirstName": "Jens", "LastName": "Jensen" } },
                "SecondConsumerParty": { "Name": { "FullName": "Mette Hansen", "FirstName": "Mette", "LastName": "Hansen" }, "NationalIdentificationNumber": "2222222222" }
              }
            }
            """;
        var (client, handler) = MakeClient(json);

        var (success, error) = await client.BlankDuplicateSecondConsumerNameAsync(Guid.NewGuid());

        Assert.True(success, error);
        Assert.Equal(string.Empty, handler.LastBody); // no PUT sent — no-op
    }

    [Fact]
    public async Task GetByIdV4Async_reads_camelCase_consumer_party_NationalIdentificationNumber()
    {
        const string json =
            """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "consumer": {
                "firstConsumerParty": { "name": { "fullName": "Test Person" }, "nationalIdentificationNumber": null },
                "secondConsumerParty": { "name": null, "nationalIdentificationNumber": "0101011234" }
              }
            }
            """;
        var (client, _) = MakeClient(json);

        var (meteringPoint, error) = await client.GetByIdV4Async(Guid.NewGuid());

        Assert.Null(error);
        Assert.Null(meteringPoint!.Consumer!.FirstConsumerParty!.NationalIdentificationNumber);
        Assert.Null(meteringPoint.Consumer.SecondConsumerParty!.Name);
        Assert.Equal("0101011234", meteringPoint.Consumer.SecondConsumerParty.NationalIdentificationNumber);
    }

    [Fact]
    public async Task GetConsumerNationalIdsAsync_calls_subresource_and_reads_both_ids()
    {
        const string json =
            """
            {
              "firstConsumerPartyNationalIdentificationNumber": "0101011111",
              "secondConsumerPartyNationalIdentificationNumber": "0202022222"
            }
            """;
        var (client, handler) = MakeClient(json);
        var effectiveDate = new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc);

        var (ids, error) = await client.GetConsumerNationalIdsAsync(Guid.NewGuid(), effectiveDate);

        Assert.Null(error);
        Assert.EndsWith("/nationalIdentificationNumber", handler.LastRequestUri!.AbsolutePath);
        Assert.Equal("?EffectiveDate=2026-06-30T22:00:00Z", handler.LastRequestUri.Query);
        Assert.Equal("0101011111", ids!.FirstConsumerPartyNationalIdentificationNumber);
        Assert.Equal("0202022222", ids.SecondConsumerPartyNationalIdentificationNumber);
    }

    private static (SettlMeteringPointClient Client, CapturingHandler Handler) MakeClient(
        string responseBody = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseBody, status);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://test.example" });
        var client = new SettlMeteringPointClient(
            httpClient,
            new FakeAuthService(),
            options,
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
        public string LastBody { get; private set; } = string.Empty;
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
