using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReportDeliveryPatternsRunnerTests
{
    [Fact]
    public void Dates_are_not_grouping_keys_and_consecutive_true_periods_are_preserved()
    {
        var original = History("2026-01-01", "2026-02-01");
        var otherDates = History("2025-03-01", "2025-04-01");
        var first = ReportDeliveryPatternsRunner.Interpret(original);
        var second = ReportDeliveryPatternsRunner.Interpret(otherDates);
        Assert.Equal(first.Key, second.Key);
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal("true (ChangeOfSupplier -> Na) -> true (MoveIn -> Na)", first.Pattern);
    }

    [Fact]
    public void Unknown_missing_null_and_false_values_are_distinct_without_enum_defaults()
    {
        var unknown = ReportDeliveryPatternsRunner.Interpret("""{"deliveryStatuses":[{"inDelivery":false,"startReason":"FutureReason","stopReason":42}]}""");
        Assert.Contains("FutureReason", unknown.Pattern);
        Assert.Contains("42", unknown.Pattern);
        var missing = ReportDeliveryPatternsRunner.Interpret("""{"deliveryStatuses":[{}]}""");
        var explicitNull = ReportDeliveryPatternsRunner.Interpret("""{"deliveryStatuses":[{"inDelivery":null}]}""");
        var falseFlag = ReportDeliveryPatternsRunner.Interpret("""{"deliveryStatuses":[{"inDelivery":false}]}""");
        Assert.NotEqual(missing.Key, explicitNull.Key);
        Assert.NotEqual(missing.Key, falseFlag.Key);
        Assert.NotEqual(explicitNull.Key, falseFlag.Key);
        Assert.Contains("(missing)", missing.Pattern);
    }

    [Fact]
    public async Task All_delivery_states_are_counted_with_three_examples_and_exact_source_responses()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var original = History("2026-01-01", "2026-02-01");
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "MP-" + i, InDelivery = i == 0 ? null : i % 2 == 0 });
            client.DeliveryHistories.Add(id, (original, null));
        }
        using var patterns = new StringWriter();
        using var examples = new StringWriter();
        using var sources = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportDeliveryPatternsRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", false, patterns, examples, sources));
        var summaryRows = patterns.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, summaryRows.Length);
        Assert.Contains("\"5\"", summaryRows[1]);
        Assert.Equal(7, examples.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        var sourceRows = sources.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, sourceRows.Length);
        foreach (var line in sourceRows)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(original, document.RootElement.GetProperty("ResponseJson").GetString());
        }
        Assert.Equal(0, client.GetByIdV4Calls);
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public void Empty_missing_and_failed_histories_have_separate_groups()
    {
        var empty = ReportDeliveryPatternsRunner.Interpret("""{"deliveryStatuses":[]}""");
        var missing = ReportDeliveryPatternsRunner.Interpret("{}");
        var failed = ReportDeliveryPatternsRunner.Interpret(null, "HTTP 500");
        Assert.NotEqual(empty.Key, missing.Key);
        Assert.NotEqual(missing.Key, failed.Key);
        Assert.True(failed.Failed);
    }

    private static string History(string first, string second) =>
        "{\"deliveryStatuses\":[{\"inDelivery\":true,\"period\":{\"start\":\"" + first + "T00:00:00Z\",\"end\":\"" + second + "T00:00:00Z\"},\"startReason\":\"ChangeOfSupplier\",\"stopReason\":\"Na\"},{\"inDelivery\":true,\"period\":{\"start\":\"" + second + "T00:00:00Z\",\"end\":\"9999-12-31T23:59:59Z\"},\"startReason\":\"MoveIn\",\"stopReason\":\"Na\"}]}";
}
