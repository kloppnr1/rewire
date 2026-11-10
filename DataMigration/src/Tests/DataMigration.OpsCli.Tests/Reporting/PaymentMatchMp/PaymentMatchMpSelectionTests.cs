using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpTests;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class PaymentMatchMpSelectionTests
{
    private static readonly DateTime On = new(2026, 8, 31);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Code_three_is_cancelled_and_not_an_eligible_own_or_main_pbs_agreement(bool main)
    {
        var sources = main
            ? new[] { Source() with { MainContract = "MAIN" }, Source("MAIN", pbs: 3) }
            : new[] { Source(pbs: 3) };
        var result = await Run(Reader(false, sources), Mp1, [Record(Mp1)], "pbs");
        result.Coverage.Single().Outcome.Should().Be("NoAgreement");
        result.ReportRows.Should().OnlyContain(row => Equals(row[5], "Ingen relevant aftale")
            && Equals(row[15], "Aftale afmeldt af PI") && row[14] == null);
    }

    [Theory]
    [InlineData("pbs", true, "Matched")]
    [InlineData("pbs", false, "Unmatched")]
    [InlineData("mb", true, "BusinessError")]
    public async Task Pbs_water_heat_exception_preserves_matching_and_does_not_exempt_other_points_or_mobilepay(string mode, bool sameName, string expected)
    {
        var reader = Reader(true, Source(), Source("W", "Vand", 1, "water"));
        reader.Owners = [Owner(Mp1), Owner(Mp2)];
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PaymentMatch:PbsWaterHeatRuleExcludedMeteringPoints:0"] = Mp1 }).Build();
        var record = Record(Mp1);
        if (!sameName) record.CustomerName = "Different synthetic customer";
        var result = await new PaymentMatchMpEngine(reader, config).RunAsync(
            PaymentMatchMpList.Parse($"{Mp1}\n{Mp2}"), [record, Record(Mp2)], "production", mode, Cutover,
            false, "points.txt", "payments.csv", (_, _, _) => { }, CancellationToken.None);
        result.Coverage.Single(r => r.MeteringPointId == Mp1).Outcome.Should().Be(expected);
        result.Coverage.Single(r => r.MeteringPointId == Mp2).Outcome.Should().Be("BusinessError");
        if (mode == "pbs")
        {
            result.Agreements.Where(r => r.MeteringPointId == Mp1).Should().OnlyContain(r => !r.EvidenceOnly);
            result.Agreements.Single(r => r.MeteringPointId == Mp1).SourceContractNumber.Should().Be("W");
            result.ReportRows.Single(r => Equals(r[7], Mp1))[14].Should().Be("123");
            result.Coverage.Single(r => r.MeteringPointId == Mp1).Explanation.Should().Contain("PBS-aftale fra vand/varme tilladt for dette målepunkt.");
            result.ReportRows.Where(r => Equals(r[7], Mp1)).Should().OnlyContain(r => r[6]!.ToString()!.Contains("PBS-aftale fra vand/varme tilladt for dette målepunkt."));
        }
        else
            result.ReportRows.Should().OnlyContain(r => !r[6]!.ToString()!.Contains("PBS-aftale fra vand/varme tilladt"));
        result.ReportRows.Where(r => Equals(r[7], Mp2)).Should().OnlyContain(r => !r[6]!.ToString()!.Contains("PBS-aftale fra vand/varme tilladt"));
    }

    internal static PaymentMpSource Source(string contract = "C1", string commodity = "El-ekstern", int pbs = 0, string? mobile = null) =>
        new("100", contract, commodity, null, new(2020, 1, 1), new(1900, 1, 1), pbs == 0 ? 0 : 123,
            pbs, new(1900, 1, 1), mobile, new(1900, 1, 1));
    private static PaymentMpSelection Select(string mode, bool business, params PaymentMpSource[] sources) =>
        PaymentMpSelection.Select(Owner(Mp1), new("100", "Synthetic customer", business), sources, On, mode);
    internal static StubMpReader Reader(bool business, params PaymentMpSource[] sources) => new()
    {
        Owners = [Owner(Mp1)], Snapshot = new([new("100", "Synthetic customer", business)], sources)
    };

    [Theory]
    [InlineData("pbs", false)]
    [InlineData("mb", false)]
    [InlineData("pbs", true)]
    [InlineData("mb", true)]
    public async Task Only_linked_main_is_selected_even_when_own_and_other_contracts_have_agreements(string mode, bool business)
    {
        var own = Source(pbs: 1, mobile: "own") with { MainContract = "MAIN", PbsNumber = 999 };
        var main = Source("MAIN", "Vand", 1, "main");
        var result = await Run(Reader(business, own, main, Source("OTHER", "Varme", 2, "other")), Mp1, [Record(Mp1)], mode);
        result.Coverage.Single().Outcome.Should().Be("Matched");
        var row = result.Agreements.Single();
        row.SourceContractNumber.Should().Be("MAIN");
        row.AgreementId.Should().Be(mode == "pbs" ? "123" : "main");
        row.OwningContract.Should().Be("C1");
    }

    [Theory]
    [InlineData("pbs", false)]
    [InlineData("mb", false)]
    [InlineData("pbs", true)]
    [InlineData("mb", true)]
    public async Task No_main_uses_own_selected_type_for_private_and_business(string mode, bool business)
    {
        foreach (var link in new string?[] { null, "", " " })
        {
            var own = Source(pbs: 1, mobile: "own") with { MainContract = link };
            var result = await Run(Reader(business, own, Source("OTHER", "Vand", 1, "other")), Mp1, [Record(Mp1)], mode);
            result.Coverage.Single().Outcome.Should().Be("Matched");
            result.Agreements.Should().ContainSingle().Which.SourceContractNumber.Should().Be("C1");
            result.ReportRows.Single()[14].Should().Be(mode == "pbs" ? "123" : "own");
            result.ReportRows.Single()[12].Should().Be("Ja");
        }
    }

    [Theory]
    [InlineData("pbs", false)]
    [InlineData("mb", false)]
    [InlineData("pbs", true)]
    [InlineData("mb", true)]
    public async Task No_main_and_inactive_own_has_no_agreement(string mode, bool business)
    {
        var own = Source(pbs: 4, mobile: "cancelled") with { MobileCancel = On };
        var result = await Run(Reader(business, own), Mp1, [Record(Mp1)], mode);
        result.Coverage.Single().Outcome.Should().Be("NoAgreement");
        result.Agreements.Should().NotContain(r => r.AgreementId != "");
        result.ReportRows.Should().OnlyContain(r => r[14] == null && r[8] == null && Equals(r[5], "Ingen relevant aftale"));
    }

    [Fact]
    public void Own_pbs_keeps_existing_status_eligibility_even_with_zero_number()
    {
        var result = Select("pbs", false, Source(pbs: 1) with { PbsNumber = 0 });
        result.Agreements.Should().ContainSingle().Which.Contract.Should().Be("C1");
    }

    [Theory]
    [InlineData("pbs", "missing")]
    [InlineData("mb", "missing")]
    [InlineData("pbs", "inactive")]
    [InlineData("mb", "inactive")]
    [InlineData("pbs", "future")]
    [InlineData("mb", "future")]
    [InlineData("pbs", "expired")]
    [InlineData("mb", "expired")]
    [InlineData("pbs", "other-account")]
    [InlineData("mb", "other-account")]
    [InlineData("pbs", "self")]
    [InlineData("mb", "self")]
    public async Task Missing_or_ineligible_main_has_no_fallback(string mode, string condition)
    {
        var own = Source(pbs: 1, mobile: "own") with { MainContract = condition == "self" ? "C1" : "MAIN" };
        var main = Source(condition == "missing" ? "OTHER" : "MAIN", "Vand", 1, "main");
        main = condition switch
        {
            "inactive" => main with { PbsCode = 4, MobileCancel = On.AddDays(-2) },
            "future" => main with { Start = On.AddDays(1) }, "expired" => main with { End = On.AddDays(-1) },
            "other-account" => main with { Account = "200" }, _ => main
        };
        var result = await Run(Reader(false, own, main), Mp1, [Record(Mp1)], mode);
        result.Coverage.Single().Outcome.Should().Be("NoAgreement");
        result.Agreements.Should().NotContain(r => r.AgreementId != "");
        result.ReportRows.Should().OnlyContain(r => r[14] == null && r[8] == null && Equals(r[5], "Ingen relevant aftale"));
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public void Main_ending_on_day_before_cutover_is_valid(string mode)
    {
        var result = Select(mode, false, Source() with { MainContract = "MAIN" }, Source("MAIN", "Vand", 1, "main") with { End = On });
        result.Agreements.Should().ContainSingle().Which.Contract.Should().Be("MAIN");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Business_error_checks_only_selected_type_and_other_own_type_does_not_suppress_it(string mode)
    {
        var ownOther = mode == "pbs" ? Source(mobile: "own-mobile") : Source(pbs: 1);
        var result = await Run(Reader(true, ownOther, Source("W", "Vand/Varme", 1, "water-mobile")), Mp1, mode: mode);
        result.Coverage.Single().Outcome.Should().Be("BusinessError");
        result.Agreements.Should().ContainSingle().Which.PaymentMethod.Should().Be(mode == "pbs" ? "PBS" : "MobilePay");
        result.ReportRows.Single()[15].Should().Be("Aktiv");
        result.ReportRows.Single()[6]!.ToString().Should().NotContain(mode == "pbs" ? "MobilePay" : "PBS");
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Other_type_on_water_heat_never_triggers_business_error(string mode)
    {
        var waterOther = mode == "pbs" ? Source("W", "Vand", mobile: "water") : Source("W", "Vand", pbs: 1);
        var result = await Run(Reader(true, Source(), waterOther), Mp1, mode: mode);
        result.Coverage.Single().Outcome.Should().Be("NoAgreement");
        result.Agreements.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public void Own_selected_type_suppresses_error_and_only_valid_water_heat_is_evidence(string mode)
    {
        Select(mode, true, Source(pbs: 1, mobile: "own"), Source("W", "Vand", 1, "water")).BusinessErrors.Should().BeEmpty();
        Select(mode, true, Source(), Source("W", "Vand", 1, "water") with { End = On.AddDays(-1) }).BusinessErrors.Should().BeEmpty();
        Select(mode, true, Source(), Source("W", "Vand", 1, "water") with { Start = On.AddDays(1) }).BusinessErrors.Should().BeEmpty();
        Select(mode, true, Source(), Source("W", "El-ekstern", 1, "water")).BusinessErrors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Linked_main_selection_and_business_error_remain_separate(string mode)
    {
        var result = await Run(Reader(true, Source() with { MainContract = "W" }, Source("W", "Vand", 1, "water")), Mp1, [Record(Mp1)], mode);
        result.Coverage.Single().Outcome.Should().Be("BusinessError");
        result.Agreements.Should().ContainSingle(r => !r.EvidenceOnly && r.SourceContractNumber == "W" && r.SettlCustomerNumber == "E1");
        result.Agreements.Should().ContainSingle(r => r.EvidenceOnly && r.SourceContractNumber == "W" && r.SettlCustomerNumber == null);
    }

    [Theory]
    [InlineData("pbs")]
    [InlineData("mb")]
    public async Task Business_error_summary_counts_each_mp_once(string mode)
    {
        var result = await Run(Reader(true, Source(), Source("W", "Vand", 1, "water"), Source("H", "Varme", 2, "heat")), Mp1, mode: mode);
        result.Agreements.Should().HaveCount(2).And.OnlyContain(r => r.EvidenceOnly);
        result.ReportRows.Should().HaveCount(2);
        result.ReportRows.Select(row => row[1]).Should().Equal("W", "H");
        result.ReportRows.Should().OnlyContain(row => Equals(row[6],
            "Ingen " + (mode == "pbs" ? "PBS" : "MobilePay") + "-aftale på elkontrakten. Denne aftale findes på vand/varme."));
        result.Coverage.Single().ErrorExplanation.Should().Contain("W (Vand)").And.Contain("H (Varme)");
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(result.Summary);
        summary.GetProperty("businessErrors").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Missing_customer_is_unresolved_and_does_not_default_to_private()
    {
        var reader = Reader(false, Source() with { MainContract = "MAIN" }, Source("MAIN", "Vand", 1, "main"));
        reader.Snapshot = reader.Snapshot! with { Customers = [] };
        var result = await Run(reader, Mp1, [Record(Mp1)]);
        result.Coverage.Single().Outcome.Should().Be("Unresolved");
        result.Agreements.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("12345678", true)]
    [InlineData("1234567", false)]
    [InlineData("1234567X", false)]
    public void Customer_type_follows_existing_first_disponent_cvr_convention(string? cvr, bool business) =>
        PaymentMpCustomer.IsBusinessCvr(cvr).Should().Be(business);

    [Fact]
    public void Changed_or_missing_owning_contract_fails_the_read()
    {
        Action missing = () => Select("mb", false, Source("OTHER"));
        missing.Should().Throw<InvalidOperationException>();
        Action changed = () => Select("mb", false, Source() with { End = On.AddDays(-1) });
        changed.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(99, false)]
    [InlineData(-1, false)]
    public void Pbs_eligibility_keeps_shared_report_definitions(int code, bool expected) =>
        Source(pbs: code).HasPbs.Should().Be(expected);

    [Fact]
    public void Mobilepay_eligibility_keeps_default_reader_cancellation_rule()
    {
        Source(mobile: "own").HasMobile.Should().BeTrue();
        (Source(mobile: "own") with { MobileCancel = null }).HasMobile.Should().BeFalse();
        (Source(mobile: "own") with { MobileCancel = On.AddDays(5) }).HasMobile.Should().BeFalse();
    }
}
