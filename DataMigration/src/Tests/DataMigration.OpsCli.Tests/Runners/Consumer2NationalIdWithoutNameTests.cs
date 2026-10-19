using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class Consumer2NationalIdWithoutNameTests
{
    private const string FakeNationalId = "0101019999";

    [Theory]
    [InlineData("id-no-name-object", "National id without name")]
    [InlineData("id-blank-name", "National id without name")]
    [InlineData("id-other-name", null)]
    [InlineData("no-id-no-name", null)]
    [InlineData("id-company", null)]
    [InlineData("same-name", "Same name")]
    public void Reason_covers_national_id_without_name(string scenario, string? expected)
    {
        var point = Point(Guid.NewGuid(), new(2026, 1, 1), scenario);
        Assert.Equal(expected, FixConsumer2DuplicateRunner.Consumer2Reason(point));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Report_and_preview_select_the_same_versions_and_never_show_the_id(bool firstPeriodOnly, int expected)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = Point(id, new(2026, 1, 1), "id-no-name-object");
        if (firstPeriodOnly)
            client.DeliveryHistories[id] = ("""{"deliveryStatuses":[{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"Na","stopReason":"Na"}]}""", null);
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }.RunAsync("featuretest", [id.ToString()], null, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.Equal("Consumer2Reason", rows[0][^1]);
        var yes = rows.Skip(1).Where(row => row[13] == "Yes").ToArray();
        Assert.Equal(expected, yes.Length);
        Assert.All(yes, row => Assert.Equal("National id without name", row[^1]));
        Assert.All(rows.Skip(1).Where(row => row[13] != "Yes"), row => Assert.Empty(row[^1]));

        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], DryRun = true }));
        var previews = logs.Where(line => line.Contains("would blank")).ToArray();
        Assert.Equal(expected, previews.Length);
        Assert.All(previews, line => Assert.Contains("National id without name", line));
        Assert.DoesNotContain(FakeNationalId, csv.ToString());
        Assert.DoesNotContain(FakeNationalId, progress.ToString());
        Assert.DoesNotContain(FakeNationalId, string.Join("\n", logs));
    }

    [Fact]
    public async Task Hot_run_blanks_a_national_id_without_name()
    {
        var id = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1);
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(Point(id, start, "id-no-name-object")); // detect
        client.Reads.Enqueue(Point(id, start, "id-no-name-object")); // recheck
        client.Reads.Enqueue(Point(id, start, "no-id-no-name"));     // post-write verify
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()] }));
        Assert.Equal((id, start), Assert.Single(client.CleanedVersions));
        Assert.Contains("  Fixed versions: 1", logs);
        Assert.DoesNotContain(FakeNationalId, string.Join("\n", logs));
    }

    [Fact]
    public async Task Hot_run_reports_a_national_id_that_survives_the_write_as_a_failed_write()
    {
        var id = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1);
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(Point(id, start, "id-no-name-object"));
        client.Reads.Enqueue(Point(id, start, "id-no-name-object"));
        client.Reads.Enqueue(Point(id, start, "id-no-name-object")); // id still there after write
        var logs = new List<string>();
        Assert.Equal(1, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()] }));
        Assert.Contains("  Failed writes: 1", logs);
    }

    private static MeteringPointV4Dto Point(Guid id, DateTime start, string scenario) => new()
    {
        Id = id,
        Consumer = new()
        {
            Start = start,
            CustomerType = scenario == "id-company" ? "Company" : "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Fixture" } },
            SecondConsumerParty = scenario switch
            {
                "id-no-name-object" or "id-company" => new() { NationalIdentificationNumber = FakeNationalId },
                "id-blank-name" => new() { Name = new() { FullName = " ", FirstName = "", LastName = null }, NationalIdentificationNumber = FakeNationalId },
                "id-other-name" => new() { Name = new() { FullName = "Someone else" }, NationalIdentificationNumber = FakeNationalId },
                "same-name" => new() { Name = new() { FullName = "Fixture" } },
                _ => new() { Name = new() { FullName = "" } },
            }
        }
    };
}
