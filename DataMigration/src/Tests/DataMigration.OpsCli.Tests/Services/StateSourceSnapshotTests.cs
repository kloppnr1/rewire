using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class StateSourceSnapshotTests
{
    [Fact]
    public async Task Pull_sections_survive_incomplete_capture_and_complete_snapshot_replays_without_clients()
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-sources-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var store = StateSourceSnapshotStore.Open(root, "production");
            await store.BeginAsync(CancellationToken.None);
            var customerId = Guid.NewGuid();
            var mpId = Guid.NewGuid();
            var cos = new DateOnly(2025, 5, 1);
            var start = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
            var mp = new SettlMpWithCos("MP1", mpId, cos, new MeteringPointInfoDto
            {
                InDelivery = true,
                CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = start }
            });
            var statuses = new GetDeliveryStatusesResponse { DeliveryStatuses =
            [new DeliveryStatusDto { InDelivery = true,
                Period = new TimePeriodDto { Start = start },
                StartReason = DeliveryStartReason.ChangeOfSupplier }] };
            await store.WriteAsync("settl-customers", new[] { new CustomerInfoDto
                { Id = customerId, CustomerNumber = "503281", FullName = "Customer" } }, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(CancellationToken.None));
            Assert.Contains("503281", await File.ReadAllTextAsync(Path.Combine(root, "settl-customers.jsonl")));

            await store.WriteAsync("settl-metering-points", new[] { mp }, CancellationToken.None);
            await store.WriteAsync("settl-delivery-histories", new[]
            {
                new GenerateStateOfflineCache.DeliveryHistoryRecord("MP1", mpId, true,
                    mp.Info.CurrentOrNextDeliveryPeriod, null, statuses, null, cos, "ChangeOfSupplier")
            }, CancellationToken.None);
            await store.WriteAsync("settl-payers", new[] { new StatePayerLookup(cos, [mpId],
            [new PayerForMeteringPointsDto
            {
                Payer = new PayerResponse { CustomerId = customerId, IsPrimaryCustomerPayer = true },
                MeteringPoints = [new PayerMeteringPointDto { MeteringPointId = mpId }]
            }], null) }, CancellationToken.None);
            await store.WriteAsync("xellent-mp-accounts", new[]
                { new StateMpAccount("MP1", "503281") }, CancellationToken.None);
            await store.WriteAsync("xellent-for-mps", new[] { "MP1" }, CancellationToken.None);
            await store.WriteAsync("xellent-meh-mps", Array.Empty<string>(), CancellationToken.None);
            await store.WriteAsync("xellent-representatives", new[]
                { new StateXellentEvidence("MP1", "503281", "503281", cos, ["503281"]) }, CancellationToken.None);
            await store.WriteAsync("xellent-invoices", new[]
            {
                new Invoice("INV1", "503281", new DateTime(2024, 6, 1),
                    new DateTime(2024, 6, 15), "A", "A", BillingCategory.Aconto,
                    PaymentMedia.Fik, InvoiceStatus.Invoiced, 100m, 25m)
            }, CancellationToken.None);
            await store.WriteAsync("xellent-excluded-invoices", Array.Empty<ExcludedCompanyInvoice>(), CancellationToken.None);
            await store.CompleteAsync(CancellationToken.None);

            var output = Path.Combine(root, "state.json");
            var options = new GenerateStateFromSettlRunOptions
            {
                Env = "production", Offline = true, OfflineCacheDirectory = root,
                OutputPath = output,
                IncludeActive = true, HistoryYears = 1, SkipWaterCensus = true
            };
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            var json = await File.ReadAllTextAsync(output);
            Assert.Contains("503281", json);
            Assert.Contains("2025-05-01", json);
            Assert.Contains("INV1", json);
            Assert.Contains("503281", await File.ReadAllTextAsync(Path.Combine(root, "xellent-mp-accounts.jsonl")));

            options.OutputPath = Path.Combine(root, "again.json");
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.Equal(json, await File.ReadAllTextAsync(options.OutputPath));

            options.ReferenceDate = new DateTime(2030, 1, 1);
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.Equal(json, await File.ReadAllTextAsync(options.OutputPath));

            options.ReferenceDate = null;
            options.NoInvoices = true;
            options.OutputPath = Path.Combine(root, "without-invoices.json");
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.DoesNotContain("INV1", await File.ReadAllTextAsync(options.OutputPath));

            options.IncludeUnknownDeliveryStarts = true;
            var oldCapture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.Contains("pull-state-sources again", oldCapture.Message);

            var unknownStatuses = new GetDeliveryStatusesResponse { DeliveryStatuses =
            [new DeliveryStatusDto { InDelivery = true,
                Period = new TimePeriodDto { Start = start },
                StartReason = DeliveryStartReason.Unknown }] };
            await store.WriteAsync("settl-delivery-histories", new[]
            {
                new GenerateStateOfflineCache.DeliveryHistoryRecord("MP1", mpId, true,
                    mp.Info.CurrentOrNextDeliveryPeriod, null, unknownStatuses, null,
                    cos, "UnknownDeliveryStartReason")
            }, CancellationToken.None);
            await store.CompleteAsync(CancellationToken.None, includesUnknownDeliveryStarts: true);
            options.IncludeUnknownDeliveryStarts = false;
            options.NoInvoices = false;
            options.OutputPath = Path.Combine(root, "unknown-default.json");
            Assert.Equal(1, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.False(File.Exists(options.OutputPath));

            options.IncludeUnknownDeliveryStarts = true;
            options.OutputPath = Path.Combine(root, "unknown-included.json");
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.Contains("INV1", await File.ReadAllTextAsync(options.OutputPath));
            Assert.Contains("MP1;2025-05-01;IncludedUnknownDeliveryStartReason",
                await File.ReadAllTextAsync(Path.Combine(root, "unknown-included-delivery-start-audit.csv")));

            await File.AppendAllTextAsync(Path.Combine(root, "xellent-mp-accounts.jsonl"), "corrupt");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GenerateStateFromSettlRunner(null!).RunAsync(options));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
