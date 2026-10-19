using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class FixConsumer2DuplicateRunnerTests
{
    private static MeteringPointV4Dto DuplicateMp(Guid id) => new()
    {
        Id = id,
        Identification = new MeteringPointV4IdentificationDto { MeteringPointIdentification = "571313100000000002" },
        Consumer = new MeteringPointV4ConsumerDto
        {
            Start = new DateTime(2026, 1, 1),
            CustomerType = "Person",
            FirstConsumerParty = new MeteringPointV4ConsumerPartyDto { Name = new MeteringPointV4NameDto { FullName = "test" } },
            SecondConsumerParty = new MeteringPointV4ConsumerPartyDto { Name = new MeteringPointV4NameDto { FullName = "test" } },
        },
    };

    private static MeteringPointV4Dto BlankedMp(Guid id) => new()
    {
        Id = id,
        Identification = new MeteringPointV4IdentificationDto { MeteringPointIdentification = "571313100000000002" },
        Consumer = new MeteringPointV4ConsumerDto
        {
            Start = new DateTime(2026, 1, 1),
            CustomerType = "Person",
            FirstConsumerParty = new MeteringPointV4ConsumerPartyDto { Name = new MeteringPointV4NameDto { FullName = "test" } },
            SecondConsumerParty = new MeteringPointV4ConsumerPartyDto { Name = new MeteringPointV4NameDto { FullName = "" } },
        },
    };

    [Fact]
    public async Task Fix_is_counted_only_after_a_fresh_re_read_confirms_the_duplicate_is_gone()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(DuplicateMp(id)); // detect read
        client.Reads.Enqueue(DuplicateMp(id)); // eligibility recheck
        client.Reads.Enqueue(BlankedMp(id));   // post-write verify read
        client.BlankResult = (true, null);

        var runner = new FixConsumer2DuplicateRunner(client, _ => { });
        var exitCode = await runner.RunAsync(new FixConsumer2DuplicateRunOptions
        {
            MeteringPointIds = [id.ToString()],
            DryRun = false,
        });

        Assert.Equal(0, exitCode);
        Assert.Equal(1, client.BlankCalls);
        Assert.Equal(3, client.GetByIdV4Calls); // detect + verify
    }

    [Fact]
    public async Task A_write_that_reports_success_but_does_not_actually_clear_the_duplicate_is_reported_as_an_error()
    {
        // Regression for the 2026-09-08 featuretest incident: BlankDuplicateSecondConsumerNameAsync
        // returned Success=true, but a follow-up read showed the duplicate still present (a new
        // dated version had reintroduced it). The runner must not silently count this as "fixed".
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(DuplicateMp(id)); // detect read
        client.Reads.Enqueue(DuplicateMp(id)); // post-write verify read — still duplicate
        client.BlankResult = (true, null);

        var loggedLines = new List<string>();
        var runner = new FixConsumer2DuplicateRunner(client, loggedLines.Add);
        var exitCode = await runner.RunAsync(new FixConsumer2DuplicateRunOptions
        {
            MeteringPointIds = [id.ToString()],
            DryRun = false,
        });

        Assert.Equal(1, exitCode); // failed writes > 0
        Assert.Contains(loggedLines, l => l.Contains("still shows duplicate", StringComparison.Ordinal));
        Assert.DoesNotContain(loggedLines, l => l.Contains("fixed - verified", StringComparison.Ordinal));
        Assert.Contains("  Failed writes: 1", loggedLines);
        Assert.Contains("  Errors: 0", loggedLines);
    }

    [Theory]
    [InlineData("rejected", 0, 1, 0)]
    [InlineData("not-sent", 0, 0, 1)]
    [InlineData("fixed", 1, 0, 0)]
    public async Task Each_cleanup_outcome_lands_in_exactly_one_count(string outcome, int fixedVersions, int failedWrites, int errors)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(DuplicateMp(id)); // detect read
        client.Reads.Enqueue(DuplicateMp(id)); // eligibility recheck
        client.Reads.Enqueue(BlankedMp(id));   // post-write verify read
        if (outcome == "rejected") client.BlankResult = (false, "HTTP 400: Kind Local not allowed");
        if (outcome == "not-sent") client.RefuseBeforeWrite = "Consumer is no longer duplicated; no write made.";

        var logs = new List<string>();
        var exitCode = await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()] });

        Assert.Equal(fixedVersions == 1 ? 0 : 1, exitCode);
        Assert.Contains("  Matched:  1", logs);
        Assert.Contains($"  Fixed versions: {fixedVersions}", logs);
        Assert.Contains($"  Failed writes: {failedWrites}", logs);
        Assert.Contains($"  Errors: {errors}", logs);
        if (outcome == "rejected") Assert.Contains(logs, l => l.Contains("Settl rejected the write", StringComparison.Ordinal));
        if (outcome == "not-sent") Assert.Contains(logs, l => l.Contains("skipped before write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dry_run_summary_has_no_failed_writes_line()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(DuplicateMp(id));
        var logs = new List<string>();
        await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], DryRun = true });
        Assert.DoesNotContain(logs, l => l.StartsWith("  Failed writes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public async Task Full_scan_includes_all_delivery_states_in_dry_run_and_live_cleanup(bool? inDelivery, bool dryRun)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, InDelivery = inDelivery });
        client.Reads.Enqueue(DuplicateMp(id));
        client.Reads.Enqueue(DuplicateMp(id)); // eligibility recheck
        client.Reads.Enqueue(BlankedMp(id));
        using var progress = new StringWriter();
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress }
            .RunAsync(new() { DryRun = dryRun }));
        Assert.Equal(dryRun ? 1 : 3, client.GetByIdV4Calls);
        Assert.Equal(dryRun ? 0 : 1, client.BlankCalls);
        Assert.Contains(logs, line => line.Contains("Matched:  1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Full_scan_still_excludes_child_points()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = Guid.NewGuid(), InDelivery = false, ParentMeteringPoint = new() { Id = Guid.NewGuid() } });
        using var progress = new StringWriter();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, _ => { }) { ProgressOutput = progress }
            .RunAsync(new() { DryRun = true }));
        Assert.Equal(0, client.GetByIdV4Calls);
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Dry_run_never_calls_the_write_or_verify_path()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(DuplicateMp(id));

        var runner = new FixConsumer2DuplicateRunner(client, _ => { });
        var exitCode = await runner.RunAsync(new FixConsumer2DuplicateRunOptions
        {
            MeteringPointIds = [id.ToString()],
            DryRun = true,
        });

        Assert.Equal(0, exitCode);
        Assert.Equal(0, client.BlankCalls);
        Assert.Equal(1, client.GetByIdV4Calls); // detect only
    }
}
