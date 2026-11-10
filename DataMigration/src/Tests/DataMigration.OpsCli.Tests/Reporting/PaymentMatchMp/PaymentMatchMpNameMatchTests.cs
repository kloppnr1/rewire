using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using FluentAssertions;
using Xunit;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpTests;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpSelectionTests;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class PaymentMatchMpNameMatchTests
{
    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Remarks_describe_only_rules_that_affect_the_result(string mode)
    {
        var reader = Reader(false, Source(pbs: 1, mobile: "own"));
        var other = Record(Mp1, "E200"); other.CustomerName = "Other";
        var single = await Run(reader, Mp1, [Record(Mp1, "E100"), other], mode);
        single.ReportCsv().Should().NotContain("Højeste E-nummer").And.NotContain("højere E-nummer");
        single.Coverage.Single().Explanation.Should().NotContain("E-nummer");
        var multiple = await Run(reader, Mp1, [Record(Mp1, "E100"), Record(Mp1, "E200")], mode);
        multiple.ReportRows[0][6]!.ToString().Should().Contain("højere E-nummer");
        multiple.ReportRows[1][6]!.ToString().Should().Contain("Højeste E-nummer");
        multiple.Coverage.Single().Explanation.Should().Contain("Højeste E-nummer");
        var missing = await Run(reader, Mp1, mode: mode);
        missing.ReportRows.Single()[6]!.ToString().Should().NotContain("Kundenavnene");
        missing.Coverage.Single().Explanation.Should().Contain("Ingen række med E-nummer");
        var none = await Run(Reader(false, Source()), Mp1, [Record(Mp1)], mode);
        none.ReportCsv().Should().NotContain("Kundenavnene").And.NotContain("Højeste E-nummer");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Eligible_agreement_evaluates_each_csv_name_and_aggregates_without_conflict_review(string mode)
    {
        var rows = new[] { "  SYNTHETIC CUSTOMER ", "Other", "Synthetic customer", "", " " }.Select((name, i) =>
        { var row = Record(Mp1, "E" + i); row.CustomerName = name; return row; }).ToList();
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "main")), Mp1, rows, mode);
        result.ReportRows.Select(r => r[5]).Should().Equal("Ikke matchet", "Ikke matchet", "Match", "Ikke matchet", "Ikke matchet");
        result.ReportRows.Select(r => r[9]).Should().Equal("E0", "E1", "E2", "E3", "E4");
        result.ReportRows.Should().OnlyContain(r => !r[6]!.ToString()!.Contains("Modstridende"));
        result.Coverage.Single().Outcome.Should().Be("Matched");
        System.Text.Json.JsonSerializer.SerializeToElement(result.Summary).GetProperty("needsReview").GetInt32().Should().Be(0);
        result.Agreements.Should().NotContain(r => r.SettlCustomerNumber != null);
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Equal_names_do_not_override_no_agreement_or_business_error(string mode)
    {
        var rows = new List<DataMigration.OpsCli.Reporting.SettlCsvRecord> { Record(Mp1, "E1"), Record(Mp1, "E2") };
        var none = await Run(Reader(false, Source()), Mp1, rows, mode);
        none.ReportRows.Should().OnlyContain(r => Equals(r[5], "Ingen relevant aftale"));
        var business = await Run(Reader(true, Source() with { MainContract = "MAIN" }, Source("MAIN", "Vand", 1, "main")), Mp1, rows, mode);
        business.ReportRows.Should().OnlyContain(r => Equals(r[5], "Kræver undersøgelse"));
        var diagnostic = business.Coverage.Single().ErrorExplanation!;
        business.ReportRows.Should().OnlyContain(r => !r[6]!.ToString()!.Contains(diagnostic) && r[6]!.ToString()!.Contains("aftale på elkontrakten."));
        business.ReportCsv().Should().NotContain("Modstridende E-numre");
        none.ReportCsv().Should().NotContain("Modstridende E-numre");
        business.Agreements.Should().Contain(a => a.Explanation.Contains("Modstridende E-numre"));
        business.Agreements.Should().NotContain(a => a.SettlCustomerNumber != null);
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(business.Summary);
        summary.GetProperty("businessErrors").GetInt32().Should().Be(1);
        summary.GetProperty("matched").GetInt32().Should().Be(0);
        var unknown = await Run(new StubMpReader(), Mp1, rows, mode);
        unknown.ReportRows.Should().OnlyContain(r => Equals(r[5], "Kræver undersøgelse") && r[6]!.ToString()!.Contains("Ingen Xellent-kunde"));
        unknown.ReportCsv().Should().NotContain("Modstridende E-numre");
    }

    [Theory]
    [InlineData("pbs", false)]
    [InlineData("mb", false)]
    [InlineData("pbs", true)]
    [InlineData("mb", true)]
    public async Task Repeated_highest_rows_are_retained_even_with_only_one_distinct_enumber(string mode, bool joint)
    {
        var rows = new[] { Record(joint ? null : Mp1, "E456", joint ? "MAIN" : null), Record(joint ? null : Mp1, "E456", joint ? "MAIN" : null) }.ToList();
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "main")), Mp1, rows, mode);
        result.ReportRows.Should().HaveCount(2).And.OnlyContain(r => Equals(r[5], "Kræver undersøgelse") && r[6]!.ToString()!.Contains("Fejl:"));
        result.Coverage.Single().ErrorCode.Should().Be("DuplicateHighestENumber");
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(result.Summary);
        summary.GetProperty("matched").GetInt32().Should().Be(0);
        summary.GetProperty("unresolved").GetInt32().Should().Be(1);
        summary.GetProperty("businessErrors").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Numeric_order_ties_and_invalid_numbers_never_guess_a_winner(string mode)
    {
        var reader = Reader(false, Source(pbs: 1, mobile: "own"));
        var ordered = await Run(reader, Mp1, [Record(Mp1, "E99"), Record(Mp1, "E100")], mode);
        ordered.ReportRows.Select(r => r[5]).Should().Equal("Ikke matchet", "Match");
        var huge = await Run(reader, Mp1, [Record(Mp1, "E999999999999999999999999999999"), Record(Mp1, "E100")], mode);
        huge.ReportRows.Select(r => r[5]).Should().Equal("Match", "Ikke matchet");
        var tied = await Run(reader, Mp1, [Record(Mp1, "E99"), Record(Mp1, "E100"), Record(Mp1, "E0100")], mode);
        tied.ReportRows.Select(r => r[5]).Should().Equal("Ikke matchet", "Kræver undersøgelse", "Kræver undersøgelse");
        tied.Coverage.Single().ErrorCode.Should().Be("DuplicateHighestENumber");
        var invalid = await Run(reader, Mp1, [Record(Mp1, "E100"), Record(Mp1, "bad")], mode);
        invalid.ReportRows.Should().OnlyContain(r => Equals(r[5], "Kræver undersøgelse"));
        invalid.Coverage.Single().ErrorCode.Should().Be("InvalidENumber");
    }

    [Fact]
    public async Task Own_pbs_zero_number_remains_eligible_and_unknown_owner_never_matches()
    {
        var own = await Run(Reader(false, Source(pbs: 1) with { PbsNumber = 0 }), Mp1, [Record(Mp1)], "pbs");
        own.ReportRows.Single()[5].Should().Be("Match");
        own.ReportRows.Single()[14].Should().BeNull();
        var unknown = await Run(new StubMpReader(), Mp1, [Record(Mp1), Record(Mp1, "E2")], "pbs");
        unknown.ReportRows.Should().OnlyContain(row => Equals(row[5], "Kræver undersøgelse"));
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(unknown.Summary);
        summary.GetProperty("unresolved").GetInt32().Should().Be(1);
        summary.GetProperty("needsReview").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Name_comparison_requires_two_nonblank_names_and_does_not_fuzzy_match()
    {
        PaymentMatchMpReport.NamesMatch(" Name ", "NAME").Should().BeTrue();
        PaymentMatchMpReport.NamesMatch(" ", "").Should().BeFalse();
        PaymentMatchMpReport.NamesMatch(null, "Name").Should().BeFalse();
        PaymentMatchMpReport.NamesMatch("Name One", "Name  One").Should().BeFalse();
    }
}
