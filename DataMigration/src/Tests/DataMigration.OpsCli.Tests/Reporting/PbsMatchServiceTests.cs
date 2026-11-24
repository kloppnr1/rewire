using DataMigration.Core.Reporting;
using DataMigration.OpsCli.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Tests.Reporting;

/// <summary>
/// The Xellent provenance a PBS row carries — where the agreement was found and how contested the
/// metering point was. The service builds its rows in three separate places (metering-point match,
/// joint-invoice match, unmatched), so the columns can silently go missing on one path while the
/// other two look right.
/// </summary>
public class PbsMatchServiceTests
{
    private static XellentPbsContract Contract(
        string contract, string mp, string? mainRef = null, int candidates = 1, int expired = 0) =>
        new()
        {
            ContractNumber = contract,
            CustomerAccount = "111",
            PbsAgreementNumber = 12345,
            MeteringpointId = mp,
            Referencetomaincontract = mainRef,
            SourceKind = mainRef is null ? PbsSourceKind.Standalone : PbsSourceKind.SubContract,
            ContractStartDate = new DateOnly(2024, 3, 4),
            ContractEndDate = new DateOnly(2026, 6, 30),
            AgreementCountOnAccount = 2,
            CandidatesOnMeteringPoint = candidates,
            ExpiredCandidatesOnMeteringPoint = expired
        };

    private static SettlCsvRecord SettlRecord(string? mp = null, string? jointInvoice = null) =>
        new()
        {
            PaymentAgreementNumber = "PA-1",
            PaymentMethod = "Betalingsservice",
            ExternalCustomerNumber = "E-999",
            CustomerName = "Kunde",
            AgreementType = "Fast",
            MeteringpointId = mp,
            JointInvoiceNumber = jointInvoice
        };

    private static void AssertProvenance(PbsMatchReportRow row, int candidates, int expired)
    {
        Assert.Equal(PbsSourceKind.Standalone, row.SourceKind);
        Assert.Equal(new DateOnly(2024, 3, 4), row.ContractStartDate);
        Assert.Equal(new DateOnly(2026, 6, 30), row.ContractEndDate);
        Assert.Equal(2, row.AgreementCountOnAccount);
        Assert.Equal(candidates, row.CandidatesOnMeteringPoint);
        Assert.Equal(expired, row.ExpiredCandidatesOnMeteringPoint);
    }

    [Fact]
    public void A_metering_point_match_carries_the_provenance()
    {
        var rows = new PbsMatchService().Match(
            [Contract("c1", "571000000000000001", candidates: 3, expired: 1)],
            [SettlRecord(mp: "571000000000000001")]);

        var row = Assert.Single(rows);
        Assert.Equal(PbsMatchStatus.Matched, row.MatchStatus);
        AssertProvenance(row, candidates: 3, expired: 1);
    }

    [Fact]
    public void A_joint_invoice_match_carries_the_provenance()
    {
        // Reached by contract number rather than metering point, and built in its own loop — the
        // path most likely to be forgotten when a column is added.
        var rows = new PbsMatchService().Match(
            [Contract("c1", "571000000000000001")],
            [SettlRecord(jointInvoice: "c1")]);

        var row = Assert.Single(rows);
        Assert.Equal(PbsMatchStatus.MatchedJoint, row.MatchStatus);
        AssertProvenance(row, candidates: 1, expired: 0);
    }

    [Fact]
    public void An_unmatched_xellent_row_carries_the_provenance_too()
    {
        // Nothing matched in Settl, but the Xellent side is exactly as well known as on a matched
        // row — and this is the row a reader is most likely to have to explain.
        var rows = new PbsMatchService().Match(
            [Contract("c1", "571000000000000001", candidates: 2, expired: 1)],
            []);

        var row = Assert.Single(rows);
        Assert.Equal(PbsMatchStatus.UnmatchedXellent, row.MatchStatus);
        AssertProvenance(row, candidates: 2, expired: 1);
    }

    [Fact]
    public void A_settl_only_row_leaves_the_provenance_blank_rather_than_defaulted()
    {
        // There is no Xellent contract behind this row. A zero candidate count or a Standalone kind
        // would describe a contract that was never found — null is the only honest answer.
        var rows = new PbsMatchService().Match([], [SettlRecord(mp: "571000000000000009")]);

        var row = Assert.Single(rows);
        Assert.Equal(PbsMatchStatus.UnmatchedSettl, row.MatchStatus);
        Assert.Null(row.SourceKind);
        Assert.Null(row.ContractStartDate);
        Assert.Null(row.ContractEndDate);
        Assert.Null(row.AgreementCountOnAccount);
        Assert.Null(row.CandidatesOnMeteringPoint);
        Assert.Null(row.ExpiredCandidatesOnMeteringPoint);
    }

