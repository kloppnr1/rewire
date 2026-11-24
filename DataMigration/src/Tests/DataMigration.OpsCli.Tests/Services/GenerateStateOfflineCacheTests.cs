using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Data;
using DataMigration.Domain.Customers;
using DataMigration.SettlApi.Models;
using System.Text.Json;

namespace DataMigration.OpsCli.Tests.Services;

public class GenerateStateOfflineCacheTests
{
    [Fact]
    public async Task Offline_run_requires_skip_flag_for_captured_missing_settl_customer()
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-skip-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var seed = new MigrationStateDocument { Accounts =
                [new MigratedAccount { SettlAccountNumber = "503281" }] };
            var inputs = new GenerateStateOfflineCache.RunInputs(
                JsonSerializer.Serialize(seed, new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                new Dictionary<string, DateOnly> { ["503281"] = new(2026, 6, 11) },
                [], [], [], [], [],
                [new SourceAccountOwnershipIssue("NoSettlCustomerForMeteringPoint",
                    "417016", "MP1", "", new DateOnly(2026, 6, 11))],
                ["417016"]);
            var options = new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(root, "state.json"), Env = "production",
                NoInvoices = true
            };
            Assert.Equal(1, await new GenerateStateFromSettlRunner(null!)
                .ReplayFromInputsAsync(inputs, options, CancellationToken.None));
            Assert.False(File.Exists(options.OutputPath));

