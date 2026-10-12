using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Services;
using System.Text.Json;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Read-only verification of the exporter sources; no provisioning or mutations.</summary>
public sealed class SettlementConfigSourceIntegrationTests(SettlFeaturetestFixture fixture, ITestOutputHelper output)
    : IClassFixture<SettlFeaturetestFixture>
{
    [Fact]
    public async Task Current_customer_segment_can_be_read_without_guessing_from_contracts()
    {
        var (customers, _, error) = await fixture.CustomerClient.FindCustomersAsync(3);
        Assert.Null(error);
        Assert.NotEmpty(customers);
        var (catalog, catalogError) = await fixture.CustomerClient.FindCustomerSegmentsAsync();
        Assert.Null(catalogError);
        Assert.NotNull(catalog);
        foreach (var customer in customers)
        {
            var (json, readError) = await fixture.CustomerClient.GetRawByIdV3Async(customer.Id);
            Assert.Null(readError);
            Assert.NotNull(json);
            var value = SettlementConfigReview.ReadCurrentSegment(json, catalog.ToDictionary(s => s.Id, s => s.Name));
            Assert.True(value.Status is "Read" or "NotAssigned", $"Current segment contract returned status {value.Status}");
        }
    }

    [Fact]
    public async Task Raw_customer_shape_exposes_current_segment()
    {
        var (customers, _, error) = await fixture.CustomerClient.FindCustomersAsync(1);
        Assert.Null(error);
        var (json, readError) = await fixture.CustomerClient.GetRawByIdV3Async(Assert.Single(customers).Id);
        Assert.Null(readError);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        output.WriteLine("Customer property names: " + string.Join(",",doc.RootElement.EnumerateObject().Select(p => p.Name)));
        var value = SettlementConfigReview.ReadCurrentSegment(json, new Dictionary<Guid,string?>());
        Assert.True(value.Status is "Read" or "NotAssigned" or "Unmapped", $"Raw segment field status: {value.Status}");
    }

    [Fact]
    public async Task Charge_Abo_query_preserves_account_and_reference_date()
    {
        (string Account, DateOnly Date)[] requests = [("339097",new(2026,8,31)), ("339097",new(2026,7,31)), ("445400",new(2026,8,31))];
        var rows = await XellentChargeSubscriptionReader.ReadAsync(fixture.XellentConnectionString, requests,"hol");
        Assert.All(rows.Keys, key => Assert.Contains(key, requests));
        Assert.All(rows.Values, products => Assert.Equal(products.Count,products.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }
}
