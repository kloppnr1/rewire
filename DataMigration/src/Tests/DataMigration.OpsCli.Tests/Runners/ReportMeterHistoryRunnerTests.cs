using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReportMeterHistoryRunnerTests
{
    [Fact]
    public void Empty_input_returns_no_periods()
    {
        var result = ReportMeterHistoryRunner.BuildResolutionPeriods([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Single_row_yields_one_open_period()
    {
        var rows = new[] { (new DateTime(2022, 1, 1), 2) };

        var result = ReportMeterHistoryRunner.BuildResolutionPeriods(rows);

        var p = Assert.Single(result);
        Assert.Equal(2, p.Seriesresolution);
        Assert.Equal(new DateTime(2022, 1, 1), p.Start);
        Assert.Null(p.End);
    }

    [Fact]
    public void Two_distinct_resolutions_split_at_transition()
    {
        var rows = new[]
        {
            (new DateTime(2022, 1, 1), 2),
            (new DateTime(2024, 1, 1), 1),
        };

        var result = ReportMeterHistoryRunner.BuildResolutionPeriods(rows);

        Assert.Collection(result,
            p =>
            {
                Assert.Equal(2, p.Seriesresolution);
                Assert.Equal(new DateTime(2022, 1, 1), p.Start);
                Assert.Equal(new DateTime(2024, 1, 1), p.End);
            },
            p =>
            {
                Assert.Equal(1, p.Seriesresolution);
                Assert.Equal(new DateTime(2024, 1, 1), p.Start);
                Assert.Null(p.End);
            });
    }

    [Fact]
    public void Three_periods_chain_end_to_next_start()
    {
        var rows = new[]
        {
            (new DateTime(2020, 1, 1), 2),
            (new DateTime(2022, 6, 1), 1),
            (new DateTime(2024, 1, 1), 2),
        };

        var result = ReportMeterHistoryRunner.BuildResolutionPeriods(rows);

        Assert.Equal(3, result.Count);
        Assert.Equal((2, new DateTime(2020, 1, 1), (DateTime?)new DateTime(2022, 6, 1)), (result[0].Seriesresolution, result[0].Start, result[0].End));
        Assert.Equal((1, new DateTime(2022, 6, 1), (DateTime?)new DateTime(2024, 1, 1)), (result[1].Seriesresolution, result[1].Start, result[1].End));
        Assert.Equal((2, new DateTime(2024, 1, 1), (DateTime?)null), (result[2].Seriesresolution, result[2].Start, result[2].End));
    }

    [Fact]
    public void Consecutive_same_resolution_rows_collapse_into_one_period()
    {
        var rows = new[]
        {
            (new DateTime(2022, 1, 1), 2),
            (new DateTime(2023, 1, 1), 2),
            (new DateTime(2024, 1, 1), 1),
        };

        var result = ReportMeterHistoryRunner.BuildResolutionPeriods(rows);

        Assert.Collection(result,
            p =>
            {
                Assert.Equal(2, p.Seriesresolution);
                Assert.Equal(new DateTime(2022, 1, 1), p.Start);
                Assert.Equal(new DateTime(2024, 1, 1), p.End);
            },
            p =>
            {
                Assert.Equal(1, p.Seriesresolution);
                Assert.Equal(new DateTime(2024, 1, 1), p.Start);
                Assert.Null(p.End);
            });
    }

    [Fact]
    public void Unordered_input_is_sorted_before_collapse()
    {
        var rows = new[]
        {
            (new DateTime(2024, 1, 1), 1),
            (new DateTime(2022, 1, 1), 2),
        };

        var result = ReportMeterHistoryRunner.BuildResolutionPeriods(rows);

        Assert.Collection(result,
            p => Assert.Equal(new DateTime(2022, 1, 1), p.Start),
            p => Assert.Equal(new DateTime(2024, 1, 1), p.Start));
    }
}
