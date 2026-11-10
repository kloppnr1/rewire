using System.Text.Json.Nodes;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// <c>export-active-customers</c> against a fake Settl tenant filled by the real <c>create</c>
/// (<see cref="ProvisioningFixture"/>): the export must give back what the migration wrote, for
/// customers linked to an electricity metering point in delivery, and nothing else.
///
/// <para>Basis for the nightly Settl export to SonWin (plans/settl-export-sonwin.md).</para>
/// </summary>
public class ExportActiveCustomersFlowTests : IClassFixture<ProvisioningFixture>
{
    private readonly ProvisioningFixture fixture;

    public ExportActiveCustomersFlowTests(ProvisioningFixture fixture)
    {
        this.fixture = fixture;
        StartDelivery(fixture);
    }

    /// <summary>
    /// <c>create</c> writes every metering point with <c>InDelivery=false</c>; Settl turns it on when
    /// DataHub confirms the change of supplier. This plays that confirmation for what create wrote.
    /// </summary>
    private static void StartDelivery(ProvisioningFixture tenant)
    {
        foreach (var mp in tenant.Settl.Api.MeteringPoints.Values)
            if (mp.Market["DeliveryStatus"] is JsonObject status) status["InDelivery"] = true;
    }
    [Fact]
    public async Task Raw_export_carries_what_create_wrote()
    {
        var (exitCode, document) = await ExportAsync(fixture, raw: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(fixture.Settl.Api.UnknownRoutes.Distinct());

        var customer = Assert.Single(document["customers"]!.AsArray())!;
        var customerId = Guid.Parse((string)customer["customerId"]!);
        Assert.True(fixture.Settl.Api.Customers.ContainsKey(customerId));
        Assert.Empty(customer["errors"]!.AsArray());
        Assert.NotNull(customer["customer"]);
        Assert.NotNull(customer["customerV2"]);

        // Every electricity metering point the customer holds and that is in delivery, each with
        // market data, billing configuration and delivery status.
        var expectedMps = fixture.Settl.Api.CustomerMpLinks[customerId]
            .Select(l => Guid.Parse((string)l["MeteringPointId"]!))
            .Where(id => fixture.Settl.Api.MeteringPoints[id].InDelivery)
            .ToHashSet();
        Assert.NotEmpty(expectedMps);
        var exportedMps = customer["meteringPoints"]!.AsArray();
        Assert.Equal(expectedMps, exportedMps.Select(m => Guid.Parse((string)m!["meteringPointId"]!)).ToHashSet());
        foreach (var mp in exportedMps)
        {
            Assert.True(mp!["market"]?["DeliveryStatus"]?["InDelivery"]?.GetValue<bool>());
            Assert.NotNull(mp["billing"]?["BillingConfiguration"]);
            Assert.NotEmpty(mp["deliveryStatuses"]!["DeliveryStatuses"]!.AsArray());
        }

        // Payers, with their links to those metering points.
        var payers = customer["payers"]!.AsArray();
        Assert.NotEmpty(payers);
        Assert.All(payers, p => Assert.NotNull(p!["payer"]));
        Assert.Equal(expectedMps, payers
            .SelectMany(p => p!["meteringPointLinks"]!.AsArray())
            .Select(l => Guid.Parse((string)l!["MeteringPointId"]!))
            .ToHashSet());

        // Contracts: create linked the account's contracts from their contract part start, which in
        // this fixture is in the future. The export keeps current state only, so a contract whose
        // links all start after today is left out.
        Assert.NotEmpty(fixture.Settl.Api.Contracts.SelectMany(c => c.Links));
        Assert.All(fixture.Settl.Api.Contracts.SelectMany(c => c.Links),
            l => Assert.True(DateTime.Parse((string)l["Start"]!).ToUniversalTime().Date > DateTime.UtcNow.Date));
        Assert.Empty(customer["contracts"]!.AsArray());
    }

    [Fact]
    public async Task Projected_export_uses_the_metering_point_identification()
    {
        var (exitCode, document) = await ExportAsync(fixture, raw: false);

        Assert.Equal(0, exitCode);
        var customer = Assert.Single(document["customers"]!.AsArray())!;
        var customerId = fixture.Settl.Api.Customers.Keys.Single();
        Assert.Equal((string?)fixture.Settl.Api.Customers[customerId]["CustomerNumber"], (string?)customer["customerNumber"]);

        // The 18-digit identification, not the Settl GUID, on metering points and on payer links.
        var identifications = fixture.Settl.Api.CustomerMpLinks[customerId]
            .Select(l => fixture.Settl.Api.MeteringPoints[Guid.Parse((string)l["MeteringPointId"]!)])
            .Where(mp => mp.InDelivery)
            .Select(mp => mp.Identification!)
            .ToHashSet();
        Assert.All(identifications, id => Assert.Matches("^[0-9]{18}$", id));
        Assert.Equal(identifications, customer["meteringPoints"]!.AsArray()
            .Select(m => (string)m!["meteringPointId"]!).ToHashSet());
        Assert.Equal(identifications, customer["payers"]!.AsArray()
            .SelectMany(p => p!["meteringPoints"]!.AsArray())
            .Select(n => (string)n!).ToHashSet());
    }

    [Fact]
    public async Task Metering_point_out_of_delivery_and_customer_without_one_are_left_out()
    {
        // A fresh tenant: this test adds data the other tests must not see.
        var tenant = new ProvisioningFixture();
        await tenant.InitializeAsync();
        StartDelivery(tenant);
        try
        {
            var customerId = tenant.Settl.Api.Customers.Keys.Single();

            // A second electricity metering point on the same customer, not in delivery.
            var outOfDelivery = await tenant.Settl.SeedMeteringPointAsync("571313100000000017", ProvisioningFixture.Cos, inDelivery: false);
            await LinkAsync(tenant, customerId, outOfDelivery);

            // A second customer holding only a metering point that is not in delivery.
            var idleCustomer = Guid.NewGuid();
            tenant.Settl.Api.Customers[idleCustomer] = new JsonObject { ["Id"] = idleCustomer, ["CustomerNumber"] = "999999" };
            await LinkAsync(tenant, idleCustomer,
                await tenant.Settl.SeedMeteringPointAsync("571313100000000024", ProvisioningFixture.Cos, inDelivery: false));

            // A contract linked today to the metering point in delivery is exported with that link only.
            var inDeliveryMp = tenant.Settl.Api.CustomerMpLinks[customerId]
                .Select(l => Guid.Parse((string)l["MeteringPointId"]!))
                .First(id => tenant.Settl.Api.MeteringPoints[id].InDelivery);
            var current = new FakeSettlApi.FakeContract(Guid.NewGuid(), "E_CURRENT", "Electricity");
            current.Links.Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["MeteringPointId"] = inDeliveryMp.ToString(),
                ["Start"] = "2020-01-01T00:00:00Z",
                ["End"] = "9999-12-31T23:59:58.999Z",
            });
            current.Links.Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["MeteringPointId"] = outOfDelivery.ToString(),
                ["Start"] = "2020-01-01T00:00:00Z",
                ["End"] = "9999-12-31T23:59:58.999Z",
            });
            tenant.Settl.Api.Contracts.Add(current);

            // Price lists and extra invoice lines come through as Settl holds them.
            tenant.Settl.Api.CustomerPriceLists[customerId] = [new JsonObject { ["PriceListIdentifier"] = "E_TEST" }];
            tenant.Settl.Api.ExtraInvoiceLines[customerId] = [new JsonObject { ["Text"] = "Test line" }];

            var (exitCode, document) = await ExportAsync(tenant, raw: true);

            Assert.Equal(0, exitCode);
            var customer = Assert.Single(document["customers"]!.AsArray())!;
            Assert.Equal(customerId, Guid.Parse((string)customer["customerId"]!));

            var ids = customer["meteringPoints"]!.AsArray().Select(m => Guid.Parse((string)m!["meteringPointId"]!)).ToList();
            Assert.DoesNotContain(outOfDelivery, ids);
            Assert.DoesNotContain(customer["meteringPointLinks"]!.AsArray(),
                l => Guid.Parse((string)l!["MeteringPointId"]!) == outOfDelivery);

            var contract = Assert.Single(customer["contracts"]!.AsArray())!;
            Assert.Equal(current.Id, Guid.Parse((string)contract["Id"]!));
            Assert.Equal(inDeliveryMp, Guid.Parse((string)Assert.Single(contract["MeteringPoints"]!.AsArray())!["MeteringPointId"]!));

            Assert.Equal("E_TEST", (string?)customer["priceLists"]?["CustomerPriceListAssociations"]?[0]?["PriceListIdentifier"]);
            Assert.Equal("Test line", (string?)customer["extraInvoiceLines"]?[0]?["Text"]);
        }
        finally
        {
            await tenant.DisposeAsync();
        }
    }

    private static async Task LinkAsync(ProvisioningFixture tenant, Guid customerId, Guid meteringPointId)
    {
        var (ok, error) = await tenant.Settl.CustomerClient.AddMeteringPointsAsync(customerId, new AddCustomerMeteringPointsRequest
        {
            CustomerMeteringPoints = [new() { MeteringPointId = meteringPointId, Start = ProvisioningFixture.Cos.AddDays(-1) }],
        });
        Assert.True(ok, error);
    }

    private static async Task<(int ExitCode, JsonObject Document)> ExportAsync(ProvisioningFixture tenant, bool raw)
    {
        var path = Path.Combine(Path.GetTempPath(), $"export-active-customers-{Guid.NewGuid():N}.json");
        try
        {
            var settl = tenant.Settl;
            var log = new List<string>();
            var runner = new ExportActiveCustomersRunner(
                settl.RawReader, settl.MeteringPointClient, settl.CustomerLinkLookup, settl.CustomerClient, log: log.Add);
            var exitCode = await runner.RunAsync(new ExportActiveCustomersRunOptions
            {
                OutputPath = path,
                Raw = raw,
                Parallelism = 2,
            });
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            if (document["customers"] is not JsonArray { Count: > 0 })
                throw new Xunit.Sdk.XunitException(
                    $"No customers exported (exit {exitCode}). Log: {string.Join(" | ", log)}. " +
                    $"Unknown fake routes: {string.Join(", ", settl.Api.UnknownRoutes.Distinct())}");
            return (exitCode, document);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
