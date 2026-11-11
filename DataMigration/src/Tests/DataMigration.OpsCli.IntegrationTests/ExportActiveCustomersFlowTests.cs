using System.Text.Json.Nodes;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
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
        // this fixture is after today. Links that start later are kept, so SonWin sees agreed changes.
        var futureLinks = fixture.Settl.Api.Contracts.SelectMany(c => c.Links).ToList();
        Assert.NotEmpty(futureLinks);
        Assert.All(futureLinks,
            l => Assert.True(DateTime.Parse((string)l["Start"]!).ToUniversalTime().Date > DateTime.UtcNow.Date));
        var expectedContracts = fixture.Settl.Api.Contracts
            .Where(c => c.Links.Any(l => expectedMps.Contains(Guid.Parse((string)l["MeteringPointId"]!))))
            .Select(c => c.Id)
            .ToHashSet();
        Assert.Equal(expectedContracts, customer["contracts"]!.AsArray()
            .Select(c => Guid.Parse((string)c!["Id"]!))
            .ToHashSet());
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
            // An ended link on the same metering point is history and left out.
            current.Links.Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString(),
                ["MeteringPointId"] = inDeliveryMp.ToString(),
                ["Start"] = "2018-01-01T00:00:00Z",
                ["End"] = "2019-12-31T23:00:00Z",
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

            var contract = Assert.Single(customer["contracts"]!.AsArray(), c => Guid.Parse((string)c!["Id"]!) == current.Id)!;
            var link = Assert.Single(contract["MeteringPoints"]!.AsArray())!;
            Assert.Equal(inDeliveryMp, Guid.Parse((string)link["MeteringPointId"]!));
            Assert.StartsWith("2020-01-01", (string)link["Start"]!);

            Assert.Equal("E_TEST", (string?)customer["priceLists"]?["CustomerPriceListAssociations"]?[0]?["PriceListIdentifier"]);
            Assert.Equal("Test line", (string?)customer["extraInvoiceLines"]?[0]?["Text"]);
        }
        finally
        {
            await tenant.DisposeAsync();
        }
    }

    [Fact]
    public async Task Projected_export_carries_the_business_example_fields()
    {
        var tenant = new ProvisioningFixture();
        await tenant.InitializeAsync();
        StartDelivery(tenant);
        try
        {
            var api = tenant.Settl.Api;
            var customerId = api.Customers.Keys.Single();
            var mpId = api.CustomerMpLinks[customerId].Select(l => Guid.Parse((string)l["MeteringPointId"]!)).First();
            var mp = api.MeteringPoints[mpId];

            // Reference data the export resolves ids against.
            var segment = Guid.NewGuid();
            api.CustomerSegments.Add(new JsonObject { ["Id"] = segment.ToString(), ["Name"] = "Erhverv" });
            api.Customers[customerId]["CustomerSegmentId"] = segment.ToString();
            api.Customers[customerId]["CreditInfo"] = new JsonObject { ["ManualApprovalRequired"] = "Yes" };
            var priceList = Guid.NewGuid();
            var element = Guid.NewGuid();
            api.PriceLists.Add(new JsonObject { ["Id"] = priceList.ToString(), ["PriceListIdentifier"] = "E_KAM_Spot" });
            api.ProductElements.Add(new JsonObject { ["Id"] = element.ToString(), ["ProductElementCode"] = "E_KAM_Tillaeg" });
            var area = api.MarketBalanceAreas[0];
            mp.Market["Grid"] = new JsonObject { ["MarketBalanceAreaId"] = area.Id.ToString() };

            // Metering point fields from the business example.
            mp.Market["Consumer"] = new JsonObject { ["ConsumerCategory"] = "Erhverv", ["ElectricalHeating"] = false };
            mp.Market["Notes"] = new JsonObject { ["Remark"] = "Test remark" };
            mp.SettlementInfo = new JsonObject { ["IgnoreInBilling"] = false, ["ExemptFromVat"] = true };

            // A customer price list valid from Danish midnight 1 January 2023.
            api.CustomerPriceLists[customerId] =
            [
                new JsonObject
                {
                    ["PriceListId"] = priceList.ToString(),
                    ["ProductElementId"] = element.ToString(),
                    ["MarketBalanceAreaId"] = area.Id.ToString(),
                    ["AssociationPeriod"] = new JsonObject { ["Start"] = "2022-12-31T23:00:00Z", ["End"] = "9999-12-31T23:59:58.999Z" },
                },
            ];

            // The contract create linked: master data and settlement terms, a current line and an
            // ended one that is left out.
            var contract = api.Contracts.First(c => c.Links.Any(l => (string?)l["MeteringPointId"] == mpId.ToString()));
            contract.Fields["OrderNumber"] = "4711";
            contract.Fields["ContractCategory"] = "Erhverv";
            contract.Fields["TypeOfContractElements"] = "Electricity";
            contract.Fields["QuantityDistributionType"] = "Profile";
            contract.Fields["FinalSettlementInfo"] = new JsonObject
            {
                ["SettlementFrequency"] = "Quarterly", ["PaymentTerms"] = 30, ["PaymentTermsOffset"] = "Days",
            };
            contract.Lines.Add(new JsonObject
            {
                ["Start"] = "2025-12-31T23:00:00Z", ["End"] = "9999-12-31T23:59:58.999Z",
                ["Text"] = "Spot", ["ProductElementCode"] = "E_KAM_Spot", ["ProductName"] = "E_Spot",
                ["Coverage"] = 100, ["Category"] = "Spot", ["ProfitSplit"] = 0, ["VolumeFlexibility"] = 0,
            });
            contract.Lines.Add(new JsonObject
            {
                ["Start"] = "2020-12-31T23:00:00Z", ["End"] = "2021-12-31T23:00:00Z",
                ["Text"] = "Old", ["ProductElementCode"] = "E_OLD",
            });

            // A TSO subscription on the metering point, and an ended tariff that is left out.
            var tso = Guid.NewGuid();
            api.MarketPriceLists[tso] = new JsonObject
            {
                ["Id"] = tso.ToString(),
                ["PriceInformation"] = new JsonObject { ["Name"] = "Abonnement", ["Description"] = "Abonnement TSO" },
            };
            mp.MarketPriceLists.Add(MarketPriceListOn(tso, "40000", "Subscription", "5790000432752",
                "2023-12-31T23:00:00Z", "9999-12-31T23:59:58.999Z"));
            var ended = Guid.NewGuid();
            api.MarketPriceLists[ended] = new JsonObject { ["Id"] = ended.ToString() };
            mp.MarketPriceLists.Add(MarketPriceListOn(ended, "41000", "Tariff", "5790000432752",
                "2020-12-31T23:00:00Z", "2022-12-31T23:00:00Z"));

            var (exitCode, document) = await ExportAsync(tenant, raw: false);

            Assert.Equal(0, exitCode);
            Assert.Empty(api.UnknownRoutes.Distinct());
            var customer = Assert.Single(document["customers"]!.AsArray())!;
            Assert.Equal("Erhverv", (string?)customer["customerSegment"]);
            Assert.Equal("Yes", (string?)customer["manualApprovalRequired"]);

            var exported = Assert.Single(customer["meteringPoints"]!.AsArray(), m => (string?)m!["meteringPointId"] == mp.Identification)!;
            Assert.Equal(area.Name, (string?)exported["priceArea"]);
            Assert.Equal("Test remark", (string?)exported["remark"]);
            Assert.Equal("Erhverv", (string?)exported["consumer"]?["category"]);
            Assert.False(exported["consumer"]?["electricalHeating"]?.GetValue<bool>());
            Assert.False(exported["billing"]?["ignoreInBilling"]?.GetValue<bool>());
            Assert.True(exported["billing"]?["exemptFromVat"]?.GetValue<bool>());

            var charge = Assert.Single(exported["marketPriceLists"]!.AsArray())!;
            Assert.Equal("40000", (string?)charge["priceListId"]);
            Assert.Equal("Subscription", (string?)charge["type"]);
            Assert.Equal("5790000432752", (string?)charge["chargeOwner"]);
            Assert.Equal("2024-01-01", (string?)charge["start"]);
            Assert.Null(charge["end"]);
            Assert.Equal(1, (int?)charge["chargeOccurrences"]);
            Assert.Equal("Abonnement TSO", (string?)charge["description"]);

            var exportedContract = Assert.Single(customer["contracts"]!.AsArray(), c => (string?)c!["name"] == contract.Name)!;
            Assert.Equal("4711", (string?)exportedContract["orderNumber"]);
            Assert.Equal("Erhverv", (string?)exportedContract["category"]);
            Assert.Equal("Electricity", (string?)exportedContract["commodityType"]);
            Assert.Equal("Profile", (string?)exportedContract["quantityDistributionType"]);
            Assert.Equal("Quarterly", (string?)exportedContract["finalSettlement"]?["frequency"]);
            Assert.Equal(30, (int?)exportedContract["finalSettlement"]?["paymentTerms"]);
            var line = Assert.Single(exportedContract["lines"]!.AsArray())!;
            Assert.Equal("E_KAM_Spot", (string?)line["productElement"]);
            Assert.Equal("2026-01-01", (string?)line["start"]);
            Assert.Equal(100, (int?)line["coverage"]);

            var price = Assert.Single(customer["priceLists"]!.AsArray())!;
            Assert.Equal("E_KAM_Spot", (string?)price["priceList"]);
            Assert.Equal("E_KAM_Tillaeg", (string?)price["productElement"]);
            Assert.Equal(area.Name, (string?)price["priceArea"]);
            Assert.Equal("2023-01-01", (string?)price["start"]);
            Assert.Null(price["end"]);
        }
        finally
        {
            await tenant.DisposeAsync();
        }
    }

    [Fact]
    public async Task Payment_agreements_from_the_settl_csv_follow_the_metering_point_or_joint_invoice()
    {
        var tenant = new ProvisioningFixture();
        await tenant.InitializeAsync();
        StartDelivery(tenant);
        var csv = Path.Combine(Path.GetTempPath(), $"payment-agreements-{Guid.NewGuid():N}.csv");
        try
        {
            var api = tenant.Settl.Api;
            var customerId = api.Customers.Keys.Single();
            var mp = api.CustomerMpLinks[customerId]
                .Select(l => api.MeteringPoints[Guid.Parse((string)l["MeteringPointId"]!)])
                .First(m => m.InDelivery);
            // The billing version create wrote, now with a joint invoice number.
            var billing = mp.BillingConfiguration!;
            billing["JointInvoice"] = true;
            billing["JointInvoiceNumber"] = "J-100";

            // Settl's export layout: Last Updated first, a Currency column the parser drops.
            await File.WriteAllTextAsync(csv, string.Join("\n",
                "Last Updated,Payment Agreement Number,Payment Method,External Customer Number," +
                "Customer Name,Payer Name,Currency,Agreement Type,Metering Point Id,Joint Invoice number",
                $"\"06.11.2026, 10.15.00\",B100,FIK,E100,Test A/S,Test A/S,DKK,Metering Point Payment Agreement,{mp.Identification},",
                "\"05.11.2026, 08.00.00\",B200,PBS,E200,Test A/S,Test A/S,DKK,Joint Invoice Payment Agreement,,J-100",
                "\"04.11.2026, 08.00.00\",B300,PBS,E300,Other,Other,DKK,Metering Point Payment Agreement,571313100000000999,"));

            var (exitCode, document) = await ExportAsync(tenant, raw: false, paymentAgreements: csv);

            Assert.Equal(0, exitCode);
            var customer = Assert.Single(document["customers"]!.AsArray())!;
            var agreements = customer["paymentAgreements"]!.AsArray();
            Assert.Equal(["B100", "B200"], agreements.Select(a => (string)a!["agreementNumber"]!).ToArray());

            var byPoint = agreements[0]!;
            Assert.Equal(mp.Identification, (string?)byPoint["meteringPoint"]);
            Assert.Equal("E100", (string?)byPoint["externalCustomerNumber"]);
            Assert.Equal("FIK", (string?)byPoint["paymentMethod"]);
            Assert.Equal("2026-11-06T10:15:00", (string?)byPoint["lastUpdated"]);

            var byJointInvoice = agreements[1]!;
            Assert.Null(byJointInvoice["meteringPoint"]);
            Assert.Equal("J-100", (string?)byJointInvoice["jointInvoiceNumber"]);
        }
        finally
        {
            File.Delete(csv);
            await tenant.DisposeAsync();
        }
    }

    [Fact]
    public async Task Share_delivery_publishes_json_lines_and_a_delta_of_what_changed_in_settl()
    {
        var tenant = new ProvisioningFixture();
        await tenant.InitializeAsync();
        StartDelivery(tenant);
        var root = Path.Combine(Path.GetTempPath(), $"share-{Guid.NewGuid():N}");
        try
        {
            var settl = tenant.Settl;
            var runner = new ExportActiveCustomersRunner(
                settl.RawReader, settl.MeteringPointClient, settl.CustomerLinkLookup, settl.CustomerClient, log: _ => { });
            var now = new DateTimeOffset(2026, 11, 11, 1, 30, 0, TimeSpan.Zero);
            var delivery = new ExportShareDelivery(() => now, _ => { });
            var options = new ExportShareDeliveryOptions { Root = root, Environment = "featuretest", Shape = "test" };
            Task<int> Export(string path, CancellationToken ct) =>
                runner.RunAsync(new ExportActiveCustomersRunOptions { OutputPath = path, JsonLines = true, Parallelism = 2 }, ct);

            Assert.Equal(0, await delivery.RunAsync(options, Export));

            // Settl changes between the nightly runs: the customer gets a remark on its metering point.
            var customerId = settl.Api.Customers.Keys.Single();
            var mp = settl.Api.MeteringPoints[Guid.Parse((string)settl.Api.CustomerMpLinks[customerId][0]["MeteringPointId"]!)];
            mp.Market["Notes"] = new JsonObject { ["Remark"] = "Changed in Settl" };
            now = now.AddDays(1);
            Assert.Equal(0, await delivery.RunAsync(options, Export));

            var env = Path.Combine(root, "featuretest");
            var second = Path.Combine(env, now.UtcDateTime.ToString("yyyy-MM-dd'T'HHmmss'Z'"));
            Assert.True(File.Exists(Path.Combine(second, ExportShareDelivery.ReadyFile)));
            var customers = File.ReadAllLines(Path.Combine(second, ExportShareDelivery.CustomersFile)).Where(l => l.Length > 0).ToList();
            var line = JsonNode.Parse(Assert.Single(customers))!;
            Assert.Equal((string?)settl.Api.Customers[customerId]["CustomerNumber"], (string?)line["customerNumber"]);

            var delta = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(second, ExportShareDelivery.DeltaFile))))!;
            Assert.Equal("changed", (string?)delta["op"]);
            Assert.Contains(delta["customer"]!["meteringPoints"]!.AsArray(), m => (string?)m!["remark"] == "Changed in Settl");

            // The check a person runs on the server passes on a real delivery.
            var checkLog = new List<string>();
            Assert.Equal(0, new ShareDeliveryCheck(() => now, checkLog.Add).Run(root, "featuretest", TimeSpan.FromHours(26)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            await tenant.DisposeAsync();
        }
    }

    [Fact]
    public async Task Missing_payment_agreement_file_fails_the_run()
    {
        var settl = fixture.Settl;
        var runner = new ExportActiveCustomersRunner(
            settl.RawReader, settl.MeteringPointClient, settl.CustomerLinkLookup, settl.CustomerClient, log: _ => { });
        var output = Path.Combine(Path.GetTempPath(), $"export-active-customers-{Guid.NewGuid():N}.json");

        var exitCode = await runner.RunAsync(new ExportActiveCustomersRunOptions
        {
            OutputPath = output,
            PaymentAgreementsCsv = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csv"),
        });

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(output));
    }

    private static JsonObject MarketPriceListOn(Guid priceList, string identification, string type, string owner,
        string start, string end) => new()
    {
        ["Id"] = priceList.ToString(),
        ["PriceIdentification"] = identification,
        ["PriceChargeOwner"] = new JsonObject { ["Identification"] = owner, ["IdentificationKind"] = "GLN" },
        ["MarketPriceListType"] = type,
        ["MeteringPointMarketPriceListAssociation"] = new JsonObject
        {
            ["AssociationId"] = Guid.NewGuid().ToString(),
            ["MarketPriceListId"] = priceList.ToString(),
            ["ChargeOccurrences"] = 1,
            ["Start"] = start,
            ["End"] = end,
        },
    };

    private static async Task LinkAsync(ProvisioningFixture tenant, Guid customerId, Guid meteringPointId)
    {
        var (ok, error) = await tenant.Settl.CustomerClient.AddMeteringPointsAsync(customerId, new AddCustomerMeteringPointsRequest
        {
            CustomerMeteringPoints = [new() { MeteringPointId = meteringPointId, Start = ProvisioningFixture.Cos.AddDays(-1) }],
        });
        Assert.True(ok, error);
    }

    private static async Task<(int ExitCode, JsonObject Document)> ExportAsync(ProvisioningFixture tenant, bool raw,
        string? paymentAgreements = null)
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
                PaymentAgreementsCsv = paymentAgreements,
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
