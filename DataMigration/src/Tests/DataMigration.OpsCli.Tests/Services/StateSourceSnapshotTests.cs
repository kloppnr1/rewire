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

    // One customer (503281) with one metering point, COS 2025-05-01, and its latest invoice an Aconto:
    // the old Slutafregning rule would call it active. Only the statement date decides now.
    private static async Task WriteOneCustomerSnapshotAsync(StateSourceSnapshotStore store,
        DateTime? latestStatement, bool withSettlementData = true)
    {
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
        var ct = CancellationToken.None;
        await store.WriteAsync("settl-customers", new[] { new CustomerInfoDto
            { Id = customerId, CustomerNumber = "503281", FullName = "Customer" } }, ct);
        await store.WriteAsync("settl-metering-points", new[] { mp }, ct);
        await store.WriteAsync("settl-delivery-histories", new[]
        {
            new GenerateStateOfflineCache.DeliveryHistoryRecord("MP1", mpId, true,
                mp.Info.CurrentOrNextDeliveryPeriod, null, statuses, null, cos, "ChangeOfSupplier")
        }, ct);
        await store.WriteAsync("settl-payers", new[] { new StatePayerLookup(cos, [mpId],
        [new PayerForMeteringPointsDto
        {
            Payer = new PayerResponse { CustomerId = customerId, IsPrimaryCustomerPayer = true },
            MeteringPoints = [new PayerMeteringPointDto { MeteringPointId = mpId }]
        }], null) }, ct);
        await store.WriteAsync("xellent-mp-accounts", new[] { new StateMpAccount("MP1", "503281") }, ct);
        await store.WriteAsync("xellent-for-mps", new[] { "MP1" }, ct);
        await store.WriteAsync("xellent-meh-mps", Array.Empty<string>(), ct);
        await store.WriteAsync("xellent-representatives", new[]
            { new StateXellentEvidence("MP1", "503281", "503281", cos, ["503281"]) }, ct);
        await store.WriteAsync("xellent-invoices", new[]
        {
            new Invoice("INV1", "503281", new DateTime(2024, 6, 1),
                new DateTime(2024, 6, 15), "A", "A", BillingCategory.Aconto,
                PaymentMedia.Fik, InvoiceStatus.Invoiced, 100m, 25m)
        }, ct);
        await store.WriteAsync("xellent-excluded-invoices", Array.Empty<ExcludedCompanyInvoice>(), ct);
        if (withSettlementData)
        {
            await store.WriteAsync("xellent-owner-contracts",
                new[] { new StateOwnerContract("MP1", "C1", new DateOnly(2025, 4, 30)) }, ct);
            await store.WriteAsync("xellent-settlement-parts", new[] { new SettlementCheck.PartRow
            {
                MeteringPoint = "MP1", ContractNumber = "C1", RecId = 7,
                StartDate = new DateTime(2020, 1, 1), EndDate = new DateTime(2025, 4, 30)
            } }, ct);
            await store.WriteAsync("xellent-statement-dates", latestStatement is { } date
                ? new[] { new SettlementCheck.StatementRow
                    { MeteringPoint = "MP1", ContractNumber = "C1", LatestStatementDate = date } }
                : Array.Empty<SettlementCheck.StatementRow>(), ct);
        }
        await store.CompleteAsync(ct);
    }

    private static GenerateStateFromSettlRunOptions OfflineOptions(string root, string output) => new()
    {
        Env = "production", Offline = true, OfflineCacheDirectory = root,
        OutputPath = Path.Combine(root, output), HistoryYears = 1, SkipWaterCensus = true
    };

    [Fact]
    public async Task Offline_customer_billed_up_to_cos_minus_one_keeps_invoices_whatever_the_latest_category()
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-sources-" + Guid.NewGuid());
        try
        {
            await WriteOneCustomerSnapshotAsync(StateSourceSnapshotStore.Open(root, "production"),
                new DateTime(2025, 4, 30));
            var options = OfflineOptions(root, "state.json");
            options.SettlementCheck = true;

            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));

            Assert.Contains("INV1", await File.ReadAllTextAsync(options.OutputPath));
            Assert.Contains("503281;503281;MP1;C1;7;2025-05-01;2025-04-30;Settled",
                await File.ReadAllTextAsync(SettlementCheck.ReportPath(options.OutputPath)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("2025-03-31")]
    [InlineData(null)]
    public async Task Offline_customer_not_billed_up_to_cos_minus_one_fails_or_loses_its_invoices(string? latest)
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-sources-" + Guid.NewGuid());
        try
        {
            await WriteOneCustomerSnapshotAsync(StateSourceSnapshotStore.Open(root, "production"),
                latest is null ? null : DateTime.Parse(latest));
            var options = OfflineOptions(root, "state.json");

            Assert.Equal(1, await new GenerateStateFromSettlRunner(null!).RunAsync(options));

            options.SkipActive = true;
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            var json = await File.ReadAllTextAsync(options.OutputPath);
            Assert.Contains("503281", json);
            Assert.DoesNotContain("INV1", json);

            options.SkipActive = false;
            options.IncludeActive = true;
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.Contains("INV1", await File.ReadAllTextAsync(options.OutputPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Offline_snapshot_without_statement_dates_is_refused_unless_the_test_is_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-sources-" + Guid.NewGuid());
        try
        {
            await WriteOneCustomerSnapshotAsync(StateSourceSnapshotStore.Open(root, "production"),
                new DateTime(2025, 4, 30), withSettlementData: false);
            var options = OfflineOptions(root, "state.json");
            options.SkipActive = true;

            Assert.Equal(1, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            Assert.False(File.Exists(options.OutputPath));

            options.IncludeActive = true;
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));

            options.SettlementCheck = true;
            Assert.Equal(1, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // One customer (503281) with two metering points on one account. MP1's contract C1 is billed up
    // to COS-1; MP2's contract C2 only has the empty statement date. Only C2's invoice is held back.
    [Fact]
    public async Task Offline_invoices_of_a_contract_not_billed_up_to_cos_minus_one_are_held_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "settl-sources-" + Guid.NewGuid());
        try
        {
            var store = StateSourceSnapshotStore.Open(root, "production");
            await store.BeginAsync(CancellationToken.None);
            var ct = CancellationToken.None;
            var customerId = Guid.NewGuid();
            var cos = new DateOnly(2025, 5, 1);
            var start = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
            var points = new[] { ("MP1", Guid.NewGuid()), ("MP2", Guid.NewGuid()) };
            var statuses = new GetDeliveryStatusesResponse { DeliveryStatuses =
            [new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto { Start = start },
                StartReason = DeliveryStartReason.ChangeOfSupplier }] };
            var mps = points.Select(p => new SettlMpWithCos(p.Item1, p.Item2, cos, new MeteringPointInfoDto
                { InDelivery = true, CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = start } })).ToArray();
            await store.WriteAsync("settl-customers", new[] { new CustomerInfoDto
                { Id = customerId, CustomerNumber = "503281", FullName = "Customer" } }, ct);
            await store.WriteAsync("settl-metering-points", mps, ct);
            await store.WriteAsync("settl-delivery-histories", mps.Select(m =>
                new GenerateStateOfflineCache.DeliveryHistoryRecord(m.Identification, m.Id, true,
                    m.Info.CurrentOrNextDeliveryPeriod, null, statuses, null, cos, "ChangeOfSupplier")).ToArray(), ct);
            await store.WriteAsync("settl-payers", new[] { new StatePayerLookup(cos, [.. points.Select(p => p.Item2)],
            [new PayerForMeteringPointsDto
            {
                Payer = new PayerResponse { CustomerId = customerId, IsPrimaryCustomerPayer = true },
                MeteringPoints = [.. points.Select(p => new PayerMeteringPointDto { MeteringPointId = p.Item2 })]
            }], null) }, ct);
            await store.WriteAsync("xellent-mp-accounts", points.Select(p => new StateMpAccount(p.Item1, "503281")).ToArray(), ct);
            await store.WriteAsync("xellent-for-mps", points.Select(p => p.Item1).ToArray(), ct);
            await store.WriteAsync("xellent-meh-mps", Array.Empty<string>(), ct);
            await store.WriteAsync("xellent-representatives", points.Select(p =>
                new StateXellentEvidence(p.Item1, "503281", "503281", cos, ["503281"])).ToArray(), ct);
            Invoice Inv(string id, string contract) => new(id, "503281", new DateTime(2024, 6, 1),
                new DateTime(2024, 6, 15), "A", "A", BillingCategory.Aconto, PaymentMedia.Fik,
                InvoiceStatus.Invoiced, 100m, 25m) { ContractNumber = contract };
            await store.WriteAsync("xellent-invoices", new[] { Inv("INV1", "C1"), Inv("INV2", "C2") }, ct);
            await store.WriteAsync("xellent-excluded-invoices", Array.Empty<ExcludedCompanyInvoice>(), ct);
            await store.WriteAsync("xellent-owner-contracts", new[]
            {
                new StateOwnerContract("MP1", "C1", new DateOnly(2025, 4, 30)),
                new StateOwnerContract("MP2", "C2", new DateOnly(2025, 4, 30)),
            }, ct);
            await store.WriteAsync("xellent-settlement-parts", Array.Empty<SettlementCheck.PartRow>(), ct);
            await store.WriteAsync("xellent-statement-dates", new[]
            {
                new SettlementCheck.StatementRow { MeteringPoint = "MP1", ContractNumber = "C1", LatestStatementDate = new DateTime(2025, 4, 30) },
                new SettlementCheck.StatementRow { MeteringPoint = "MP2", ContractNumber = "C2", LatestStatementDate = new DateTime(1900, 1, 1) },
            }, ct);
            await store.CompleteAsync(ct);
            var options = OfflineOptions(root, "state.json");

            Assert.Equal(1, await new GenerateStateFromSettlRunner(null!).RunAsync(options));

            options.SkipActive = true;
            Assert.Equal(0, await new GenerateStateFromSettlRunner(null!).RunAsync(options));
            var json = await File.ReadAllTextAsync(options.OutputPath);
            Assert.Contains("INV1", json);
            Assert.DoesNotContain("INV2", json);
            var held = await File.ReadAllTextAsync(SettlementCheck.HeldInvoicesReportPath(options.OutputPath));
            Assert.Contains("503281;503281;INV2;2024-06-01;2;2;C2;MP2;2025-05-01;1900-01-01", held);
            Assert.DoesNotContain("INV1", held);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
