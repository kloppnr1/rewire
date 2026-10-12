using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Tests;

/// <summary>
/// The up-front batch create of electricity metering points. What matters here is that batching
/// changes only <b>when</b> the metering points are created — not how many are counted as created,
/// existing or failed, and not what dry-run or capture-mode (reconcile) do.
/// </summary>
public class MeteringPointPrecreateTests
{
    private const string Mp1 = "571313100000000011";
    private const string Mp2 = "571313100000000012";
    private const string Mp3 = "571313100000000013";
    private const string Mp4 = "571313100000000014";
    private const string Mp5 = "571313100000000015";
    private const string Mp6 = "571313100000000016";

    [Fact]
    public async Task Precreate_sends_batches_and_maps_every_metering_point()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var customers = new List<Customer>
        {
            MakeCustomer("352639", Mp1, Mp2, Mp3),
            MakeCustomer("352640", Mp4, Mp5)
        };

        var result = await client.PrecreateMeteringPointsAsync(
            customers, MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5), MakeContext(batchSize: 3), default);

        // Two create calls — 3 + 2 — instead of five.
        Assert.Equal([3, 2], api.PostedBatches.Select(b => b.Count).ToList());
        Assert.Equal(2, result.Batches);
        Assert.Equal(5, result.Considered);
        Assert.Equal(5, result.Created);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.AlreadyExisting);
        Assert.Equal(0, result.Unresolved);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Precreate_counts_metering_points_that_already_exist_without_creating_them()
    {
        var api = new RecordingClients();
        api.Preexisting.Add(Mp2);
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2)], MakeMps(Mp1, Mp2), MakeContext(batchSize: 10), default);

        Assert.Equal(1, result.AlreadyExisting);
        Assert.Equal(1, result.Created);
        Assert.Equal([Mp1], api.PostedBatches.Single());
    }

    [Fact]
    public async Task A_rejected_batch_is_halved_until_the_failing_metering_point_stands_alone()
    {
        var api = new RecordingClients();
        api.Rejected.Add(Mp3);
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4)],
            MakeMps(Mp1, Mp2, Mp3, Mp4), MakeContext(batchSize: 4), default);

        // 4 → (1,2) + (3,4) → (3) + (4). The bad one ends up alone, so the error names it.
        Assert.Equal(
            [[Mp1, Mp2, Mp3, Mp4], [Mp1, Mp2], [Mp3, Mp4], [Mp3], [Mp4]],
            api.PostedBatches);
        Assert.Equal(1, result.Failed);
        Assert.Equal(3, result.Created);
        // Same failure text as the per-customer path produced.
        Assert.Equal($"MP {Mp3} create failed: boom", result.Errors.Single());
        // Still one batch as far as the caller is concerned — splits are recovery, not batches.
        Assert.Equal(1, result.Batches);
    }

    [Fact]
    public async Task A_metering_point_Settl_already_has_is_counted_as_existing_and_the_rest_go_in_one_retry()
    {
        var api = new RecordingClients();
        api.Conflicting.Add(Mp3);
        var log = new List<string>();
        var client = NewClient(api, log: log.Add);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4)],
            MakeMps(Mp1, Mp2, Mp3, Mp4), MakeContext(batchSize: 4), default);

        // No halving: the rejection named the metering point, so it is taken out and the other
        // three are re-posted in a single call.
        Assert.Equal([[Mp1, Mp2, Mp3, Mp4], [Mp1, Mp2, Mp4]], api.PostedBatches);
        Assert.Equal(1, result.AlreadyExisting);
        Assert.Equal(3, result.Created);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
        // Visible in the log, but not as a failure.
        Assert.Contains(log, l => l.Contains(Mp3) && l.Contains("already exists"));
        Assert.DoesNotContain(log, l => l.Contains("FAILED"));
    }

    [Fact]
    public async Task A_metering_point_Settl_already_has_keeps_its_guid_for_the_customer_loop()
    {
        var api = new RecordingClients();
        api.Conflicting.Add(Mp1);
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([customer], mps, context, default);
        var postsBefore = api.PostedBatches.Count;

        var result = await client.MigrateAsync(customer, mps, context, default);

        // The id Settl refused is the id the existing metering point carries, so the customer loop
        // links it instead of trying to create it a second time.
        Assert.Equal(postsBefore, api.PostedBatches.Count);
        Assert.Equal(1, result.MeteringPointsExisting);
        Assert.Equal(0, result.MeteringPointsFailed);
        Assert.True(result.MeteringPointIds.ContainsKey(Mp1));
        Assert.Equal(1, result.CustomerLinks);
    }

    [Fact]
    public async Task A_batch_with_both_a_duplicate_and_a_genuine_rejection_separates_the_two()
    {
        var api = new RecordingClients { RejectionsUseSettlBody = true };
        api.Conflicting.Add(Mp2);
        api.Rejected.Add(Mp3);
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4)],
            MakeMps(Mp1, Mp2, Mp3, Mp4), MakeContext(batchSize: 4), default);

        Assert.Equal(1, result.AlreadyExisting);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Failed);
        // The postal-code rejection still comes out naming the metering point it is about.
        var error = Assert.Single(result.Errors);
        Assert.Contains(Mp3, error);
        Assert.Contains("PostalCodeObj", error);
    }

    [Fact]
    public async Task A_duplicate_Settl_reports_under_an_id_we_never_sent_is_still_not_a_failure()
    {
        var api = new RecordingClients { ConflictsUseUnknownIds = true };
        api.Conflicting.Add(Mp3);
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4)],
            MakeMps(Mp1, Mp2, Mp3, Mp4), MakeContext(batchSize: 4), default);

        // Nothing to attribute the rejection to, so the halving runs — but the metering point that
        // ends up alone is classified as already there, not as failed.
        Assert.Equal(
            [[Mp1, Mp2, Mp3, Mp4], [Mp1, Mp2], [Mp3, Mp4], [Mp3], [Mp4]],
            api.PostedBatches);
        Assert.Equal(1, result.AlreadyExisting);
        Assert.Equal(3, result.Created);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Every_metering_point_comes_back_with_what_became_of_it()
    {
        var api = new RecordingClients { RejectionsUseSettlBody = true };
        api.Preexisting.Add(Mp2);     // the lookup finds it
        api.Conflicting.Add(Mp4);     // the create finds it
        api.Rejected.Add(Mp5);        // Settl will not have it
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4, Mp5, Mp6)],
            MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5, Mp6), MakeContext(batchSize: 6), default);

        // One entry per metering point the phase took on, in the order it took them.
        Assert.Equal([Mp1, Mp2, Mp3, Mp4, Mp5, Mp6],
            result.MeteringPoints.Select(m => m.MeteringPointIdentification));

        var byId = result.MeteringPoints.ToDictionary(m => m.MeteringPointIdentification);
        Assert.Equal(SettlMeteringPointStatus.Created, byId[Mp1].Status);
        Assert.Equal(SettlMeteringPointStatus.AlreadyExisted, byId[Mp2].Status);
        Assert.Equal(SettlMeteringPointStatus.Created, byId[Mp3].Status);
        // Found by the create rather than by the lookup — same answer.
        Assert.Equal(SettlMeteringPointStatus.AlreadyExisted, byId[Mp4].Status);
        Assert.Equal(SettlMeteringPointStatus.Failed, byId[Mp5].Status);
        Assert.Equal(SettlMeteringPointStatus.Created, byId[Mp6].Status);

        // Created and already-existing both mean "Settl has it"; the refused one does not.
        Assert.All(result.MeteringPoints.Where(m => m.MeteringPointIdentification != Mp5),
            m => Assert.True(m.Landed));
        Assert.False(byId[Mp5].Landed);

        // Enough to show a human why, without going back to the run's error list.
        Assert.Contains("PostalCodeObj", byId[Mp5].Error!);
        Assert.All(result.MeteringPoints.Where(m => m.Status != SettlMeteringPointStatus.Failed),
            m => Assert.Null(m.Error));
    }

    [Fact]
    public async Task The_outcome_per_metering_point_tallies_to_the_counters()
    {
        var api = new RecordingClients();
        api.Preexisting.Add(Mp2);
        api.Conflicting.Add(Mp4);
        api.Rejected.Add(Mp5);
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4, Mp5, Mp6)],
            MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5, Mp6), MakeContext(batchSize: 2), default);

        Assert.Equal(result.Considered, result.MeteringPoints.Count);
        // A metering point Settl accepted but had not surfaced again counts as created here, so the
        // two read-back outcomes are added up together.
        Assert.Equal(result.Created + result.Unresolved,
            result.MeteringPoints.Count(m => m.Status == SettlMeteringPointStatus.Created));
        Assert.Equal(result.AlreadyExisting,
            result.MeteringPoints.Count(m => m.Status == SettlMeteringPointStatus.AlreadyExisted));
        Assert.Equal(result.Failed,
            result.MeteringPoints.Count(m => m.Status == SettlMeteringPointStatus.Failed));
        // The phase ran to the end, so it has an answer for every one of them.
        Assert.DoesNotContain(result.MeteringPoints,
            m => m.Status == SettlMeteringPointStatus.NotAttempted);
        // And the counters themselves are untouched by any of this.
        Assert.Equal(3, result.Created);
        Assert.Equal(2, result.AlreadyExisting);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void A_metering_point_the_phase_never_settled_comes_back_as_not_attempted()
    {
        var settled = new Dictionary<string, SettlMeteringPointOutcome>
        {
            [Mp1] = new(Mp1, SettlMeteringPointStatus.Created)
        };

        var outcomes = SettlClient.BuildMeteringPointOutcomes([Mp1, Mp2], settled);

        Assert.Equal([Mp1, Mp2], outcomes.Select(o => o.MeteringPointIdentification));
        Assert.Equal(SettlMeteringPointStatus.Created, outcomes[0].Status);
        // A run that stopped before reaching this one says so — it is not left out, and it does not
        // read as fine just because nothing failed.
        Assert.Equal(SettlMeteringPointStatus.NotAttempted, outcomes[1].Status);
        Assert.False(outcomes[1].Landed);
        Assert.Null(outcomes[1].Error);
    }

    [Fact]
    public async Task A_run_with_nothing_to_do_says_nothing_rather_than_reporting_success()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);

        var dryRun = await client.PrecreateMeteringPointsAsync(
            [customer], mps, MakeContext(batchSize: 10) with { DryRun = true }, default);

        Assert.Empty(dryRun.MeteringPoints);
    }

    [Fact]
    public async Task MigrateAsync_takes_the_guid_from_the_phase_and_creates_nothing()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([customer], mps, context, default);
        var precreatedGuid = api.CreatedGuidByIdentification[Mp1];
        var postsBefore = api.PostedBatches.Count;

        var result = await client.MigrateAsync(customer, mps, context, default);

        Assert.Equal(postsBefore, api.PostedBatches.Count);           // no second create
        Assert.Equal(precreatedGuid, result.MeteringPointIds[Mp1]);   // the phase's guid is used
        Assert.Equal(1, result.MeteringPointsCreated);
        Assert.Equal(0, result.MeteringPointsExisting);
        Assert.Equal(0, result.MeteringPointsFailed);
        Assert.Equal(1, result.CustomerLinks);
    }

    [Fact]
    public async Task A_metering_point_shared_by_two_accounts_counts_created_once_then_existing()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var first = MakeCustomer("352639", Mp1);
        var second = MakeCustomer("352640", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([first, second], mps, context, default);

        var firstResult = await client.MigrateAsync(first, mps, context, default);
        var secondResult = await client.MigrateAsync(second, mps, context, default);

        Assert.Equal(1, firstResult.MeteringPointsCreated);
        Assert.Equal(0, firstResult.MeteringPointsExisting);
        Assert.Equal(0, secondResult.MeteringPointsCreated);
        Assert.Equal(1, secondResult.MeteringPointsExisting);
        Assert.Equal([Mp1], secondResult.ExistingMeteringPointIdentifications!);
        // Same guid both times, and only one create in total.
        Assert.Equal(firstResult.MeteringPointIds[Mp1], secondResult.MeteringPointIds[Mp1]);
        Assert.Single(api.PostedBatches);
    }

    [Fact]
    public async Task A_metering_point_the_phase_failed_falls_back_to_the_per_customer_path()
    {
        var api = new RecordingClients();
        api.Rejected.Add(Mp1);
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([customer], mps, context, default);
        api.Rejected.Clear();  // whatever Settl choked on is gone by the time the customer runs

        var result = await client.MigrateAsync(customer, mps, context, default);

        // Not silently omitted: the customer loop created it itself.
        Assert.Equal(1, result.MeteringPointsCreated);
        Assert.Equal(0, result.MeteringPointsFailed);
        Assert.True(result.MeteringPointIds.ContainsKey(Mp1));
    }

    [Fact]
    public async Task Dry_run_skips_the_phase_and_leaves_MigrateAsync_untouched()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10) with { DryRun = true };

        var phase = await client.PrecreateMeteringPointsAsync([customer], mps, context, default);

        Assert.Equal(SettlMeteringPointPrecreateResult.Empty, phase);
        Assert.Empty(api.PostedBatches);
        Assert.Equal(0, api.SearchCalls);   // the phase did not even look

        var result = await client.MigrateAsync(customer, mps, context, default);

        Assert.Empty(api.PostedBatches);                  // dry-run still writes nothing
        Assert.Equal(1, result.MeteringPointsCreated);    // and still reports would-create
        Assert.Equal(1, result.CustomerLinks);
    }

    [Fact]
    public async Task Capture_mode_is_unaffected_by_the_phase()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([customer], mps, context, default);
        var postsAfterPhase = api.PostedBatches.Count;

        var plan = await client.CapturePlanAsync(customer, mps, context, default);

        Assert.Equal(postsAfterPhase, api.PostedBatches.Count);   // capture writes nothing
        var planned = Assert.Single(plan.MeteringPoints);
        Assert.Equal(Mp1, planned.Identification);
        // Capture stays independent of live Settl state — it plans its own id, it does not adopt
        // the guid the phase got back.
        Assert.NotEqual(api.CreatedGuidByIdentification[Mp1], planned.Master.Id);
    }

    [Fact]
    public async Task Concurrent_lookups_produce_the_same_batches_and_counts_as_sequential_ones()
    {
        var customers = new List<Customer> { MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4, Mp5, Mp6) };
        var mps = MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5, Mp6);

        var sequentialApi = new RecordingClients();
        sequentialApi.Preexisting.Add(Mp2);
        sequentialApi.Preexisting.Add(Mp5);
        var sequential = await NewClient(sequentialApi).PrecreateMeteringPointsAsync(
            customers, mps, MakeContext(batchSize: 2, searchConcurrency: 1), default);

        var concurrentApi = new RecordingClients();
        concurrentApi.Preexisting.Add(Mp2);
        concurrentApi.Preexisting.Add(Mp5);
        var concurrent = await NewClient(concurrentApi).PrecreateMeteringPointsAsync(
            customers, mps, MakeContext(batchSize: 2, searchConcurrency: 8), default);

        // Same MPs in the same batches, in the same order — pending was folded back in order.
        Assert.Equal(sequentialApi.PostedBatches, concurrentApi.PostedBatches);
        Assert.Equal([[Mp1, Mp3], [Mp4, Mp6]], concurrentApi.PostedBatches);
        Assert.Equal(sequential.AlreadyExisting, concurrent.AlreadyExisting);
        Assert.Equal(2, concurrent.AlreadyExisting);
        Assert.Equal(sequential.Created, concurrent.Created);
        Assert.Equal(sequential.Failed, concurrent.Failed);
        Assert.Equal(sequential.Batches, concurrent.Batches);
        Assert.Equal(sequential.Unresolved, concurrent.Unresolved);
        Assert.Equal(sequentialApi.SearchCalls, concurrentApi.SearchCalls);
    }

    [Fact]
    public async Task A_search_concurrency_of_one_keeps_the_lookups_strictly_sequential()
    {
        var customers = new List<Customer> { MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4, Mp5, Mp6) };
        var mps = MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5, Mp6);

        var api = new RecordingClients { SearchDelayMs = 20 };
        await NewClient(api).PrecreateMeteringPointsAsync(
            customers, mps, MakeContext(batchSize: 10, searchConcurrency: 1), default);

        Assert.Equal(1, api.MaxConcurrentSearches);
        // Existence check first, in exactly the order the MPs were collected.
        Assert.Equal([Mp1, Mp2, Mp3, Mp4, Mp5, Mp6], api.SearchOrder.Take(6));
    }

    [Fact]
    public async Task A_higher_search_concurrency_actually_overlaps_the_lookups()
    {
        var customers = new List<Customer> { MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4, Mp5, Mp6) };
        var mps = MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5, Mp6);

        var api = new RecordingClients { SearchDelayMs = 20 };
        var result = await NewClient(api).PrecreateMeteringPointsAsync(
            customers, mps, MakeContext(batchSize: 10, searchConcurrency: 4), default);

        Assert.True(api.MaxConcurrentSearches > 1,
            $"expected overlapping lookups, saw at most {api.MaxConcurrentSearches} at a time");
        Assert.True(api.MaxConcurrentSearches <= 4,
            $"concurrency was supposed to be capped at 4, saw {api.MaxConcurrentSearches}");
        Assert.Equal(6, result.Created);
    }

    [Fact]
    public async Task The_first_read_back_pass_happens_without_waiting()
    {
        var api = new RecordingClients();
        // A retry delay long enough that a wait before the first look would be unmistakable.
        var client = NewClient(api, searchRetryDelayMs: 3000);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2)], MakeMps(Mp1, Mp2), MakeContext(batchSize: 10), default);
        started.Stop();

        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Unresolved);
        Assert.True(started.ElapsedMilliseconds < 1000,
            $"read-back waited {started.ElapsedMilliseconds} ms before its first look");
    }

    [Fact]
    public async Task A_just_created_metering_point_gets_its_billing_written_without_a_read()
    {
        var api = new RecordingClients();
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([customer], mps, context, default);
        await client.MigrateAsync(customer, mps, context, default);

        Assert.Equal(0, api.BillingReads);
        var (_, request) = Assert.Single(api.BillingWrites);
        Assert.NotNull(request.BillingConfiguration);
        Assert.Equal("DKK", request.BillingConfiguration!.Currency);
        Assert.True(request.BillingConfiguration.JointInvoice);
        // The MP carries a per-MP invoicing override, so that is written too — unchanged.
        Assert.NotNull(request.PaymentConfiguration);
    }

    [Fact]
    public async Task A_metering_point_that_already_existed_still_has_its_billing_read_first()
    {
        var api = new RecordingClients();
        api.Preexisting.Add(Mp1);
        var client = NewClient(api);
        var customer = MakeCustomer("352639", Mp1);
        var mps = MakeMps(Mp1);
        var context = MakeContext(batchSize: 10);

        await client.PrecreateMeteringPointsAsync([customer], mps, context, default);
        var result = await client.MigrateAsync(customer, mps, context, default);

        Assert.Equal(1, result.MeteringPointsExisting);
        // The backfill path is untouched: read first, then write what differs.
        Assert.Equal(1, api.BillingReads);
        Assert.Single(api.BillingWrites);
    }

    [Fact]
    public async Task Without_id_resolution_the_phase_stops_at_the_create()
    {
        var api = new RecordingClients();
        // A retry delay that would be obvious if any read-back pass ran at all.
        var client = NewClient(api, searchRetryDelayMs: 3000);
        var customers = new List<Customer> { MakeCustomer("352639", Mp1, Mp2, Mp3) };

        var result = await client.PrecreateMeteringPointsAsync(
            customers, MakeMps(Mp1, Mp2, Mp3),
            MakeContext(batchSize: 2) with { ResolveCreatedMeteringPointIds = false }, default);

        // Only the existence check looked — one lookup per MP, none after the create.
        Assert.Equal(3, api.SearchCalls);
        Assert.Equal([Mp1, Mp2, Mp3], api.SearchOrder);
        Assert.Equal([[Mp1, Mp2], [Mp3]], api.PostedBatches);
        // Settl accepted all three, so all three are created — and nothing is awaiting Settl.
        Assert.Equal(3, result.Created);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Without_id_resolution_a_metering_point_Settl_refuses_is_still_counted_as_failed()
    {
        var api = new RecordingClients();
        api.Rejected.Add(Mp3);
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4)], MakeMps(Mp1, Mp2, Mp3, Mp4),
            MakeContext(batchSize: 4) with { ResolveCreatedMeteringPointIds = false }, default);

        Assert.Equal(1, result.Failed);
        Assert.Equal(3, result.Created);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal($"MP {Mp3} create failed: boom", result.Errors.Single());
        // Still only the four existence checks — the halving retries post, they do not search.
        Assert.Equal(4, api.SearchCalls);
    }

    [Fact]
    public async Task Id_resolution_is_on_unless_the_caller_says_otherwise()
    {
        var api = new RecordingClients();
        var client = NewClient(api);

        var result = await client.PrecreateMeteringPointsAsync(
            [MakeCustomer("352639", Mp1)], MakeMps(Mp1), MakeContext(batchSize: 10), default);

        Assert.True(MakeContext(batchSize: 10).ResolveCreatedMeteringPointIds);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, api.SearchCalls);   // existence check, then the read-back
    }

    [Fact]
    public async Task The_progress_callback_moves_once_per_batch_and_lands_on_the_summary_numbers()
    {
        var api = new RecordingClients();
        api.Preexisting.Add(Mp2);
        api.Rejected.Add(Mp6);
        var client = NewClient(api);
        var customers = new List<Customer> { MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4, Mp5, Mp6) };

        var progress = new List<(int Settled, int Total)>();
        var result = await client.PrecreateMeteringPointsAsync(
            customers, MakeMps(Mp1, Mp2, Mp3, Mp4, Mp5, Mp6), MakeContext(batchSize: 2), default,
            (settled, total) => progress.Add((settled, total)));

        // Mp2 already existed, so four creates in batches of two — plus the opening call and the
        // one that reports the already-existing MPs: at least one call per batch.
        Assert.Equal(3, result.Batches);
        Assert.True(progress.Count >= result.Batches + 1,
            $"expected at least {result.Batches + 1} progress calls, saw {progress.Count}");

        // Total never changes, and it is the number the phase set out to handle.
        Assert.All(progress, p => Assert.Equal(result.Considered, p.Total));
        Assert.Equal(6, result.Considered);

        // Starts at nothing settled, never goes backwards.
        Assert.Equal(0, progress[0].Settled);
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i].Settled >= progress[i - 1].Settled,
                $"progress went backwards: {progress[i - 1].Settled} → {progress[i].Settled}");

        // And it ends exactly where the summary ends: created + already existing + failed.
        Assert.Equal(4, result.Created);
        Assert.Equal(1, result.AlreadyExisting);
        Assert.Equal(1, result.Failed);
        Assert.Equal(
            result.Created + result.AlreadyExisting + result.Failed,
            progress[^1].Settled);
    }

    [Fact]
    public async Task Without_a_callback_the_phase_behaves_exactly_as_before()
    {
        var withApi = new RecordingClients();
        withApi.Preexisting.Add(Mp2);
        var customers = new List<Customer> { MakeCustomer("352639", Mp1, Mp2, Mp3, Mp4) };
        var mps = MakeMps(Mp1, Mp2, Mp3, Mp4);

        var withResult = await NewClient(withApi).PrecreateMeteringPointsAsync(
            customers, mps, MakeContext(batchSize: 2), default, (_, _) => { });

        var withoutApi = new RecordingClients();
        withoutApi.Preexisting.Add(Mp2);
        var withoutResult = await NewClient(withoutApi).PrecreateMeteringPointsAsync(
            customers, mps, MakeContext(batchSize: 2), default);

        Assert.Equal(withResult with { Errors = [], Warnings = [], MeteringPoints = [] },
            withoutResult with { Errors = [], Warnings = [], MeteringPoints = [] });
        Assert.Equal(withResult.MeteringPoints, withoutResult.MeteringPoints);
        Assert.Equal(withApi.PostedBatches, withoutApi.PostedBatches);
        Assert.Equal(withApi.SearchOrder, withoutApi.SearchOrder);
        Assert.Equal(withApi.SearchCalls, withoutApi.SearchCalls);
    }

    // === helpers ===

    private static SettlClient NewClient(
        RecordingClients api, int searchRetryDelayMs = 0, Action<string>? log = null) =>
        new(api, api, api, api, api, deliveryCategory: "El-ekstern", logLine: log ?? (_ => { }),
            searchRetryDelayMs: searchRetryDelayMs);

    private static SettlMigrationContext MakeContext(int batchSize, int searchConcurrency = 8) => new(
        DryRun: false,
        ChangeOfSupplierDate: new DateTime(2024, 1, 1),
        MpContractLinks: [],
        ExemptionContext: null,
        SettlementGroupMap: null,
        OwnerByCustomerNumber: null,
        MeteringPointCreateBatchSize: batchSize,
        SearchConcurrency: searchConcurrency);

    private static Dictionary<string, MeteringPoint> MakeMps(params string[] ids) =>
        ids.ToDictionary(id => id, MakeElectricityMp);

    private static Customer MakeCustomer(string accountNumber, params string[] mpIds)
    {
        var address = new Address("Rådhusvej", "3", "", "", "3200", "Helsinge", "", "", "", "DK", Dar: null!);
        var contact = new ContactInfo("test@example.com", "", "", "");

        var assignments = mpIds
            .Select(id => new MeteringPointAssignment(
                MeteringPointId: id,
                Product: null!,
                Start: new DateOnly(2024, 1, 1),
                EndDate: DateOnly.FromDateTime(MigrationConstants.EndDateNull),
                AmrCode: ""))
            .ToList();

        var contract = new Contract(
            Number: $"C-{accountNumber}", Description: "Test", Start: new DateOnly(2024, 1, 1),
            Commodity: Commodity.Electricity, CustomerNumber: accountNumber,
            Billing: new BillingSchedule(BillingFrequency.Monthly, "MND"),
            MeteringPoints: assignments, PaymentTerm: null,
            BuyerInvoicing: new BuyerInvoicingReferences("5798008161063", null, null, null));

        return new Customer(
            AccountNumber: accountNumber, Name: "Gribskov Kommune", AdditionalName: "",
            Type: CustomerType.Company, Cvr: "29188440", Cpr: "",
            Address: address, Contact: contact,
            Billing: new CustomerBilling(DefaultEan: "5798008161063", Pbs: null, MobilePay: null),
            Contracts: [contract])
        {
            Payers =
            [
                new Payer(
                    Identifier: accountNumber,
                    IsPrimary: true,
                    ContractNumbers: [$"C-{accountNumber}"],
                    BuyerInvoicing: new BuyerInvoicingReferences("5798008161063", null, null, null),
                    InvoiceChannel: InvoiceChannel.OioUbl,
                    PaymentChannel: PaymentChannel.EInvoiceFik)
            ]
        };
    }

    private static MeteringPoint MakeElectricityMp(string identification) => new(
        Identification: identification,
        InstallationAddress: new Address("Rådhusvej", "3", null, null, "3200", "Helsinge", "0001", "Helsinge", "270", "DK", Dar: null!),
        Commodity: Commodity.Electricity,
        Meter: new Meter("M-1", 1m, ReadingInterval: null),
        ContactPoints: [],
        JointInvoiceGroupId: "JIG-1",
        ConnectionStatus: ConnectionStatus.Connected,
        InDelivery: true,
        Grid: new GridConnection(PriceArea.Dk2, "031", SettlementMethod.Flex, MeteringPointKind.Consumption, ""),
        Start: new DateOnly(2024, 1, 1),
        EstimatedAnnualVolumeKwh: 5000,
        BillingName: "Gribskov Kommune",
        BuyerInvoicing: new BuyerInvoicingReferences("5798008161063", "ORD-1", null, null));

    /// <summary>
    /// Settl stand-in: records every v4 create batch, lets a chosen MP reject the batch it lands in
    /// (that is how Settl behaves — the whole call fails), and can pretend an MP was already there.
    /// </summary>
    private sealed class RecordingClients
        : ISettlCustomerClient, ISettlPayerClient, ISettlMeteringPointClient, ISettlContractClient, ISettlSettlementGroupClient
    {
        /// <summary>MP identifications that make the create call they are part of fail.</summary>
        public HashSet<string> Rejected { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// MP identifications Settl answers "already exist" for — the 409 a metering point that is
        /// in Settl but did not surface in the lookup produces.
        /// </summary>
        public HashSet<string> Conflicting { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Report the conflict under an aggregate id the run never sent, for the case where Settl's
        /// id is its own rather than the one in the payload.
        /// </summary>
        public bool ConflictsUseUnknownIds { get; set; }

        /// <summary>
        /// Answer a genuine rejection with Settl's own error body (the postal code it does not know)
        /// instead of the short "boom" the older tests use.
        /// </summary>
        public bool RejectionsUseSettlBody { get; set; }

        /// <summary>MP identifications that are already in Settl before the run.</summary>
        public HashSet<string> Preexisting { get; } = new(StringComparer.Ordinal);

        /// <summary>One entry per v4 create call, in order, holding the identifications it carried.</summary>
        public List<List<string>> PostedBatches { get; } = [];

        public Dictionary<string, Guid> CreatedGuidByIdentification { get; } = new(StringComparer.Ordinal);

        public int SearchCalls { get; private set; }

        /// <summary>Every identification looked up, in the order the calls actually arrived.</summary>
        public List<string> SearchOrder { get; } = [];

        /// <summary>Highest number of lookups that were in flight at the same moment.</summary>
        public int MaxConcurrentSearches { get; private set; }

        /// <summary>Makes each lookup take time, so overlapping calls are actually observable.</summary>
        public int SearchDelayMs { get; set; }

        /// <summary>How many times the billing config was read back.</summary>
        public int BillingReads { get; private set; }

        /// <summary>One entry per billing write, in order.</summary>
        public List<(Guid MeteringPointId, UpdateBillingMeteringPointRequest Request)> BillingWrites { get; } = [];

        private readonly object _gate = new();
        private int _inFlight;

        // --- Metering point ---
        public void SetDeliveryCategory(string deliveryCategory) { }

        public Task<(bool, string?)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("v3 create is retired — creates must POST v4");

        public Task<(bool, string?)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken ct = default)
        {
            var ids = request.MeteringPointCreateObjectDtos
                .Select(m => m.Identification!.MeteringPointIdentification!)
                .ToList();
            PostedBatches.Add(ids);

            var conflicts = request.MeteringPointCreateObjectDtos
                .Where(m => Conflicting.Contains(m.Identification!.MeteringPointIdentification!))
                .ToList();
            var refused = request.MeteringPointCreateObjectDtos
                .Where(m => Rejected.Contains(m.Identification!.MeteringPointIdentification!))
                .ToList();

            // A real 409 body, so the client has to read it the way it reads Settl's.
            if (conflicts.Count > 0 || (refused.Count > 0 && RejectionsUseSettlBody))
                return Task.FromResult<(bool, string?)>((false, RejectionBody(conflicts, refused)));

            if (refused.Count > 0)
                return Task.FromResult<(bool, string?)>((false, "boom"));

            foreach (var mp in request.MeteringPointCreateObjectDtos)
                CreatedGuidByIdentification[mp.Identification!.MeteringPointIdentification!] = mp.Id;
            return Ok();
        }

        public async Task<(MeteringPointInfoDto?, string?)> SearchByIdentificationAsync(string ident, CancellationToken ct = default)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            try
            {
                lock (_gate)
                {
                    SearchCalls++;
                    SearchOrder.Add(ident);
                    if (inFlight > MaxConcurrentSearches) MaxConcurrentSearches = inFlight;
                }

                if (SearchDelayMs > 0) await Task.Delay(SearchDelayMs, ct);

                lock (_gate)
                {
                    if (Preexisting.Contains(ident) && !CreatedGuidByIdentification.ContainsKey(ident))
                        CreatedGuidByIdentification[ident] = Guid.NewGuid();
                    return CreatedGuidByIdentification.TryGetValue(ident, out var id)
                        ? (new MeteringPointInfoDto { Id = id, MeteringPointIdentification = ident }, null)
                        : ((MeteringPointInfoDto?)null, (string?)null);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<(bool, string?)> CreateWaterAsync(CreateWaterMeteringPointRequest request, CancellationToken ct = default) => Ok();
        public Task<(MeteringPointInfoDto?, string?)> SearchWaterByIdentificationAsync(string ident, CancellationToken ct = default) => Null<MeteringPointInfoDto>();
        public Task<(MeteringPointInfoDto?, string?)> GetWaterByIdAsync(Guid id, CancellationToken ct = default) => Null<MeteringPointInfoDto>();
        public Task<(BillingMeteringPointCurrentDto?, string?)> GetBillingByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default)
        {
            lock (_gate) BillingReads++;
            return Null<BillingMeteringPointCurrentDto>();
        }

        public Task<(bool, string?)> UpdateBillingAsync(Guid id, UpdateBillingMeteringPointRequest request, CancellationToken ct = default)
        {
            lock (_gate) BillingWrites.Add((id, request));
            return Ok();
        }
        public Task<(MeteringPointCurrentDto?, string?)> GetByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default) => Null<MeteringPointCurrentDto>();
        public Task<(GetDeliveryStatusesResponse?, string?)> GetDeliveryStatusesAsync(Guid id, CancellationToken ct = default) => Null<GetDeliveryStatusesResponse>();
        public Task<(string?, string?)> GetDeliveryStatusesRawAsync(Guid id, CancellationToken ct = default) => Null<string>();
        public Task<(string?, string?)> GetByIdRawAsync(Guid id, CancellationToken ct = default) => Null<string>();
        public Task<(bool, string?)> UpdateAsync(Guid id, MeteringPointDto dto, CancellationToken ct = default) => Ok();
        public Task<(BillingMeteringPointInfoDto?, string?)> SearchBillingByIdentificationAsync(string ident, CancellationToken ct = default) => Null<BillingMeteringPointInfoDto>();
        public Task<(BillingMeteringPointHistoryResponse?, string?)> GetBillingHistoryAsync(Guid id, CancellationToken ct = default) => Null<BillingMeteringPointHistoryResponse>();
        public Task<(MeteringPointV3HistoryResponse?, string?)> GetMarketHistoryAsync(Guid id, CancellationToken ct = default) => Null<MeteringPointV3HistoryResponse>();
        public Task<(MeteringPointV4Dto?, string?)> GetByIdV4Async(Guid id, DateTime? d = null, CancellationToken ct = default) => Null<MeteringPointV4Dto>();
        public Task<(List<MarketBalanceAreaInfoDto>?, string?)> FindMarketBalanceAreasAsync(CancellationToken ct = default) => Task.FromResult<(List<MarketBalanceAreaInfoDto>?, string?)>(([], null));
        public Task<(List<MeteringGridAreaInfoDto>?, string?)> FindMeteringGridAreasAsync(CancellationToken ct = default) => Task.FromResult<(List<MeteringGridAreaInfoDto>?, string?)>(([], null));
        public Task<(List<BillingMeteringPointInfoDto>, int, string?)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<BillingMeteringPointInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<MeteringPointInfoDto>, int, string?)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken ct = default) => Task.FromResult<(List<MeteringPointInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<MeteringPointInfoDto>, string?)> FindWaterMeteringPointsAsync(CancellationToken ct = default) => Task.FromResult<(List<MeteringPointInfoDto>, string?)>(([], null));
        public Task<(bool, string?)> UpdateAnnualConsumptionsAsync(Guid id, double v, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> UpdateEstimatedAnnualVolumeAsync(Guid id, double v, DateOnly effectiveStart, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> AddExemptionsAsync(Guid id, AddMeteringPointExemptionsRequest request, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> BlankDuplicateSecondConsumerNameAsync(Guid id, CancellationToken ct = default) => Ok();

        // --- Customer ---
        public Task<(bool, string?)> CreateAsync(CreateCustomersRequest request, CancellationToken ct = default) => Ok();
        public Task<(CustomerInfoDto?, string?)> SearchByCustomerNumberAsync(string n, string c = "DK", CancellationToken ct = default) => Null<CustomerInfoDto>();
        public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddCustomerMeteringPointsRequest request, CancellationToken ct = default) => Ok();
        public Task<(CustomerResponse?, string?)> GetByIdAsync(Guid id, CancellationToken ct = default) => Null<CustomerResponse>();
        public Task<(string?, string?)> GetRawByIdV3Async(Guid id, CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<(bool, string?)> UpdateV3RawAsync(Guid id, string json, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> UpdateNationalIdentificationNumberV3Async(Guid id, string nin, CancellationToken ct = default) => Ok();
        public Task<(List<CustomerMeteringPointDto>?, string?)> GetMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<CustomerMeteringPointDto>?, string?)>(([], null));
        public Task<(List<CustomerInfoDto>, int, string?)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<CustomerInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<CustomerInfoDto>, int, string?)> SearchCustomersAsync(string? f = null, string? n = null, string? v = null, int limit = 50, CancellationToken ct = default) => Task.FromResult<(List<CustomerInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<CustomerOrganisationMeteringPointDto>?, string?)> GetOrganisationMeteringPointsAsync(Guid id, CancellationToken ct = default) => Null<List<CustomerOrganisationMeteringPointDto>>();
        public Task<(CustomerHistoryResponse?, string?)> GetHistoryAsync(Guid id, CancellationToken ct = default) => Null<CustomerHistoryResponse>();
        public Task<(List<CustomerSegmentDto>?, string?)> FindCustomerSegmentsAsync(CancellationToken ct = default) => Null<List<CustomerSegmentDto>>();
        public Task<(SearchCustomerPriceListResponse?, string?)> SearchPriceListsAsync(Guid id, SearchCustomerPriceListRequest? req = null, CancellationToken ct = default) => Null<SearchCustomerPriceListResponse>();
        public Task<(List<ExtraInvoiceLineDto>, int, string?)> GetExtraInvoiceLinesAsync(Guid id, int limit = 50, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<ExtraInvoiceLineDto>, int, string?)>(([], 0, null));

        // --- Payer ---
        public Task<(bool, string?)> CreateAsync(CreatePayersRequest request, CancellationToken ct = default) => Ok();
        public Task<(PayerInfoDto?, string?)> SearchByPayerIdentificationAsync(string id, string c = "DK", CancellationToken ct = default) => Null<PayerInfoDto>();
        public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddPayerMeteringPointsRequest request, CancellationToken ct = default) => Ok();
        Task<(PayerResponse?, string?)> ISettlPayerClient.GetByIdAsync(Guid id, DateTime? effectiveDate, CancellationToken ct) => Null<PayerResponse>();
        Task<(PayerHistoryResponse?, string?)> ISettlPayerClient.GetHistoryAsync(Guid id, CancellationToken ct) => Null<PayerHistoryResponse>();
        Task<(List<PayerMeteringPointDto>?, string?)> ISettlPayerClient.GetMeteringPointsAsync(Guid id, CancellationToken ct) => Task.FromResult<(List<PayerMeteringPointDto>?, string?)>(([], null));
        public Task<(string?, string?)> GetRawByIdAsync(Guid id, CancellationToken ct = default) => Null<string>();
        public Task<(bool, string?)> UpdateRawAsync(Guid id, string json, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> UpdatePayerBillingAsync(Guid id, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> patches, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> DeleteAsync(Guid id, CancellationToken ct = default) => Ok();
        public Task<(List<PayerInfoDto>, int, string?)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<PayerInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<PayerInfoDto>?, string?)> FindPayersByCustomerAsync(Guid id, string c = "DK", CancellationToken ct = default) => Null<List<PayerInfoDto>>();
        public Task<(List<PayerForMeteringPointsDto>?, string?)> FindPayersForMeteringPointsAsync(IReadOnlyList<Guid> ids, DateTime? d = null, CancellationToken ct = default) => Null<List<PayerForMeteringPointsDto>>();

        // --- Contract ---
        public Task<(List<ContractInfoDto>?, string?)> FindContractsAsync(string? commodityType = null, CancellationToken ct = default) => Null<List<ContractInfoDto>>();
        public Task<(List<ContractMeteringPointInfoDto>?, string?)> GetContractMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<ContractMeteringPointInfoDto>?, string?)>(([], null));
        public Task<(List<ContractInfoDto>?, string?)> FindContractsByMeteringPointAsync(Guid meteringPointId, CancellationToken ct = default) => Null<List<ContractInfoDto>>();
        public Task<(List<ContractLineInfoDto>?, string?)> GetContractLinesAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<ContractLineInfoDto>?, string?)>(([], null));
        public Task<(List<CustomerContractWithMeteringPointsDto>?, string?)> GetCustomerMeteringPointContractsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<CustomerContractWithMeteringPointsDto>?, string?)>(([], null));
        public Task<(bool, string?)> AddContractMeteringPointsAsync(Guid id, AddContractMeteringPointsRequest request, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> RemoveContractMeteringPointAsync(Guid contractId, Guid linkId, CancellationToken ct = default) => Task.FromResult<(bool, string?)>((false, "simulated failure"));

        // --- Settlement group ---
        public Task<(SettlementGroupInfoDto?, string?)> FindByNameAsync(string c, string n, CancellationToken ct = default) => Null<SettlementGroupInfoDto>();
        public Task<(List<SettlementGroupInfoDto>?, string?)> FindByMeteringPointAsync(Guid id, CancellationToken ct = default) => Null<List<SettlementGroupInfoDto>>();
        public Task<(List<SettlementGroupInfoDto>?, string?)> ListAllAsync(string c, CancellationToken ct = default) => Null<List<SettlementGroupInfoDto>>();
        public Task<(HashSet<Guid>?, string?)> ListMeteringPointIdsAsync(Guid id, CancellationToken ct = default) => Null<HashSet<Guid>>();
        public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddSettlementGroupMeteringPointsRequest request, CancellationToken ct = default) => Ok();
        public Task<(HashSet<Guid>?, string?)> ListCustomerIdsAsync(Guid id, CancellationToken ct = default) => Null<HashSet<Guid>>();
        public Task<(bool, string?)> AddCustomersAsync(Guid id, AddSettlementGroupCustomersRequest request, CancellationToken ct = default) => Ok();

        /// <summary>
        /// Settl's rejection body, verbatim in shape: one entry per metering point it refused, each
        /// with the aggregate id it concerns and a message.
        /// </summary>
        private string RejectionBody(
            List<MeteringPointCreateObjectV4Dto> conflicts,
            List<MeteringPointCreateObjectV4Dto> refused)
        {
            var entries = new List<string>();

            foreach (var mp in conflicts)
            {
                // Either the id we sent, or an id only Settl knows — the client must cope with both.
                var id = ConflictsUseUnknownIds ? Guid.NewGuid() : mp.Id;
                entries.Add(Entry($"Aggregate '{id}' (type MeteringPointAggregate) already exist.", id));
            }

            foreach (var mp in refused)
                entries.Add(Entry(
                    "Argument of type PostalCodeObj must not be null AdditionalInfo: Combination of "
                    + "postal code: 1954 and city: Frederiksberg C does not exist in DK", mp.Id));

            return $$"""HTTP 409: {"Errors":[{{string.Join(",", entries)}}]}""";
        }

        private static string Entry(string text, Guid id) =>
            $$"""{"Message":{"Identifier":"BioWebService.UnknownException","Text":"{{text}}","MessageType":"Error","Params":[]},"Id":"{{id}}"}""";

        private static Task<(bool, string?)> Ok() => Task.FromResult<(bool, string?)>((true, null));
        private static Task<(T?, string?)> Null<T>() => Task.FromResult<(T?, string?)>((default, null));
    }
}
