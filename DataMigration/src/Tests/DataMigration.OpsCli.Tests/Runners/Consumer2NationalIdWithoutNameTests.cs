using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

/// <summary>
/// Settl's metering-point read never carries national ids; they come from the separate national-id
/// read, which the stub answers from <see cref="StubFixConsumer2DuplicateMeteringPointClient.SecondNationalIds"/>.
/// </summary>
public class Consumer2NationalIdWithoutNameTests
{
    private const string FakeNationalId = "0101019999";

    [Theory]
    [InlineData("no-name-object", true, "National id without name")]
    [InlineData("blank-name", true, "National id without name")]
    [InlineData("other-name", true, null)]
    [InlineData("no-name-object", false, null)]
    [InlineData("company", true, null)]
    [InlineData("same-name", false, "Same name")]
    public void Reason_covers_national_id_without_name(string scenario, bool secondHasId, string? expected)
    {
        var point = Point(Guid.NewGuid(), new(2026, 1, 1), scenario);
        Assert.Equal(expected, FixConsumer2DuplicateRunner.Consumer2Reason(point, secondHasId));
    }

    [Theory]
    [InlineData("no-name-object", true)]
    [InlineData("blank-name", true)]
    [InlineData("other-name", false)]
    [InlineData("company", false)]
    [InlineData("same-name", false)]
    public void National_id_is_only_read_when_it_decides_selection(string scenario, bool expected) =>
        Assert.Equal(expected, FixConsumer2DuplicateRunner.NeedsNationalIdCheck(Point(Guid.NewGuid(), new(2026, 1, 1), scenario)));

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Report_and_preview_select_the_same_versions_and_never_show_the_id(bool firstPeriodOnly, int expected)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = Point(id, new(2026, 1, 1), "no-name-object");
        client.SecondNationalIds[id] = FakeNationalId;
        if (firstPeriodOnly)
            client.DeliveryHistories[id] = ("""{"deliveryStatuses":[{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"Na","stopReason":"Na"}]}""", null);
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }.RunAsync("featuretest", [id.ToString()], null, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.Equal("Consumer2Reason", rows[0][18]);
        var yes = rows.Skip(1).Where(row => row[13] == "Yes").ToArray();
        Assert.Equal(expected, yes.Length);
        Assert.All(yes, row => Assert.Equal("National id without name", row[18]));
        Assert.All(rows.Skip(1).Where(row => row[13] != "Yes"), row => Assert.Empty(row[18]));

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
    public async Task A_named_consumer_2_is_not_selected()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = Point(id, new(2026, 1, 1), "other-name");
        client.SecondNationalIds[id] = FakeNationalId;
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], DryRun = true }));
        Assert.DoesNotContain(logs, line => line.Contains("would blank"));
    }

    [Theory]
    [InlineData("other-name", "", "Yes")]
    [InlineData("other-name", " ", "Yes")]
    [InlineData("other-name", null, "")]
    [InlineData("other-name", FakeNationalId, "")]
    [InlineData("company", "", "")]
    public async Task Report_flags_a_named_consumer_2_with_an_empty_national_id(string scenario, string? secondId, string expected)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var point = Point(id, new(2026, 1, 1), scenario);
        if (scenario == "company") point.Consumer!.SecondConsumerParty = new() { Name = new() { FullName = "Someone else" } };
        client.ConsumerPoints[id] = point;
        client.SecondNationalIds[id] = secondId;
        using var csv = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [id.ToString()], null, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.Equal("Consumer2NameWithEmptyNationalId", rows[0][^1]);
        // A point is only listed when it is a duplicate, has an empty id or could not be read.
        if (expected == "") { Assert.Single(rows); return; }
        // The first delivery period has no consumer version, so its cell is empty.
        Assert.Equal(["", "Yes"], rows.Skip(1).Select(row => row[^1]).ToArray());
        Assert.All(rows.Skip(1), row => Assert.Equal("No", row[13]));

        // The fix never selects these versions.
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], DryRun = true }));
        Assert.DoesNotContain(logs, line => line.Contains("would blank"));
    }

    [Fact]
    public async Task A_failed_national_id_read_skips_the_point()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = Point(id, new(2026, 1, 1), "no-name-object");
        client.NationalIdReadFailures.Add(id);
        var logs = new List<string>();
        Assert.Equal(1, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()] }));
        Assert.Contains(logs, line => line.Contains("skipped:") && line.Contains("national id read failed"));
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Hot_run_blanks_a_national_id_without_name()
    {
        var id = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1);
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.SecondNationalIds[id] = FakeNationalId;
        client.Reads.Enqueue(Point(id, start, "no-name-object")); // detect
        client.Reads.Enqueue(Point(id, start, "no-name-object")); // recheck
        client.Reads.Enqueue(Point(id, start, "no-name-object")); // post-write verify
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()] }));
        Assert.Equal((id, start), Assert.Single(client.CleanedVersions));
        Assert.Contains("  Fixed versions: 1", logs);
        Assert.DoesNotContain(FakeNationalId, string.Join("\n", logs));
    }

    [Theory]
    [InlineData("no-name-object")]
    [InlineData("same-name")]
    public async Task A_national_id_that_survives_the_write_is_a_failed_write(string scenario)
    {
        var id = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1);
        var client = new StubFixConsumer2DuplicateMeteringPointClient { KeepNationalIdAfterWrite = true };
        client.SecondNationalIds[id] = FakeNationalId;
        client.Reads.Enqueue(Point(id, start, scenario));
        client.Reads.Enqueue(Point(id, start, scenario));
        client.Reads.Enqueue(Point(id, start, "no-name-object")); // name cleared, id still there
        var logs = new List<string>();
        Assert.Equal(1, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString()] }));
        Assert.Contains("  Failed writes: 1", logs);
        Assert.DoesNotContain(FakeNationalId, string.Join("\n", logs));
    }

    private static MeteringPointV4Dto Point(Guid id, DateTime start, string scenario) => new()
    {
        Id = id,
        Consumer = new()
        {
            Start = start,
            CustomerType = scenario == "company" ? "Company" : "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Fixture" } },
            SecondConsumerParty = scenario switch
            {
                "no-name-object" or "company" => new(),
                "blank-name" => new() { Name = new() { FullName = " ", FirstName = "", LastName = null } },
                "other-name" => new() { Name = new() { FullName = "Someone else" } },
                "same-name" => new() { Name = new() { FullName = "Fixture" } },
                _ => new() { Name = new() { FullName = "" } },
            }
        }
    };
}
