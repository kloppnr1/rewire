using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests.Extraction;

/// <summary>
/// En ladestander ligger på sin egen kontrakt og migreres ikke. Rapporten skal alligevel bære den
/// med, for det er der man kan se — inden kørslen — at el-kontrakten lukkes i Xellent mens
/// ladestanderen bliver stående. Som resten af de rene display-felter fyldes de også i dry-run og
/// offline, hvor der ikke er nogen Settl at spørge.
/// </summary>
public class EvChargerReportTests
{
    [Fact]
    public async Task Report_carries_chargers_tied_to_a_metering_point()
    {
        var load = LoadResult() with
        {
            EvChargersByMeteringPoint = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
            {
                ["MP-1"] = [Charger("1086589", "100406")],
            },
        };

        var report = await RunAsync(load);

        var entry = Assert.Single(report.EvChargersByMeteringPoint);
        Assert.Equal("MP-1", entry.Key);
        var charger = Assert.Single(entry.Value);
        Assert.Equal("1086589", charger.InstallationNumber);
        Assert.Equal("Fiktivgadec 90, 8920 Randers NV", charger.Address);
        Assert.True(charger.HasSubscription);
        Assert.False(charger.HasConsumption);
        Assert.Empty(report.EvChargersByAccount);
    }

    [Fact]
    public async Task Report_carries_unlinked_chargers_on_the_account()
    {
        var load = LoadResult() with
        {
            EvChargersByAccount = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
            {
                ["100406"] = [Charger("1086589", "100406"), Charger("1086590", "100406")],
            },
        };

        var report = await RunAsync(load);

        Assert.Empty(report.EvChargersByMeteringPoint);
        var entry = Assert.Single(report.EvChargersByAccount);
        Assert.Equal("100406", entry.Key);
        Assert.Equal(2, entry.Value.Count);
    }

    [Fact]
    public async Task Report_has_no_charger_entries_when_the_extract_has_none()
    {
        var report = await RunAsync(LoadResult());

        Assert.Empty(report.EvChargersByMeteringPoint);
        Assert.Empty(report.EvChargersByAccount);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<MigrationReport> RunAsync(XellentLoadResult load)
    {
        var runner = new CreateRunner(
            new DataMigration.SettlApi.Configuration.SettlApiOptions(),
            new Fixtures.MockCustomerClient(), new Fixtures.MockPayerClient(),
            new Fixtures.MockMeteringPointClient(), new Fixtures.MockContractClient(),
            new MockSettlementGroupClient(), new WarningCollector());

        var options = new CreateOptions
        {
            Accounts = ["100406"],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 1, 1),
            DryRun = true,
            Offline = true,
        };

        var (_, report) = await runner.RunAsync(options, load);
        return report;
    }

    private static EvCharger Charger(string installation, string account) =>
        new(
            InstallationNumber: installation,
            CustomerAccount: account,
            ContractNumber: "10562433",
            Address: "Fiktivgadec 90, 8920 Randers NV",
            Parts:
            [
                new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                    new DateOnly(2025, 3, 19), null)
            ]);

    private static XellentLoadResult LoadResult()
    {
        var address = new Address("Fiktivgadec", "90", null, null, "8920", "Randers NV", null, null, null, "DK", null);
        var mp = new MeteringPoint(
            Identification: "MP-1",
            InstallationAddress: address,
            Commodity: Commodity.Electricity,
            Meter: null,
            ContactPoints: [],
            JointInvoiceGroupId: null,
            ConnectionStatus: ConnectionStatus.Connected,
            InDelivery: true,
            Grid: null,
            Start: new DateOnly(2025, 1, 1));

        var customer = new Customer(
            AccountNumber: "100406",
            Name: "Testkunde",
            AdditionalName: null,
            Type: CustomerType.Person,
            Cvr: null,
            Cpr: "0101801234",
            Address: address,
            Contact: ContactInfo.Empty,
            Billing: new CustomerBilling(DefaultEan: null, Pbs: null, MobilePay: null),
            Contracts:
            [
                new Contract(
                    Number: "c-100406",
                    Description: "",
                    Start: new DateOnly(2025, 1, 1),
                    Commodity: Commodity.Electricity,
                    CustomerNumber: "100406",
                    Billing: new BillingSchedule(BillingFrequency.Monthly, "M01"),
                    MeteringPoints:
                    [
                        new MeteringPointAssignment(
                            MeteringPointId: "MP-1",
                            Product: new DataMigration.Domain.Products.Product("P", "P", Commodity.Electricity),
                            Start: new DateOnly(2025, 1, 1),
                            EndDate: new DateOnly(1900, 1, 1))
                    ])
            ])
        {
            Payers =
            [
                new Payer("100406", IsPrimary: true, ContractNumbers: ["c-100406"],
                    BuyerInvoicing: null, InvoiceChannel.Pdf, PaymentChannel.Fik)
            ]
        };

        return new XellentLoadResult(
            Customers: [customer],
            MeteringPoints: [mp],
            OwnerClusters: [],
            JointInvoiceGroups: [],
            SnapshotDate: new DateOnly(2026, 1, 1),
            MpOrderIdentifiers: new Dictionary<string, string?>(StringComparer.Ordinal),
            AccountGroupings: [new DataMigration.Domain.Xellent.AccountGrouping("100406", ["100406"], null, null, null, new Dictionary<string, string>())],
            FingerprintByRepresentative: new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal));
    }
}