    // --- Annotering ---
    //
    // Bemærkningerne lå tidligere hos kalderen, i to kopier: CLI-runneren og dashboardet. De drev
    // fra hinanden i det øjeblik den ene blev rettet — en produktionseksport 2026-08-07 sagde
    // stadig "Ingen aktiv PBS i Xellent" fra dashboardet, mens CLI'en var omformuleret. Nu hører de
    // til matchet, og testene ligger her.

    private static PbsContractQueryResult QueryResult(
        IReadOnlyList<XellentPbsContract> contracts,
        Dictionary<string, ExcludedPbsContract>? excludedByMp = null,
        Dictionary<string, int>? contractCodes = null) =>
        new(contracts,
            new Dictionary<string, string>(),
            excludedByMp ?? [],
            contractCodes ?? []);

    [Fact]
    public void A_settl_row_whose_metering_point_has_no_agreement_is_named_and_explained()
    {
        // Rækken har ingen Xellent-kontrakt i matchet, men målepunktet ER kendt — kunden bag det kan
        // navngives, og det er forskellen på "ukendt målepunkt" og "kunde uden PBS".
        var excluded = new Dictionary<string, ExcludedPbsContract>
        {
            ["571000000000000009"] = new("222", "c-uden-pbs", "Andelsboligforeningen", 0, default)
        };

        var rows = new PbsMatchService().Match(
            QueryResult([], excluded), [SettlRecord(mp: "571000000000000009")],
            includeTerminated: false, changeOfSupplierDate: new DateTime(2026, 7, 1));

        var row = Assert.Single(rows);
        Assert.Equal("222", row.CustomerNumber);
        Assert.Equal("c-uden-pbs", row.ContractNumber);
        // Ikke "på denne kontrakt": efter fallbacken betyder kode 0 at hverken målepunktets egen
        // kontrakt eller dens hovedkontrakt bar en aftale.
        Assert.Equal("Ingen aktiv PBS på kundens kontrakter (Andelsboligforeningen)", row.Notes);
    }

    [Fact]
    public void A_terminated_agreement_that_matched_anyway_is_a_mismatch()
    {
        var rows = new PbsMatchService().Match(
            QueryResult([Contract("c1", "571000000000000001")],
                contractCodes: new Dictionary<string, int> { ["c1"] = 4 }),
            [SettlRecord(mp: "571000000000000001")],
            includeTerminated: false, changeOfSupplierDate: new DateTime(2026, 7, 1));

        var row = Assert.Single(rows);
        Assert.Equal(PbsMatchStatus.Mismatch, row.MatchStatus);
        Assert.Equal("Afmeldt i Xellent", row.Notes);
    }

    [Fact]
    public void With_include_terminated_the_match_stands()
    {
        // Flaget er brugerens udtrykkelige ønske om at få afmeldte med; så er et match ikke en
        // uoverensstemmelse.
        var rows = new PbsMatchService().Match(
            QueryResult([Contract("c1", "571000000000000001")],
                contractCodes: new Dictionary<string, int> { ["c1"] = 4 }),
            [SettlRecord(mp: "571000000000000001")],
            includeTerminated: true, changeOfSupplierDate: new DateTime(2026, 7, 1));

        var row = Assert.Single(rows);
        Assert.Equal(PbsMatchStatus.Matched, row.MatchStatus);
        Assert.Null(row.Notes);
    }

    [Fact]
    public void An_annotation_does_not_displace_the_note_the_row_already_had()
    {
        // Netop den fælde AddNote findes for: rækken samler en bemærkning i matchet og endnu en i
        // annoteringen, og en tildeling ville tabe den første uden at nogen så det.
        var rows = new PbsMatchService().Match(
            QueryResult([Contract("c1", "571000000000000001")],
                contractCodes: new Dictionary<string, int> { ["c1"] = 4 }),
            [SettlRecord(mp: "571000000000000001"), SettlRecord(mp: "571000000000000001")],
            includeTerminated: false, changeOfSupplierDate: new DateTime(2026, 7, 1));

        Assert.All(rows, r =>
        {
            Assert.Contains("2 Settl-rækker på samme målepunkt", r.Notes);
            Assert.Contains("Afmeldt i Xellent", r.Notes);
        });
    }
}
