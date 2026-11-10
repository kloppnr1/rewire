using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class PayerBillingDiffTests
{
    private static ReportPayersRunner Runner(FakePayerClient payerClient)
        => new(customerClient: null!, payerClient);

    private static PayerInfoDto Payer(string id, string? ean = null)
        => new() { Id = Guid.NewGuid(), PayerIdentification = id, ElectronicNumber = ean };

    private static Dictionary<string, BuyerInvoicingReferences> Expected(params (string id, BuyerInvoicingReferences refs)[] e)
        => e.ToDictionary(x => x.id, x => x.refs, StringComparer.Ordinal);

    [Fact]
    public async Task Ean_only_expectation_diffs_without_raw_fetch()
    {
        var fake = new FakePayerClient();
        var payers = new List<PayerInfoDto> { Payer("100", ean: null) };
        var expected = Expected(("100", new BuyerInvoicingReferences(ElectronicNumber: "5790001234567")));

        var rows = await Runner(fake).DiffBillingAsync(payers, expected, showAll: false);

        var row = Assert.Single(rows);
        Assert.Equal("ElectronicNumber", row.Årsager);
        Assert.Contains("EAN=''→'5790001234567'(Backfill)", row.Detalje);
        Assert.Equal(0, fake.RawFetches); // EAN comes from the paginated DTO — no detail GET
    }

    [Fact]
    public async Task Identifier_expectation_triggers_single_raw_fetch_and_diffs()
    {
        var fake = new FakePayerClient(rawJson: """{ "PayerBilling": { "BuyerReferenceIdentifier": null, "OrderIdentifier": "ORD-1" } }""");
        var payers = new List<PayerInfoDto> { Payer("100") };
        var expected = Expected(("100", new BuyerInvoicingReferences(
            OrderIdentifier: "ORD-1", BuyerReferenceIdentifier: "ACME Procurement")));

        var rows = await Runner(fake).DiffBillingAsync(payers, expected, showAll: false);

        var row = Assert.Single(rows);
        Assert.Equal(1, fake.RawFetches);
        Assert.Equal("BuyerReference", row.Årsager);                       // OrderId matches → only BuyerRef drifts
        Assert.Contains("BuyerRef=''→'ACME Procurement'(Backfill)", row.Detalje);
        Assert.DoesNotContain("OrderId=", row.Detalje);
    }

    [Fact]
    public async Task Payer_without_expectation_is_skipped()
    {
        var fake = new FakePayerClient();
        var payers = new List<PayerInfoDto> { Payer("999", ean: "5790009999999") };

        var rows = await Runner(fake).DiffBillingAsync(payers, Expected(), showAll: true);

        Assert.Empty(rows);
        Assert.Equal(0, fake.RawFetches);
    }

    [Fact]
    public async Task Matching_values_yield_no_row_by_default()
    {
        var fake = new FakePayerClient(rawJson: """{ "PayerBilling": { "OrderIdentifier": "ORD-1" } }""");
        var payers = new List<PayerInfoDto> { Payer("100", ean: "5790001234567") };
        var expected = Expected(("100", new BuyerInvoicingReferences(
            ElectronicNumber: "5790001234567", OrderIdentifier: "ORD-1")));

        var rows = await Runner(fake).DiffBillingAsync(payers, expected, showAll: false);

        Assert.Empty(rows);
    }

    private sealed class FakePayerClient(string rawJson = "{ \"PayerBilling\": {} }") : ISettlPayerClient
    {
        public int RawFetches { get; private set; }

        public Task<(string? Json, string? Error)> GetRawByIdAsync(Guid payerId, CancellationToken ct = default)
        {
            RawFetches++;
            return Task.FromResult<(string?, string?)>((rawJson, null));
        }

        public Task<(bool Success, string? Error)> CreateAsync(CreatePayersRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(string payerIdentification, string countryCode = "DK", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(Guid payerId, DateTime? effectiveDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> UpdateRawAsync(Guid payerId, string updateRequestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> UpdatePayerBillingAsync(Guid payerId, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> billingPatches, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> DeleteAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(Guid customerId, string countryCode = "DK", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
