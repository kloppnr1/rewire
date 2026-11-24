using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReportDriftRunnerTests
{
    private static Dictionary<string, HashSet<string>> Map(params (string rep, string[] payers)[] entries) =>
        entries.ToDictionary(
            e => e.rep,
            e => e.payers.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    [Fact]
    public void Matching_fanout_yields_no_rows_by_default()
    {
        var expected = Map(("100", ["100", "100-1"]));
        var actual = Map(("100", ["100", "100-1"]));

        var (rows, needsUpdate, noBasis) = ReportDriftRunner.BuildFanOutRows(expected, actual, showAll: false);

        Assert.Empty(rows);
        Assert.Equal(0, needsUpdate);
        Assert.Equal(0, noBasis);
    }

    [Fact]
    public void Matching_fanout_emits_ok_row_with_show_all()
    {
        var expected = Map(("100", ["100", "100-1"]));
        var actual = Map(("100", ["100", "100-1"]));

        var (rows, needsUpdate, _) = ReportDriftRunner.BuildFanOutRows(expected, actual, showAll: true);

        var row = Assert.Single(rows);
        Assert.Equal("Fan-out", row.EntityType);
        Assert.Equal("100", row.Identification);
        Assert.Equal("OK", row.Status);
        Assert.Equal("", row.Årsager);
        Assert.Equal(0, needsUpdate);
    }

    [Fact]
    public void Under_migrated_rep_flags_missing_payers()
    {
        var expected = Map(("100", ["100", "100-1", "100-2"]));
        var actual = Map(("100", ["100"]));

        var (rows, needsUpdate, _) = ReportDriftRunner.BuildFanOutRows(expected, actual, showAll: false);

        var row = Assert.Single(rows);
        Assert.Equal("NeedsUpdate", row.Status);
        Assert.Equal("MissingPayers", row.Årsager);
        Assert.Equal("expected=3;actual=1;missing=[100-1,100-2]", row.Detalje);
        Assert.Equal(1, needsUpdate);
    }

    [Fact]
    public void Extra_settl_payer_flags_extra_payers()
    {
        var expected = Map(("100", ["100"]));
        var actual = Map(("100", ["100", "100-1"]));

        var (rows, _, _) = ReportDriftRunner.BuildFanOutRows(expected, actual, showAll: false);

        var row = Assert.Single(rows);
        Assert.Equal("ExtraPayers", row.Årsager);
        Assert.Equal("expected=1;actual=2;extra=[100-1]", row.Detalje);
    }

    [Fact]
    public void Missing_and_extra_combine_in_reasons_and_detail()
    {
        var expected = Map(("100", ["100", "100-1"]));
        var actual = Map(("100", ["100", "100-2"]));

        var (rows, _, _) = ReportDriftRunner.BuildFanOutRows(expected, actual, showAll: false);

        var row = Assert.Single(rows);
        Assert.Equal("MissingPayers,ExtraPayers", row.Årsager);
        Assert.Equal("expected=2;actual=2;missing=[100-1];extra=[100-2]", row.Detalje);
    }

    [Fact]
    public void Rep_without_xellent_expectation_counts_as_no_basis_and_emits_no_row()
    {
        var expected = Map(("100", ["100"]));
        var actual = Map(("999", ["999"])); // 999 not in Xellent expectation

        var (rows, needsUpdate, noBasis) = ReportDriftRunner.BuildFanOutRows(expected, actual, showAll: true);

        Assert.Empty(rows);
        Assert.Equal(0, needsUpdate);
        Assert.Equal(1, noBasis);
    }
}
