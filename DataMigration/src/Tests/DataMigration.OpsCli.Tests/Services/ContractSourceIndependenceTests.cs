using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class ContractSourceIndependenceTests
{
    [Fact]
    public async Task Contract_capture_reads_without_any_invoice_sections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contract-only-" + Guid.NewGuid());
        try
        {
            var store = StateSourceSnapshotStore.Open(directory, "production");
            await store.BeginAsync(CancellationToken.None);
            await store.WriteAsync("xellent-contract-parts", Array.Empty<XellentContractPartRow>(), CancellationToken.None);
            await store.WriteAsync("xellent-contract-part-logs", Array.Empty<XellentContractPartRow>(), CancellationToken.None);
            await store.WriteAsync("xellent-charge-subscriptions", Array.Empty<XellentChargeSubscriptionRow>(), CancellationToken.None);
            await store.WriteAsync("xellent-charger-assignments", Array.Empty<XellentChargerAssignmentRow>(), CancellationToken.None);
            await store.WriteAsync("settl-contract-links", Array.Empty<SettlContractLinkRow>(), CancellationToken.None);
            await store.CompleteAsync(CancellationToken.None);

            var read = await store.ReadContractSourcesAsync(CancellationToken.None);
            Assert.Empty(read.LiveParts);
            Assert.Empty(read.SettlLinks);
            Assert.Empty(read.ChargerAssignments);
            Assert.False(File.Exists(Path.Combine(directory, "xellent-invoices.jsonl")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Source_sections_can_be_added_to_an_existing_offline_snapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contract-update-" + Guid.NewGuid());
        try
        {
            var store = StateSourceSnapshotStore.Open(directory, "production");
            await store.BeginUpdateAsync(CancellationToken.None);
            await store.WriteAsync("settl-contract-links", Array.Empty<SettlContractLinkRow>(), CancellationToken.None);
            await store.CompleteAsync(CancellationToken.None);

            store = StateSourceSnapshotStore.Open(directory, "production");
            await store.BeginUpdateAsync(CancellationToken.None);
            await store.WriteAsync("xellent-contract-parts", Array.Empty<XellentContractPartRow>(), CancellationToken.None);
            await store.WriteAsync("xellent-contract-part-logs", Array.Empty<XellentContractPartRow>(), CancellationToken.None);
            await store.WriteAsync("xellent-charge-subscriptions", Array.Empty<XellentChargeSubscriptionRow>(), CancellationToken.None);
            await store.WriteAsync("xellent-charger-assignments", Array.Empty<XellentChargerAssignmentRow>(), CancellationToken.None);
            await store.CompleteAsync(CancellationToken.None);

            var read = await store.ReadContractSourcesAsync(CancellationToken.None);
            Assert.Empty(read.SettlLinks);
            Assert.Empty(read.ChargerAssignments);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Offline_impact_report_labels_physical_and_account_only_chargers()
    {
        var part = new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription,
            "OAboEje0", new DateOnly(2026, 1, 1), null);
        var source = new ContractSourceSnapshot(
            [new("MP1", "A1", "C1", "G1", "V AboRabat", new DateTime(2026, 1, 1),
                new DateTime(1900, 1, 1), "Live", null)], [],
            [new("MP1", Guid.NewGuid(), "V AboRabat", DateTimeOffset.UtcNow,
                DateTimeOffset.MaxValue)])
        {
            HasChargerAssignments = true,
            ChargerAssignments =
            [
                new("MP1", new EvCharger("I1", "A1", "CC1", null, [part]),
                    new DateOnly(2026, 8, 31)),
                new(null, new EvCharger("I2", "A1", "CC2", null, [part]),
                    new DateOnly(2026, 8, 31))
            ]
        };

        var report = ContractImpactReport.Build(source, "V AboRabat");
        Assert.Contains("I1:OAboEje0;I2:OAboEje0", report.Csv);
    }
}
