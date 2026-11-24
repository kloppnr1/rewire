using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Services;
using System.Text.Json;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// The settlement-config exporter's sources, read through the real code paths: the customer and
/// segment endpoints on the fake Settl tenant, and the Charge_Abo query against an in-memory Xellent.
/// </summary>
public sealed class SettlementConfigSourceIntegrationTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>
{
    [Fact]
    public async Task Current_customer_segment_can_be_read_without_guessing_from_contracts()
    {
        var (customers, _, error) = await fixture.Settl.CustomerClient.FindCustomersAsync(3);
        Assert.Null(error);
        Assert.NotEmpty(customers);
        var (catalog, catalogError) = await fixture.Settl.CustomerClient.FindCustomerSegmentsAsync();
        Assert.Null(catalogError);
        Assert.NotNull(catalog);
        foreach (var customer in customers)
        {
            var (json, readError) = await fixture.Settl.CustomerClient.GetRawByIdV3Async(customer.Id);
            Assert.Null(readError);
            Assert.NotNull(json);
            var value = SettlementConfigReview.ReadCurrentSegment(json, catalog.ToDictionary(s => s.Id, s => s.Name));
            Assert.True(value.Status is "Read" or "NotAssigned", $"Current segment contract returned status {value.Status}");
        }
    }

    [Fact]
    public async Task Raw_customer_shape_exposes_current_segment()
    {
        var (customers, _, error) = await fixture.Settl.CustomerClient.FindCustomersAsync(1);
        Assert.Null(error);
        var (json, readError) = await fixture.Settl.CustomerClient.GetRawByIdV3Async(Assert.Single(customers).Id);
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
        using var xellent = new FakeXellent();
        // 339097 owns a charger until 31 July and rents one from 1 August; 445400 owns one throughout.
        xellent.Add(XellentRows.ChargeSubscription("339097", "C339097A", "OAboEjeMEL", new(2025, 1, 1), new(2026, 7, 31)));
        xellent.Add(XellentRows.ChargeSubscription("339097", "C339097B", "OAboLejMEL", new(2026, 8, 1), XellentRows.Open));
        xellent.Add(XellentRows.ChargeSubscription("445400", "C445400A", "O HCEjeML", new(2025, 1, 1), XellentRows.Open));
        xellent.Add(XellentRows.ChargeSubscription("445400", "C445400B", "O HCEjeML", new(2025, 6, 1), XellentRows.Open));

        (string Account, DateOnly Date)[] requests = [("339097",new(2026,8,31)), ("339097",new(2026,7,31)), ("445400",new(2026,8,31))];
        var rows = await XellentChargeSubscriptionReader.ReadAsync(xellent.ConnectionString, requests,"hol");
        Assert.All(rows.Keys, key => Assert.Contains(key, requests));
        Assert.All(rows.Values, products => Assert.Equal(products.Count,products.Distinct(StringComparer.OrdinalIgnoreCase).Count()));

        // Account and date stay paired: the July owner is not pooled with the August renter.
        Assert.Equal(["OAboLejMEL"], rows[("339097", new(2026, 8, 31))]);
        Assert.Equal(["OAboEjeMEL"], rows[("339097", new(2026, 7, 31))]);
        Assert.Equal(["O HCEjeML"], rows[("445400", new(2026, 8, 31))]);
    }
}
