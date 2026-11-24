using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class ConsumerVersionReportTests
{
    private static readonly DateTime January = new(2026, 1, 1);
    private static readonly DateTime June = new(2026, 6, 1);
    private static readonly DateTime December = new(2026, 12, 1);

    [Fact]
    public async Task Historical_and_future_duplicates_include_all_versions_and_every_delivery_period_once()
    {
        var id = Guid.NewGuid();
        var client = Fixture(id);
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", [], 1, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, client.GetByIdV4Calls);
        Assert.Equal(1, client.ConsumerRequests.Count(r => r.Date is null));
        Assert.Contains("2026-01-01T01:00:00.0000000+01:00", rows[1]);
        Assert.Contains("2026-12-01T01:00:00.0000000+01:00", rows[2]);
        Assert.Equal("1", Cells(rows[1])[8]);
        Assert.Equal("3", Cells(rows[2])[8]);
        Assert.Equal("false", Cells(rows[1])[17]);
        Assert.Equal("true", Cells(rows[2])[17]);
        // Every named consumer 2 also gets a national-id read, which counts as a read.
        Assert.Contains($"duplicates: 1; read errors: 0; versions read: {3 + client.NationalIdReads}; duplicate versions: 2", progress.ToString());
        Assert.Equal(0, client.BlankCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_wrong_dated_response_is_unknown_and_keeps_successful_findings(bool wrongVersion)
    {
        var id = Guid.NewGuid();
        var client = Fixture(id);
        client.DatedConsumerPoints[(id, December)] = wrongVersion ? Point(id, June, December, false) : null;
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", [], null, csv));
        Assert.Contains("\"Unknown\"", csv.ToString());
        Assert.Contains("Consumer correlation incomplete", csv.ToString());
        Assert.Contains("\"Yes\"", csv.ToString());
        Assert.Contains("Report incomplete", progress.ToString());
    }

    [Fact]
    public async Task Missing_history_retains_current_duplicate_but_marks_report_incomplete()
    {
        var id = Guid.NewGuid();
        var client = Fixture(id);
        client.ConsumerPoints[id] = Point(id, June, December, true);
        client.ConsumerHistories[id] = (null, "Failed fixture");
        using var csv = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], null, csv));
        Assert.Contains("Consumer history missing or failed", csv.ToString());
        Assert.Contains("\"Yes\"", csv.ToString());
    }

    [Fact]
    public async Task Limit_finishes_all_versions_of_affected_mp_without_reading_next_mp()
    {
        var id = Guid.NewGuid();
        var client = Fixture(id);
        client.MarketPoints.Add(new() { Id = Guid.NewGuid() });
        using var csv = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], 1, csv));
        Assert.Equal(3, client.ConsumerRequests.Count);
        Assert.All(client.ConsumerRequests, r => Assert.Equal(id, r.Id));
    }

    [Fact]
    public void Touching_intervals_do_not_overlap_and_open_ends_remain_open()
    {
        var version = new ConsumerVersion(Point(Guid.NewGuid(), January, June, true), null, "As returned");
        var history = ConsumerDeliveryEvidence.Parse("""
            {"deliveryStatuses":[{"inDelivery":true,"period":{"start":"2026-06-01T00:00:00Z","end":null}}]}
            """, null);
        Assert.Equal("No overlapping delivery period", Assert.Single(history.For(version)).Status);
        var open = version with { Point = Point(Guid.NewGuid(), June, null, true) };
        Assert.Equal("(open)", Assert.Single(history.For(open)).OverlapEnd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"deliveryStatuses\":[{\"inDelivery\":false,\"period\":{\"start\":\"invalid\",\"end\":null}}]}")]
    [InlineData("{\"deliveryStatuses\":[{\"inDelivery\":\"future\",\"period\":{\"start\":\"2026-01-01\",\"end\":null}}]}")]
    public void Missing_or_invalid_delivery_data_cannot_be_reported_as_no_overlap(string? json)
    {
        var history = ConsumerDeliveryEvidence.Parse(json, null);
        var version = new ConsumerVersion(Point(Guid.NewGuid(), January, June, true), null, "As returned");
        Assert.NotEmpty(history.Errors);
        Assert.DoesNotContain(history.For(version), r => r.Status == "No overlapping delivery period");
    }

    [Fact]
    public async Task Overlapping_consumer_boundaries_are_incomplete_and_not_correlated()
    {
        var id = Guid.NewGuid();
        var client = Fixture(id);
        client.DatedConsumerPoints[(id, January)] = Point(id, January, December, true);
        var evidence = await ConsumerVersionEvidence.ReadAsync(client, id, () => { }, default);
        Assert.Contains(evidence.Errors, e => e.Contains("overlapping"));
        Assert.NotEqual("As returned", evidence.Versions[0].Status);
    }

    [Fact]
    public void Conflicting_delivery_intervals_keep_period_evidence_but_mark_it_incomplete()
    {
        var history = ConsumerDeliveryEvidence.Parse("""
            {"deliveryStatuses":[
              {"inDelivery":false,"period":{"start":"2026-01-01T00:00:00Z","end":"2026-06-01T00:00:00Z"}},
              {"inDelivery":true,"period":{"start":"2026-03-01T00:00:00Z","end":null}}]}
            """, null);
        Assert.Contains(history.Errors, e => e.Contains("overlap"));
        var version = new ConsumerVersion(Point(Guid.NewGuid(), January, June, true), null, "As returned");
        Assert.Equal(3, history.For(version).Count());
    }

    [Fact]
    public async Task Missing_consumer_start_is_exported_even_without_duplicate_and_does_not_count_as_match()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "fixture" });
        client.ConsumerPoints[id] = new() { Id = id };
        client.ConsumerHistories[id] = (new() { ConsumerDates = [] }, null);
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", [], null, csv));
        Assert.Contains(id.ToString(), csv.ToString());
        Assert.Contains("Consumer start missing or invalid.", csv.ToString());
        Assert.Contains("duplicates: 0", progress.ToString());
        Assert.Contains("duplicate versions: 0", progress.ToString());
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Entirely_unreadable_mp_is_exported_with_unknown_duplicate_result()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerHistories[id] = (null, "Fixture failure");
        using var csv = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [id.ToString()], null, csv));
        Assert.Contains(id.ToString(), csv.ToString());
        Assert.Contains("Consumer correlation incomplete", csv.ToString());
        Assert.Contains("Unknown", csv.ToString());
    }

    [Fact]
    public async Task Independent_rows_keep_delivery_periods_outside_consumer_dates_and_blank_valid_warnings()
    {
        var id = Guid.NewGuid();
        var client = Fixture(id);
        client.ConsumerPoints[id] = Point(id, June, December, true);
        client.ConsumerHistories[id] = (new() { ConsumerDates = [June] }, null);
        client.DeliveryHistories[id] = ("""
            {"deliveryStatuses":[
              {"inDelivery":false,"period":{"start":"2025-12-01T01:00:00+01:00","end":"2026-01-01T01:00:00+01:00"},"startReason":"Na","stopReason":"Na"},
              {"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":"2026-12-01T00:00:00Z"},"startReason":"ChangeOfSupplier","stopReason":"Na"},
              {"inDelivery":false,"period":{"start":"2026-12-01T00:00:00Z","end":null},"startReason":"FutureReason","stopReason":42}]}
            """, null);
        using var csv = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], null, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.All(rows, row => Assert.Equal(23, row.Length));
        Assert.Equal(new[] { "MeteringPointIdentification", "SettlId", "DeliveryPatternMatch",
            "DeliveryStartDk", "DeliveryEndDk", "InDelivery", "DeliveryStartReason", "DeliveryStopReason",
            "ConsumerVersionCount", "Consumer1Name", "Consumer2Name", "ConsumerStartDk", "ConsumerEndDk",
            "DuplicateConsumer2", "AnyDuplicateConsumer2", "ConsumerWarning", "DeliveryWarning", "MarketUpdate", "Consumer2Reason", "Consumer2NameWithEmptyNationalId", "CompanyWithConsumer2", "Consumer2FictitiousCpr", "CprInvalidFormat" }, rows[0]);
        Assert.Equal(4, rows.Length);
        Assert.Equal("0", rows[1][8]);
        Assert.Empty(rows[1][17]);
        Assert.Equal("1", rows[2][8]);
        Assert.Equal("Yes", rows[2][13]);
        Assert.Equal("Yes", rows[2][14]);
        Assert.Empty(rows[2][15]);
        Assert.Equal("0", rows[3][8]);
        Assert.Equal("2025-12-01T01:00:00.0000000+01:00", rows[1][3]);
        Assert.Equal("2026-01-01T01:00:00.0000000+01:00", rows[1][4]);
        Assert.Equal("FutureReason", rows[3][6]);
        Assert.Equal("42", rows[3][7]);
        Assert.Equal("(open)", rows[3][4]);
    }

    private static string[] Cells(string row) => row.Split(';').Select(cell => cell.Trim('"')).ToArray();

    private static StubFixConsumer2DuplicateMeteringPointClient Fixture(Guid id)
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "fixture", InDelivery = false });
        client.ConsumerPoints[id] = Point(id, June, December, false);
        client.ConsumerHistories[id] = (new() { ConsumerDates = [December, January, June, January] }, null);
        client.DatedConsumerPoints[(id, January)] = Point(id, January, June, true);
        client.DatedConsumerPoints[(id, December)] = Point(id, December, null, true);
        client.DeliveryHistories[id] = ("""
            {"deliveryStatuses":[
              {"inDelivery":false,"period":{"start":"2026-01-01T00:00:00Z","end":"2026-03-01T00:00:00Z"},"startReason":"Na","stopReason":"Na"},
              {"inDelivery":true,"period":{"start":"2026-03-01T00:00:00Z","end":null},"startReason":"ChangeOfSupplier","stopReason":"Na"}]}
            """, null);
        return client;
    }

    private static MeteringPointV4Dto Point(Guid id, DateTime start, DateTime? end, bool duplicate) => new()
    {
        Id = id, Identification = new() { MeteringPointIdentification = "fixture" },
        Consumer = new()
        {
            Start = start, End = end, CustomerType = "Person", MarketUpdate = start == December,
            FirstConsumerParty = new() { Name = new() { FullName = "Synthetic" } },
            SecondConsumerParty = new() { Name = new() { FullName = duplicate ? "Synthetic" : "Different" } }
        }
    };
}
