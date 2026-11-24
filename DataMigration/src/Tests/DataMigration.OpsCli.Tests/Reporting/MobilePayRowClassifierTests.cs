using DataMigration.OpsCli.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Tests.Reporting;

/// <summary>
/// The rules that decide what the operator sees. Shared by the Excel sheet and the dashboard, so
/// a change here changes both.
/// </summary>
public class MobilePayRowClassifierTests
{
    // ── The finding the report exists for ─────────────────────────────────────────────────────

    [Fact]
    public void Migrated_customer_without_a_payment_agreement_blocks_the_handover()
    {
        // The customer is in Settl but Settl has no payment agreement for them. Nobody is going to
        // notice unless the report says so — they simply lose MobilePay.
        var agreement = Agreement();
        var remarks = MobilePayRowClassifier.BuildRemarks(
            agreement, MobilePayMatchStatus.NoSettlCustomerNumber, meteringPointsTried: 2);

        // And it has to say what was searched for. "No E-number found" without naming the keys is
        // unreadable: it gives the reader nothing to repeat the lookup with, so a customer Settl
        // genuinely has no agreement for is indistinguishable from an export that misses them.
        var missing = Assert.Single(
            remarks, r => r.StartsWith(MobilePayRowClassifier.MissingAgreementRemark, StringComparison.Ordinal));
        Assert.Contains("2 målepunkter", missing);
        Assert.Contains(agreement.SourceContractNumber, missing);
        Assert.Equal(
            MobilePayRowSeverity.Blocking,
            MobilePayRowClassifier.Classify(agreement, MobilePayMatchStatus.NoSettlCustomerNumber, remarks));
    }

    [Fact]
    public void Matched_row_is_clean()
    {
        var agreement = Agreement();
        var remarks = MobilePayRowClassifier.BuildRemarks(agreement, MobilePayMatchStatus.Matched);

        Assert.Empty(remarks);
        Assert.Equal(
            MobilePayRowSeverity.None,
            MobilePayRowClassifier.Classify(agreement, MobilePayMatchStatus.Matched, remarks));
    }

    // ── Placement questions ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Competing_agreements_block_even_when_matched()
    {
        var agreement = Agreement(agreementCount: 2);
        var remarks = MobilePayRowClassifier.BuildRemarks(agreement, MobilePayMatchStatus.Matched);

        Assert.Contains(remarks, r => r.Contains("FLERE AFTALER") && r.Contains("(2)"));
        Assert.Equal(
            MobilePayRowSeverity.Blocking,
            MobilePayRowClassifier.Classify(agreement, MobilePayMatchStatus.Matched, remarks));
    }

    [Fact]
    public void Sub_contract_placement_is_reported_but_only_needs_a_look()
    {
        var agreement = Agreement(kind: MobilePaySourceKind.SubContract);
        var remarks = MobilePayRowClassifier.BuildRemarks(agreement, MobilePayMatchStatus.Matched);

        Assert.Contains(MobilePayRowClassifier.SubContractRemark, remarks);
        Assert.Equal(
            MobilePayRowSeverity.Review,
            MobilePayRowClassifier.Classify(agreement, MobilePayMatchStatus.Matched, remarks));
    }

    [Fact]
    public void Missing_agreement_and_sub_contract_are_both_reported()
    {
        var agreement = Agreement(kind: MobilePaySourceKind.SubContract);
        var remarks = MobilePayRowClassifier.BuildRemarks(agreement, MobilePayMatchStatus.NoSettlCustomerNumber);

        Assert.Equal(2, remarks.Count);
        Assert.Equal(
            MobilePayRowSeverity.Blocking,
            MobilePayRowClassifier.Classify(agreement, MobilePayMatchStatus.NoSettlCustomerNumber, remarks));
    }