            options.SkipUnresolvedSourceAccounts = true;
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!)
                .ReplayFromInputsAsync(inputs, options, CancellationToken.None));
            Assert.Contains("NoSettlCustomerForMeteringPoint",
                await File.ReadAllTextAsync(Path.Combine(root, "state-ownership-issues.csv")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Failed_run_keeps_delivery_evidence_without_replacing_a_successful_capture()
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-failed-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var options = new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(root, "state.json"), Env = "production"
            };
            var histories = new[] { new GenerateStateOfflineCache.DeliveryHistoryRecord(
                "MP1", Guid.NewGuid(), true, null, null, null,
                "lookup failed", null, "DeliveryStatusLookupFailed") };

            await GenerateStateOfflineCache.CaptureFailureAsync(options,
                histories, CancellationToken.None);
            var incomplete = Path.Combine(GenerateStateOfflineCache.DirectoryFor(options), "incomplete");
            Assert.Contains("\"Status\":\"Incomplete\"",
                await File.ReadAllTextAsync(Path.Combine(incomplete, "manifest.json")));
            Assert.Contains("MP1", await File.ReadAllTextAsync(
                Path.Combine(incomplete, "delivery-histories.json")));
            Assert.False(File.Exists(Path.Combine(incomplete, "ownership-issues.csv")));
            Assert.Equal(1, await GenerateStateOfflineCache.ReplayAsync(options));

            var seed = new MigrationStateDocument { Accounts = [] };
            var inputs = new GenerateStateOfflineCache.RunInputs(
                JsonSerializer.Serialize(seed, new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                new Dictionary<string, DateOnly>(), [], [], [], []);
            await GenerateStateOfflineCache.CaptureDataAsync(options, histories, inputs,
                CancellationToken.None);
            var completeManifest = await File.ReadAllTextAsync(Path.Combine(
                GenerateStateOfflineCache.DirectoryFor(options), "manifest.json"));
            await GenerateStateOfflineCache.CaptureFailureAsync(options,
                histories, CancellationToken.None);
            Assert.Equal(completeManifest, await File.ReadAllTextAsync(Path.Combine(
                GenerateStateOfflineCache.DirectoryFor(options), "manifest.json")));
            Assert.Equal(0, await GenerateStateOfflineCache.ReplayAsync(options));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Default_capture_directory_is_beside_the_output_and_independent_of_filename()
    {
        var data = Path.Combine(Path.GetTempPath(), "migration-data-" + Guid.NewGuid());
        var live = new GenerateStateFromSettlRunOptions
        {
            OutputPath = Path.Combine(data, "new-invoices-state.json"), Env = "production"
        };
        var offline = new GenerateStateFromSettlRunOptions
        {
            OutputPath = Path.Combine(data, "replayed-state.json"), Env = "production"
        };

        var expected = Path.Combine(data, "generate-state-from-settl-offline", "production");
        Assert.Equal(expected, GenerateStateOfflineCache.DirectoryFor(live));
        Assert.Equal(expected, GenerateStateOfflineCache.DirectoryFor(offline));
    }

    [Fact]
    public async Task Offline_reapplies_ownership_and_invoice_filters_from_captured_inputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "settl-input-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var live = new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(directory, "live.json"), Env = "production",
                ReferenceDate = new DateTime(2025, 5, 1),
                IncludeActive = true, HistoryYears = 1,
                OfflineCacheDirectory = Path.Combine(directory, "cache")
            };
            var seed = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "503281", CustomerName = "Tina" }] };
            var invoice = new Invoice("INV1", "503281", new DateTime(2024, 6, 1),
                new DateTime(2024, 6, 15), "A", "A", BillingCategory.Aconto,
                PaymentMedia.Fik, InvoiceStatus.Invoiced, 100m, 25m);
            var earlierOccupant = invoice with { InvoiceId = "OLD", AccountNumber = "417016" };
            var inputs = new GenerateStateOfflineCache.RunInputs(
                JsonSerializer.Serialize(seed, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                new Dictionary<string, DateOnly> { ["503281"] = new(2025, 5, 1) },
                [new("MP1", "503281", "503281", "503281", new DateOnly(2025, 5, 1), ["503281"])],
                ["417016", "503281"], [invoice, earlierOccupant], []);
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).ReplayFromInputsAsync(
                inputs, live, CancellationToken.None));
            var expectedOutput = await File.ReadAllTextAsync(live.OutputPath);
            File.Delete(live.OutputPath);
            var first = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
            var histories = new[] { new GenerateStateOfflineCache.DeliveryHistoryRecord(
                "MP1", Guid.NewGuid(), true, new TimePeriodDto { Start = first }, null,
                new GetDeliveryStatusesResponse { DeliveryStatuses =
                [new DeliveryStatusDto { InDelivery = true,
                    Period = new TimePeriodDto { Start = first },
                    StartReason = DeliveryStartReason.ChangeOfSupplier }] },
                null, new DateOnly(2025, 5, 1), "ChangeOfSupplier") };
            await GenerateStateOfflineCache.CaptureDataAsync(live, histories, inputs,
                CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(live.OfflineCacheDirectory!, "state.json")));
            var replay = new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(directory, "offline.json"), Env = "production",
                OfflineCacheDirectory = live.OfflineCacheDirectory, Offline = true,
                IncludeActive = true, SkipUnresolvedSourceAccounts = true,
                HistoryYears = 1
            };
            Assert.Equal(0, await GenerateStateOfflineCache.ReplayAsync(replay));
            Assert.Equal(expectedOutput, await File.ReadAllTextAsync(replay.OutputPath));
            var state = JsonSerializer.Deserialize<MigrationStateDocument>(
                await File.ReadAllTextAsync(replay.OutputPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal("INV1", Assert.Single(Assert.Single(state!.Accounts).Invoices).InvoiceId);

            replay.HistoryYears = 0;
            Assert.Equal(0, await GenerateStateOfflineCache.ReplayAsync(replay));
            state = JsonSerializer.Deserialize<MigrationStateDocument>(
                await File.ReadAllTextAsync(replay.OutputPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Empty(Assert.Single(state!.Accounts).Invoices);
            replay.HistoryYears = 1;

            replay.IncludeActive = false;
            replay.SkipActive = true;
            replay.SkipUnresolvedSourceAccounts = false;
            Assert.Equal(0, await GenerateStateOfflineCache.ReplayAsync(replay));
            state = JsonSerializer.Deserialize<MigrationStateDocument>(
                await File.ReadAllTextAsync(replay.OutputPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Empty(Assert.Single(state!.Accounts).Invoices);

            replay.SkipActive = false;
            Assert.Equal(1, await GenerateStateOfflineCache.ReplayAsync(replay));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Offline_rebuilds_from_data_without_a_saved_output_and_validates_its_source()
    {
        var directory = Path.Combine(Path.GetTempPath(), "settl-state-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var live = new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(directory, "live.json"), Env = "production",
                ReferenceDate = new DateTime(2025, 5, 1),
                OfflineCacheDirectory = Path.Combine(directory, "cache")
            };
            var first = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
            var histories = new[] { new GenerateStateOfflineCache.DeliveryHistoryRecord(
                "MP1", Guid.NewGuid(), true, new TimePeriodDto { Start = first }, null,
                new GetDeliveryStatusesResponse { DeliveryStatuses =
                [
                    new DeliveryStatusDto { InDelivery = true,
                        Period = new TimePeriodDto { Start = first },
                        StartReason = DeliveryStartReason.ChangeOfSupplier }
                ] }, null, new DateOnly(2025, 5, 1), "ChangeOfSupplier") };
            var seed = new MigrationStateDocument { Accounts = [] };
            var inputs = new GenerateStateOfflineCache.RunInputs(
                JsonSerializer.Serialize(seed, new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                new Dictionary<string, DateOnly>(), [], [], [], [],
                [new DeliveryStartAudit("MP2", new DateOnly(2025, 5, 2), "MoveIn")],
                [new SourceAccountOwnershipIssue("MehOnlyXellentMeteringPoint", "", "MP3", "")]);
            await GenerateStateOfflineCache.CaptureDataAsync(live, histories, inputs,
                CancellationToken.None);
            Assert.False(File.Exists(live.OutputPath));
            Assert.Contains("ChangeOfSupplier", await File.ReadAllTextAsync(
                Path.Combine(live.OfflineCacheDirectory!, "delivery-histories.json")));

            var replay = new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(directory, "replay.json"), Env = "production",
                OfflineCacheDirectory = live.OfflineCacheDirectory, Offline = true
            };
            Assert.Equal(0, await GenerateStateOfflineCache.ReplayAsync(replay));
            Assert.Contains("\"accounts\": []", await File.ReadAllTextAsync(replay.OutputPath));
            Assert.Contains("MehOnlyXellentMeteringPoint", await File.ReadAllTextAsync(
                Path.Combine(directory, "replay-ownership-issues.csv")));
            Assert.Contains("MP2;2025-05-02;MoveIn", await File.ReadAllTextAsync(
                Path.Combine(directory, "replay-delivery-start-audit.csv")));

            // An older run-shaped cache must not be interpreted as a data snapshot.
            var manifestPath = Path.Combine(live.OfflineCacheDirectory!, "manifest.json");
            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("\"Version\":3", manifestJson);
            await File.WriteAllTextAsync(manifestPath,
                manifestJson.Replace("\"Version\":3", "\"Version\":2", StringComparison.Ordinal));
            Assert.Equal(1, await GenerateStateOfflineCache.ReplayAsync(replay));
            await File.WriteAllTextAsync(manifestPath, manifestJson);

            replay.Env = "featuretest";
            Assert.Equal(1, await GenerateStateOfflineCache.ReplayAsync(replay));

            replay.Env = "production";
            await GenerateStateOfflineCache.CaptureDataAsync(live,
                [histories[0] with { FirstDate = new DateOnly(2026, 1, 1) }],
                inputs, CancellationToken.None);
            Assert.Equal(1, await GenerateStateOfflineCache.ReplayAsync(replay));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
