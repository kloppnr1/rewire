using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

/// <summary>DataHub customer rules (BRS-015) flagged by the report, and the opt-in clearing of a fictitious CPR 2.</summary>
public class Consumer2DataHubRuleTests
{
    private const string RealCpr = "0101019999";
    private const string Fictitious = "1111110000";

    [Theory]
    // type, consumer 2 named, CPR 1, CPR 2 → company, fictitious, invalid
    [InlineData("Company", true, null, null, "Yes", "No", "No")]
    [InlineData("Company", false, null, null, "", "", "")]
    [InlineData("Person", true, RealCpr, Fictitious, "No", "Yes", "No")]
    [InlineData("Person", true, Fictitious, Fictitious, "", "", "")]
    [InlineData("Person", true, null, Fictitious, "", "", "")]
    [InlineData("Person", true, "12345", RealCpr, "No", "No", "Yes")]
    [InlineData("Person", true, RealCpr, "01010199AB", "No", "No", "Yes")]
    [InlineData("Person", true, RealCpr, RealCpr, "", "", "")]
    public async Task Report_flags_datahub_rules_without_exporting_cprs(string type, bool named, string? first, string? second,
        string company, string fictitious, string invalid)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = Point(id, type, named);
        client.FirstNationalIds[id] = first;
        client.SecondNationalIds[id] = second;
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", [id.ToString()], null, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.Equal(["CompanyWithConsumer2", "Consumer2FictitiousCpr", "CprInvalidFormat"], rows[0][20..]);
        // An empty expectation means the point breaks no rule and is not listed.
        if (company == "") { Assert.Single(rows); return; }
        var row = rows[^1];
        Assert.Equal([company, fictitious, invalid], row[20..]);
        Assert.Equal("No", row[13]);
        foreach (var value in new[] { first, second }.Where(v => v is { Length: > 5 }))
        {
            Assert.DoesNotContain(value!, csv.ToString());
            Assert.DoesNotContain(value!, progress.ToString());
        }
    }

    [Theory]
    [InlineData(true, true, false, 1)]
    [InlineData(false, true, false, 0)]
    [InlineData(true, true, true, 0)]
    public async Task Dry_run_clears_a_fictitious_cpr_2_only_when_asked(bool option, bool listed, bool firstPeriodOnly, int expected)
    {
        var id = Guid.NewGuid();
        var client = Fictitious2(id);
        if (firstPeriodOnly)
            client.DeliveryHistories[id] = ("""{"deliveryStatuses":[{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"Na","stopReason":"Na"}]}""", null);
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = listed ? [id.ToString()] : [], DryRun = true, ClearFictitiousCpr2 = option }));
        var lines = logs.Where(line => line.Contains("would clear second party NationalIdentificationNumber (Fictitious CPR)")).ToArray();
        Assert.Equal(expected, lines.Length);
        Assert.DoesNotContain(logs, line => line.Contains("would blank"));
        Assert.DoesNotContain(RealCpr, string.Join("\n", logs));
    }

    [Fact]
    public async Task The_option_needs_a_list_of_points()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new FixConsumer2DuplicateRunner(client, _ => { }) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { DryRun = true, ClearFictitiousCpr2 = true }));
    }

    [Fact]
    public async Task Hot_run_clears_only_the_cpr_and_counts_it_fixed()
    {
        var id = Guid.NewGuid();
        var client = Fictitious2(id);
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], ClearFictitiousCpr2 = true }));
        Assert.Equal((id, new DateTime(2026, 1, 1)), Assert.Single(client.ClearedCprs));
        Assert.Empty(client.CleanedVersions);
        Assert.Contains("  Fixed versions: 1", logs);
        Assert.Contains("  Failed writes: 0", logs);
    }

    [Fact]
    public async Task A_cpr_that_survives_the_write_is_a_failed_write()
    {
        var id = Guid.NewGuid();
        var client = Fictitious2(id);
        client.KeepNationalIdAfterWrite = true;
        var logs = new List<string>();
        Assert.Equal(1, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], ClearFictitiousCpr2 = true }));
        Assert.Contains("  Failed writes: 1", logs);
    }

    private static StubFixConsumer2DuplicateMeteringPointClient Fictitious2(Guid id)
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = Point(id, "Person", true);
        client.FirstNationalIds[id] = RealCpr;
        client.SecondNationalIds[id] = Fictitious;
        return client;
    }

    private static MeteringPointV4Dto Point(Guid id, string type, bool named) => new()
    {
        Id = id,
        Consumer = new()
        {
            Start = new DateTime(2026, 1, 1),
            CustomerType = type,
            FirstConsumerParty = new() { Name = new() { FullName = "Fixture" } },
            SecondConsumerParty = named ? new() { Name = new() { FullName = "Someone else" } } : new()
        }
    };
}
