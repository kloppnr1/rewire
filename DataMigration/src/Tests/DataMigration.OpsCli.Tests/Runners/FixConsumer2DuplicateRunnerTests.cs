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
        Assert.Equal(2, client.GetByIdV4Calls); // detect + verify
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

        Assert.Equal(1, exitCode); // errors > 0
        Assert.Contains(loggedLines, l => l.Contains("still shows duplicate", StringComparison.Ordinal));
        Assert.DoesNotContain(loggedLines, l => l.Contains("fixed —", StringComparison.Ordinal));
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
