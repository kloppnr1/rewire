using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using FluentAssertions;
using Xunit;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpTests;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpSelectionTests;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class PaymentMatchMpReportTests
{
    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Competing_rows_omit_blank_export_agreement_numbers_and_preserve_populated_numbers(string mode)
    {
        var blank = Record(Mp1, "E1"); blank.PaymentAgreementNumber = " ";
        var populated = Record(Mp1, "E2"); populated.PaymentAgreementNumber = " CSV2 ";
        var result = await Run(Reader(false, Source(pbs: 1, mobile: "own")), Mp1, [blank, populated], mode);
        result.ReportRows[0][6]!.ToString().Should().NotContain("Betalingsaftale i Settl:");
        result.ReportRows[1][6]!.ToString().Should().Contain("Betalingsaftale i Settl: CSV2.");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Rejected_joint_candidate_does_not_claim_a_successful_match(string mode)
    {
        var export = Record(null, "E1", "MAIN"); export.CustomerName = "Other";
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "main")), Mp1, [export], mode);
        result.ReportRows.Single()[5].Should().Be("Ikke matchet");
        result.ReportRows.Single()[6]!.ToString().Should().NotContain("Matchet via fællesfaktura.").And.Contain("Fundet via fællesfaktura.");
    }

    [Theory]
    [InlineData("pbs", true)]
    [InlineData("mb", true)]
    [InlineData("pbs", false)]
    [InlineData("mb", false)]
    public async Task Conflicts_show_actual_csv_pairs_without_promoting_a_safe_match(string mode, bool eligible)
    {
        var first = Record(Mp1, "E1"); first.CustomerName = "First name"; first.PaymentAgreementNumber = "CSV1";
        var second = Record(Mp1, "E2"); second.CustomerName = "Second name"; second.PaymentMethod = "MobilePay"; second.PaymentAgreementNumber = "CSV2";
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: eligible ? 1 : 0, mobile: eligible ? "own" : null));
        var result = await Run(reader, Mp1, [first, second, Record(Mp2, "UNRELATED")], mode);
        result.Coverage.Single().Outcome.Should().Be(eligible ? "Unmatched" : "NoAgreement");
        result.Coverage.Single().SettlCustomerName.Should().BeNull();
        result.Agreements.Should().NotContain(a => a.SettlCustomerNumber != null);
        result.ReportRows.Should().HaveCount(2).And.OnlyContain(row => Equals(row[5], eligible ? "Ikke matchet" : "Ingen relevant aftale") && Equals(row[8], Mp1));
        result.ReportRows.Select(row => (row[9], row[10], row[mode == "pbs" ? 17 : 16])).Should()
            .Equal(("E1", "PBS", "First name"), ("E2", "MobilePay", "Second name"));
        result.ReportRows[0][6]!.ToString().Should().Contain("CSV1");
        result.ReportRows[1][6]!.ToString().Should().Contain("CSV2");
        result.ReportRows.Should().OnlyContain(row => Equals(row[14], eligible ? mode == "pbs" ? "123" : "own" : null));
        result.ReportCsv().Should().NotContain("UNRELATED");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Joint_conflicts_keep_each_candidate_key_and_main_provenance(string mode)
    {
        var result = await Run(Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "main")), Mp1,
            [Record(null, "E1", "MAIN"), Record(null, "E2", "MAIN"), Record(null, "UNRELATED", "OTHER")], mode);
        result.ReportRows.Should().HaveCount(2).And.OnlyContain(row => Equals(row[1], "MAIN") && ((string)row[8]!).Contains("MAIN")
            && (Equals(row[5], "Match") || Equals(row[5], "Ikke matchet")));
        result.ReportRows.Select(row => row[9]).Should().Equal("E1", "E2");
    }

    [Fact]
    public async Task Shared_mobile_conflicts_are_repeated_for_exact_associated_mps()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("C2") with { MainContract = "MAIN" }, Source("MAIN", mobile: "shared"));
        reader.Owners = [Owner(Mp1), Owner(Mp2, contract: "C2")];
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [Record(Mp1, "E1"), Record(Mp1, "E2")]);
        result.Coverage.Should().OnlyContain(row => row.Outcome == "Matched");
        result.ReportRows.Should().HaveCount(4);
        foreach (var mp in new[] { Mp1, Mp2 })
        {
            result.ReportRows.Where(row => Equals(row[7], mp)).Select(row => row[9]).Should().Equal("E1", "E2");
            result.ReportRows.Where(row => Equals(row[7], mp)).Select(row => row[5]).Should().Equal("Ikke matchet", "Match");
        }
    }

    [Theory]
    [InlineData("Match", "Match", null)]
    [InlineData("Match (fælles)", "Match", "fællesfaktura")]
    [InlineData("E-nummer fundet", "Match", null)]
    [InlineData("Kun Xellent", "Ikke matchet", "Xellent")]
    [InlineData("Kun Settl", "Ikke matchet", "kun i Settl")]
    [InlineData("INTET E-nummer", "Ikke matchet", "Intet E-nummer")]
    [InlineData("Forskel", "Kræver undersøgelse", "Uoverensstemmelse")]
    [InlineData("Fejl", "Kræver undersøgelse", null)]
    public void Primary_status_is_shared_and_original_detail_remains_in_notes(string original, string expected, string? note)
    {
        foreach (var mode in new[] { "pbs", "mb" })
        {
            var coverage = new PaymentMpCoverage(Mp1, "100", "C1", null, null, "Unmatched", 1, "Coverage detail");
            var agreement = new PaymentMpAgreement(Mp1, "100", "C1", null, "A", "E1", "S1", Mp1, original, null, "C1", true, "PBS", "Original detail", false);
            var cells = PaymentMatchMpReport.Cells(coverage, agreement, mode, "2026-09-01", 1, null, 1);
            cells[5].Should().Be(expected);
            cells[6]!.ToString().Should().Contain("Original detail");
            cells[6]!.ToString().Should().NotContain("Fejl i betalingsgrundlaget.");
            if (note is not null) cells[6]!.ToString().Should().Contain(note);
            foreach (var (outcome, label) in new[] { ("NoAgreement", "Ingen relevant aftale"), ("Unresolved", "Kræver undersøgelse"), ("BusinessError", "Kræver undersøgelse") })
                PaymentMatchMpReport.Cells(coverage with { Outcome = outcome }, agreement, mode, "2026-09-01", 1, null, 1)[5].Should().Be(label);
        }
    }

    private static readonly string[] Original = ["Kundenummer", "Betalingsaftale fundet på kontrakt", "Kontrakttype", "Kontraktslutdato",
        "Målepunkter med egen betalingsaftale", "Status", "Bemærkning", "Målepunkt", "Nøgle der matchede i Settl", "Enummer Settl",
        "E-nummer hentet fra betalingsmetode", "Migreret kontrakt", "Betalingsaftale på migreret kontrakt", "COS", "Betalingsaftale-ID", "Betalingsaftale-status (Xellent)"];
    private static readonly string[] Names = ["Kundenavn på betalingsaftalen i Settl", "Kundenavn i Xellent", "Kundenummer i Xellent"];

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public void Columns_are_exactly_original_order_plus_three_customer_columns(string mode) =>
        PaymentMatchMpReport.Columns(mode).Should().Equal(Original.Concat(mode == "pbs" ? ["Kandidater på målepunkt"] : Array.Empty<string>()).Concat(Names));

    [Fact]
    public async Task Shared_mobilepay_is_repeated_per_associated_mp_with_its_actual_pair_and_source_count()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("C2") with { MainContract = "MAIN" }, Source("MAIN", mobile: "shared"));
        reader.Owners = [Owner(Mp1), Owner(Mp2, contract: "C2")];
        var export = Record(Mp2); export.CustomerName = "Export name";
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [export]);
        result.Agreements.Should().ContainSingle();
        result.ReportRows.Should().HaveCount(2);
        result.ReportRows.Select(r => r[7]).Should().Equal(Mp1, Mp2);
        result.ReportRows.Should().OnlyContain(r => Equals(r[4], 1) && Equals(r[14], "shared") && Equals(r[9], "E1")
            && Equals(r[15], "Aktiv") && Equals(r[16], "Export name"));
        result.ReportRows[0][12].Should().Be("Nej"); result.ReportRows[1][12].Should().Be("Nej");
    }

    [Fact]
    public async Task Separate_main_agreements_preserve_each_id_and_actual_unchanged_match_result()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN-A" }, Source("C2") with { MainContract = "MAIN-B" }, Source("MAIN-A", mobile: "A"), Source("MAIN-B", mobile: "B"));
        reader.Owners = [Owner(Mp1), Owner(Mp2, contract: "C2")];
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [Record(Mp1, "E1"), Record(Mp2, "E2")]);
        result.ReportRows.Select(r => (r[7], r[14], r[9])).Should().Equal((Mp1, "A", "E1"), (Mp2, "B", "E1"));
        // Both main contracts retain the existing first supplied same-account MP lookup.
        result.ReportRows.Should().OnlyContain(r => Equals(r[4], 2) && Equals(r[8], Mp1));
    }

    [Fact]
    public async Task Pbs_repeats_csv_pairs_and_keeps_source_count_status_and_real_owner_candidates()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("C2"), Source("MAIN", pbs: 1));
        reader.Snapshot = reader.Snapshot! with { PbsOwnAgreementCounts = new Dictionary<string, int> { ["100"] = 7 } };
        reader.Owners = [Owner(Mp1), Owner(Mp1, contract: "C2") with { ContractStart = new(2019, 1, 1) }];
        var first = Record(Mp1); first.CustomerName = "First";
        var second = Record(Mp1); second.CustomerName = "Second";
        var result = await Run(reader, Mp1, [first, second], "pbs");
        result.ReportRows.Should().HaveCount(2).And.OnlyContain(r => Equals(r[4], 7) && Equals(r[15], "Aktiv") && Equals(r[16], 2));
        result.ReportRows.Select(r => r[17]).Should().Equal("First", "Second");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task No_agreement_and_missing_owner_are_explicit_report_rows(string mode)
    {
        var result = await Run(Reader(false, Source()), $"{Mp1}\n{Mp2}", mode: mode);
        result.ReportRows.Should().HaveCount(2);
        result.ReportRows[0][5].Should().Be("Ingen relevant aftale");
        result.ReportRows[1][5].Should().Be("Kræver undersøgelse");
        result.ReportRows.Should().OnlyContain(r => r[14] == null && r[9] == null && !string.IsNullOrEmpty((string?)r[6]));
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Unlinked_agreements_are_ignored_and_business_errors_keep_selected_type_evidence(string mode)
    {
        var ambiguous = await Run(Reader(false, Source(), Source("W", "Vand", 1, "water"),
            Source("H", "Varme", 1, "heat") with { PbsNumber = 456 }), Mp1, mode: mode);
        ambiguous.ReportRows.Should().ContainSingle().Which[5].Should().Be("Ingen relevant aftale");
        var business = await Run(Reader(true, Source(), Source("W", "Vand", 1, "water")), Mp1, mode: mode);
        business.ReportRows.Should().HaveCount(1).And.OnlyContain(r => Equals(r[5], "Kræver undersøgelse") && r[9] == null && ((string)r[6]!).Contains("Denne aftale findes på vand/varme."));
        business.ReportRows.Single()[15].Should().Be("Aktiv");
    }

    [Fact]
    public async Task Source_provenance_is_truthful_and_unmatched_key_is_blank()
    {
        var reader = Reader(false, Source() with { MainContract = "W" }, Source("W", "Vand", 1) with { End = new(2026, 9, 10) });
        var result = await Run(reader, Mp1, mode: "pbs");
        var row = result.ReportRows.Single();
        row[1].Should().Be("W"); row[2].Should().Be("Hovedkontrakt"); row[3].Should().Be("2026-09-10");
        row[5].Should().Be("Ikke matchet"); row[8].Should().BeNull(); row[4].Should().Be(0);
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Joint_key_label_and_csv_escaping_follow_the_original_report(string mode)
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 1, mobile: "own"));
        var export = Record(null, joint: "MAIN"); export.CustomerName = "=Synthetic; \"name\"";
        var result = await Run(reader, Mp1, [export], mode);
        result.ReportRows.Single()[8].Should().Be("Fællesfaktura MAIN");
        result.ReportCsv().Should().Contain("'" + Mp1).And.Contain("\"'=Synthetic; \"\"name\"\"\"");
        result.ReportCsv().Split('\n')[0].TrimEnd('\r').Should().Be(string.Join(';', result.ReportColumns));
    }
}