    // ── Pinning ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pinning_that_does_not_add_up_is_stated_on_the_row()
    {
        // The owning contract does not end the day before change of supply, so the account was not
        // established by that event. The agreement can be attributed to the wrong customer, and
        // nothing else in the output would show it.
        var agreement = Agreement();
        var remarks = MobilePayRowClassifier.BuildRemarks(
            agreement, MobilePayMatchStatus.Matched, pinningOff: true);

        Assert.Contains(MobilePayRowClassifier.PinningOffRemark, remarks);
        Assert.Equal(
            MobilePayRowSeverity.Review,
            MobilePayRowClassifier.Classify(agreement, MobilePayMatchStatus.Matched, remarks));
    }

    [Fact]
    public void A_clean_COS_minus_one_pinning_is_not_remarked_on()
    {
        var agreement = Agreement();
        var remarks = MobilePayRowClassifier.BuildRemarks(
            agreement, MobilePayMatchStatus.Matched, pinningOff: false);

        Assert.DoesNotContain(MobilePayRowClassifier.PinningOffRemark, remarks);
    }

    // ── Optælling ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Counts_follow_the_rows_they_are_computed_from()
    {
        // Regression: the runner printed the whole environment's tallies next to a profile's row
        // count, so a profile view looked like it had thousands of unmigrated customers.
        var rows = new List<MobilePayMatchReportRow>
        {
            RowWith(MobilePayMatchStatus.Matched),
            RowWith(MobilePayMatchStatus.NoSettlCustomerNumber)
        };

        var scan = new MobilePayScanFacts(
            SettlMeteringPointsScanned: 12493, MigratedAccountsResolved: 529, AgreementsRead: 529);

        var all = MobilePayMatchResult.From(rows, scan);
        Assert.Equal(1, all.Matched);
        Assert.Equal(1, all.NoSettlCustomerNumber);

        var subset = MobilePayMatchResult.From(rows.Where(r => r.Status == MobilePayMatchStatus.Matched));
        Assert.Equal(1, subset.Matched);
        Assert.Equal(0, subset.NoSettlCustomerNumber);
        Assert.Single(subset.Rows);
    }

    [Fact]
    public void Filtering_to_a_profile_narrows_the_rows_but_not_the_scan()
    {
        // How much of Settl was read is a fact about the run, not about which rows survived. The
        // profile view used to re-project and thereby lose it.
        var scan = new MobilePayScanFacts(12493, 529, 640);
        var rows = new List<MobilePayMatchReportRow> { RowWith(MobilePayMatchStatus.Matched) };

        var filtered = MobilePayMatchResult.From(rows, scan);

        Assert.Equal(12493, filtered.Scan!.SettlMeteringPointsScanned);
        Assert.Equal(529, filtered.Scan.MigratedAccountsResolved);
        Assert.Equal(640, filtered.Scan.AgreementsRead);
    }

    private static MobilePayMatchReportRow RowWith(MobilePayMatchStatus status) => new()
    {
        Account = "100054",
        Status = status,
        Remarks = status == MobilePayMatchStatus.NoSettlCustomerNumber
            ? [MobilePayRowClassifier.MissingAgreementRemark]
            : []
    };

    // ── Regression guard ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ended_source_contract_is_no_longer_remarked_on()
    {
        // The account-first version reported this, because it had to guess whether an ended
        // contract still counted. Going via COS-1 the end date IS the key, so it says nothing —
        // the reader no longer even carries the flag.
        var remarks = MobilePayRowClassifier.BuildRemarks(Agreement(), MobilePayMatchStatus.Matched);

        Assert.DoesNotContain(remarks, r => r.Contains("udløbet"));
    }

    private static MobilePayContract Agreement(
        MobilePaySourceKind kind = MobilePaySourceKind.MainContract,
        int agreementCount = 1)
        => new(
            Account: "100054",
            MobilePayAgreementId: "agr_test",
            Referencetomaincontract: null,
            SourceContractNumber: "10500000",
            SourceKind: kind,
            SourceContractEndDate: new DateOnly(2026, 6, 30),
            AgreementCountOnAccount: agreementCount);
}
