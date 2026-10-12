using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReportConsumer2DuplicateRunnerTests
{
    [Theory]
    [InlineData(false, "Possible delivery change")]
    [InlineData(true, "No later-delivery signal in these fields")]
    [InlineData(null, "Unable to determine")]
    public async Task Report_includes_inactive_candidates_and_uncertainty_without_writes(bool? inDelivery, string signal)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "571313100000000002", InDelivery = inDelivery });
        client.Reads.Enqueue(Duplicate(id));
        using var output = new StringWriter();
        var result = await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [], null, output);
        Assert.Equal(0, result);
        Assert.Equal(0, client.BlankCalls);
        Assert.Contains(signal, output.ToString());
        Assert.Contains("do not establish whether changes happened after migration", output.ToString());
        Assert.Contains("featuretest", output.ToString());
        Assert.Contains("Test; \"Name\"", output.ToString().Replace("\"\"", "\""));
        Assert.DoesNotContain("1234567890", output.ToString());
    }

    [Fact]
    public void Completed_period_is_a_signal_even_when_currently_in_delivery()
    {
        var assessment = ReportConsumer2DuplicateRunner.Assess(new() { InDelivery = true, MostRecentCompletedDeliveryPeriod = new() });
        Assert.Equal("Possible delivery change", assessment.Signal);
        Assert.Contains("effective dates", assessment.Reason);
    }

    [Fact]
    public async Task Report_uses_exact_cleanup_detection_and_keeps_business_points_out()
    {
        var id = Guid.NewGuid();
        var mp = Duplicate(id);
        mp.Consumer!.CustomerType = "Company";
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(mp);
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [id.ToString()], null, output));
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Failed_consumer_read_marks_report_incomplete()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        using var output = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [Guid.NewGuid().ToString()], null, output));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scoped_identification_and_guid_read_market_evidence(bool useGuid)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "571313100000000002", InDelivery = false });
        client.Reads.Enqueue(Duplicate(id));
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("production-hev",
            [useGuid ? id.ToString() : "571313100000000002"], null, output));
        Assert.Contains("Possible delivery change", output.ToString());
        Assert.Contains("production-hev", output.ToString());
        Assert.Equal(0, client.BlankCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_mismatched_market_evidence_keeps_duplicate_with_warning(bool mismatched)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        if (mismatched) client.MarketPoints.Add(new() { Id = Guid.NewGuid(), MeteringPointIdentification = "571313100000000002", InDelivery = false });
        client.Reads.Enqueue(Duplicate(id));
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [id.ToString()], null, output));
        Assert.Contains("Unable to determine", output.ToString());
        Assert.DoesNotContain("Possible delivery change", output.ToString());
    }

    [Fact]
    public async Task Matching_limit_stops_before_reading_another_candidate()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) client.MarketPoints.Add(new() { Id = id });
        client.Reads.Enqueue(Duplicate(ids[0]));
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [], 1, output));
        Assert.Equal(1, client.GetByIdV4Calls);
        Assert.Equal(2, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Differing_or_missing_names_are_excluded(bool missing)
    {
        var id = Guid.NewGuid();
        var mp = Duplicate(id);
        mp.Consumer!.SecondConsumerParty!.Name = missing ? null : new() { FullName = "Other" };
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(mp);
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [id.ToString()], null, output));
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    private static MeteringPointV4Dto Duplicate(Guid id) => new()
    {
        Id = id, Identification = new() { MeteringPointIdentification = "571313100000000002" },
        Consumer = new()
        {
            CustomerType = "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Test; \"Name\"" } },
            SecondConsumerParty = new() { Name = new() { FullName = " Test; \"Name\" " } }
        }
    };
}
