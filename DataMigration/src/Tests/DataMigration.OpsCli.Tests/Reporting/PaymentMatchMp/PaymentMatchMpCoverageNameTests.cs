using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using FluentAssertions;
using Xunit;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpTests;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpSelectionTests;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class PaymentMatchMpCoverageNameTests
{
    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Coverage_uses_associated_export_name_and_csv_escapes_it(string mode)
    {
        var csv = Record(Mp1); csv.CustomerName = "=Synthetic; \"export\""; csv.PayerName = "Wrong payer";
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "own")), Mp1, [csv], mode);
        result.Coverage.Single().SettlCustomerName.Should().Be(csv.CustomerName);
        result.CoverageCsv().Should().Contain("Kundenavn på betalingsaftalen i Settl")
            .And.Contain("\"'=Synthetic; \"\"export\"\"\"").And.NotContain("Wrong payer");
        result.Coverage.Single().Outcome.Should().Be("Unmatched");
    }

    [Fact]
    public async Task Shared_mobilepay_name_reaches_every_associated_mp_without_duplicating_agreement()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("C2") with { MainContract = "MAIN" }, Source("MAIN", mobile: "shared"));
        reader.Owners = [Owner(Mp1), Owner(Mp2, contract: "C2")];
        var csv = Record(Mp2); csv.CustomerName = "Shared export name";
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [csv]);
        result.Agreements.Should().ContainSingle().Which.MeteringPointId.Should().Be(Mp2);
        result.Coverage.Should().HaveCount(2).And.OnlyContain(r => r.SettlCustomerName == "Shared export name");
    }

    [Fact]
    public async Task Multiple_pbs_names_keep_first_occurrence_and_remove_only_blank_or_ordinal_duplicates()
    {
        var records = new[] { "Second", "", "First", "Second", " ", "first" }.Select(name =>
        { var row = Record(Mp1); row.CustomerName = name; return row; }).ToList();
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1)), Mp1, records, "pbs");
        result.Coverage.Single().SettlCustomerName.Should().Be("Second; First; first");
        result.Coverage.Single().Agreements.Should().Be(6);
        result.CoverageCsv().Should().Contain("\"Second; First; first\"");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Csv_conflict_clears_coverage_names(string mode)
    {
        var first = Record(Mp1); first.CustomerName = "First";
        var second = Record(Mp1, "E2"); second.CustomerName = "Second";
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "own")), Mp1, [first, second], mode);
        result.Coverage.Single().SettlCustomerName.Should().BeNull();
        result.Coverage.Single().Outcome.Should().Be("Unmatched");
    }

    [Fact]
    public async Task Conflict_clearing_propagates_to_every_mp_sharing_mobilepay_detail()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("C2") with { MainContract = "MAIN" }, Source("MAIN", mobile: "shared"));
        reader.Owners = [Owner(Mp1), Owner(Mp2, contract: "C2")];
        var first = Record(Mp1); first.CustomerName = "First";
        var second = Record(Mp1, "E2"); second.CustomerName = "Second";
        var other = Record(Mp2); other.CustomerName = "Otherwise matchable";
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [first, second, other]);
        result.Coverage.Should().OnlyContain(r => r.SettlCustomerName == null);
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Missing_csv_names_and_absent_matches_stay_null(string mode)
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "own"));
        var csv = Record(Mp1); csv.CustomerName = " "; csv.PayerName = "Never substitute payer";
        (await Run(reader, Mp1, [csv], mode)).Coverage.Single().SettlCustomerName.Should().BeNull();
        (await Run(reader, Mp1, mode: mode)).Coverage.Single().SettlCustomerName.Should().BeNull();
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Unlinked_agreements_and_business_evidence_do_not_infer_names(string mode)
    {
        var csv = Record(Mp1); csv.CustomerName = "Must not infer from CSV";
        var ambiguous = await Run(Reader(false, Source(), Source("W", "Vand", 1, "water"),
            Source("H", "Varme", 1, "heat") with { PbsNumber = 456 }), Mp1, [csv], mode);
        ambiguous.Coverage.Single().Outcome.Should().Be("NoAgreement");
        ambiguous.Agreements.Should().NotContain(r => r.AgreementId != "");
        var business = await Run(Reader(true, Source(), Source("W", "Vand", 1, "water")), Mp1, mode: mode);
        business.Coverage.Single().SettlCustomerName.Should().BeNull();
    }
}
