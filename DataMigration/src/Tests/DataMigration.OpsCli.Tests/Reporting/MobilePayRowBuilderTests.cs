using DataMigration.OpsCli.Reporting;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Tests.Reporting;

/// <summary>
/// The matching rules: which Settl payment agreement belongs to which Xellent MobilePay agreement,
/// and what that makes the row's status. Previously reachable only through
/// <c>MobilePaySettlMatchService</c>, which needs a Settl environment and a live database — and in
/// Settl featuretest no migrated customer holds a MobilePay agreement, so the cases below could not
/// be reached at all.
/// </summary>
public class MobilePayRowBuilderTests
{
    private const string Account = "100054";
    private const string MainContract = "10500000";
    private const string SubContract = "10500001";
    private const string MpId = "571313179104952730";
    private static readonly DateOnly Cos = new(2026, 7, 1);

    /// <summary>End date of a contract the customer left long before cutover — account 116581's
    /// 10279962 ended 2025-11-16 while the change of supply was 2026-07-01.</summary>
    private static readonly DateOnly Abandoned = new(2025, 11, 16);

    // ── Status ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Migrated_customer_with_a_payment_agreement_matches_on_the_metering_point()
    {
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        var row = Assert.Single(rows);
        Assert.Equal(MobilePayMatchStatus.Matched, row.Status);
        Assert.Equal(MpId, row.MeteringPointId);
        Assert.Equal("E3521", row.EnummerSettl);
        Assert.False(row.IsJoint);
    }

    [Fact]
    public void Migrated_customer_without_any_payment_agreement_is_the_finding()
    {
        // No payment agreement of any method means no E-number, so there is nothing to key the
        // MobilePay agreement on and the handover cannot proceed for this customer.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: []);

        var row = Assert.Single(rows);
        Assert.Equal(MobilePayMatchStatus.NoSettlCustomerNumber, row.Status);
        Assert.Null(row.EnummerSettl);
        Assert.Equal(1, row.SettlMeteringPointCount);

        // The metering point still shows. Blanking it was what made the status impossible to act on:
        // the row asserted a customer with no E-number while hiding the very key that was looked up,
        // and the COS and migrated-contract columns beside it then appeared to come from nowhere.
        Assert.Equal(MpId, row.MeteringPointId);
        Assert.Equal("", row.MatchKey);
    }

    [Fact]
    public void An_account_with_nothing_in_Settl_carries_no_pinning_to_show()
    {
        // The service cannot produce this — it only reads agreements for accounts Settl resolved —
        // but the builder is public, so state what a caller gets: no E-number, and no pinning.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: [],
            records: []);

        var row = Assert.Single(rows);
        Assert.Equal(MobilePayMatchStatus.NoSettlCustomerNumber, row.Status);
        Assert.Equal(0, row.SettlMeteringPointCount);
        Assert.Equal("", row.MigratedContractNumber);
        Assert.Null(row.Cos);
        Assert.Null(row.IsOnMigratedContract);
        // The agreement's own contract end date is Xellent's and needs no pinning — it survives.
        Assert.Equal(Cos.AddDays(-1), row.SourceContractEndDate);
    }

    [Fact]
    public void A_payment_agreement_for_someone_elses_metering_point_does_not_match()
    {
        // Regression guard on the keying: the record must be reached through THIS account's metering
        // points, never by merely existing in the CSV.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: "571313179100000000")]);

        Assert.Equal(MobilePayMatchStatus.NoSettlCustomerNumber, Assert.Single(rows).Status);
    }

    // ── Payment method is irrelevant to the match, and stated anyway ──────────────────────────

    [Fact]
    public void Any_payment_method_supplies_the_E_number()
    {
        // Deliberate, and confirmed with the product owner 2026-07-30: what the sheet needs from
        // Settl is the customer's E-number, which identifies the customer and not the agreement.
        // Requiring a MobilePay-method row would empty the report — there were 5 of those in all of
        // Settl production — because creating the missing MobilePay agreements IS the handover.
        foreach (var method in new[] { "FI-Kort", "Betalingsservice", "MobilePay" })
        {
            var rows = Build(
                agreements: [Agreement()],
                settlMps: Mps(Account, MpId),
                records: [Record(mpId: MpId, paymentMethod: method)]);

            var row = Assert.Single(rows);
            Assert.Equal(MobilePayMatchStatus.Matched, row.Status);
            Assert.Equal("E3521", row.EnummerSettl);
        }
    }

    [Fact]
    public void The_row_names_the_method_the_E_number_came_from()
    {
        // Without this a green row reads as "MobilePay already exists in Settl", which is false for
        // all but a handful of customers.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId, paymentMethod: "Betalingsservice")]);

        Assert.Equal("Betalingsservice", Assert.Single(rows).EnummerSourcePaymentMethod);
    }

    [Fact]
    public void No_E_number_means_no_method_either()
    {
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: []);

        Assert.Null(Assert.Single(rows).EnummerSourcePaymentMethod);
    }

    // ── Every metering point on the account is tried ──────────────────────────────────────────

    [Fact]
    public void The_match_is_found_on_any_of_the_accounts_metering_points()
    {
        // The account-first version picked one metering point up front and missed the agreement when
        // it guessed wrong. There is no reason to pick: try them all.
        var second = "571313179104952731";
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId, second),
            records: [Record(mpId: second)]);

        var row = Assert.Single(rows);
        Assert.Equal(MobilePayMatchStatus.Matched, row.Status);
        Assert.Equal(second, row.MeteringPointId);
        Assert.Equal(2, row.SettlMeteringPointCount);
    }

    // ── Opsagte aftaler ───────────────────────────────────────────────────────────────────────
    //
    // Læseren returnerer dem kun når kaldet bad om det (--include-terminated). Hvad der så sker med
    // dem er builderens beslutning, og den følger PBS' regel: opsagt efter skiftedatoen er
    // migreringens eget værk, opsagt før er kundens.

    [Fact]
    public void An_agreement_the_migration_itself_closed_is_reported()
    {
        // Opsagt dagen efter leverandørskiftet. Det er præcis den kunde arket findes for: MobilePay
        // fulgte ikke med over, og uden denne række er tabet usynligt.
        var rows = Build(
            agreements: [Agreement(cancelDate: Cos.AddDays(1))],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        var row = Assert.Single(rows);
        Assert.Equal(Cos.AddDays(1), row.CancelDate);
        Assert.Equal("Opsagt 2026-07-02", PaymentMatchColumns.MobilePayAgreementStatusLabel(row.CancelDate));
    }

    [Fact]
    public void An_agreement_the_customer_closed_before_the_cutover_is_not_reported()
    {
        // Kunden holdt op med at betale med MobilePay mens de stadig var i Xellent. Migreringen tog
        // intet fra dem, og overdragelsen har intet at sige om det.
        var rows = Build(
            agreements: [Agreement(cancelDate: Cos.AddDays(-30))],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        Assert.Empty(rows);
    }

    [Fact]
    public void An_agreement_closed_on_the_cutover_day_itself_is_not_reported()
    {
        // Grænsen er streng: skiftedagen hører til før-siden, som PBS' IsTerminatedAfterCos også
        // afgør den.
        var rows = Build(
            agreements: [Agreement(cancelDate: Cos)],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        Assert.Empty(rows);
    }

    [Fact]
    public void An_uncancelled_agreement_carries_no_cancel_date()
    {
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        Assert.Null(Assert.Single(rows).CancelDate);
        Assert.Equal("Aktiv", PaymentMatchColumns.MobilePayAgreementStatusLabel(null));
    }

    // ── Joint invoice fallback ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_payment_agreement_without_a_metering_point_matches_on_the_joint_invoice()
    {
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(jointInvoice: MainContract)]);

        var row = Assert.Single(rows);
        Assert.Equal(MobilePayMatchStatus.Matched, row.Status);
        Assert.True(row.IsJoint);

        // The two columns part company here, which is why they are two: the account's metering point
        // is known, and the key that reached Settl is not a metering point at all.
        Assert.Equal(MpId, row.MeteringPointId);
        Assert.Equal(MainContract, row.MatchKey);
    }

    [Fact]
    public void The_joint_invoice_may_be_the_agreements_main_contract()
    {
        // The agreement sits on a sub-contract while the joint invoice is issued on the main one.
        var rows = Build(
            agreements: [Agreement(contract: SubContract, mainRef: MainContract)],
            settlMps: Mps(Account, MpId),
            records: [Record(jointInvoice: MainContract)]);

        Assert.True(Assert.Single(rows).IsJoint);
    }

    [Fact]
    public void The_metering_point_wins_over_the_joint_invoice()
    {
        // Both keys resolve. The metering point identifies one delivery point; the joint invoice
        // number can cover several, so it is the weaker evidence and only a fallback.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId, enummer: "E1111"), Record(jointInvoice: MainContract, enummer: "E2222")]);

        var row = Assert.Single(rows);
        Assert.False(row.IsJoint);
        Assert.Equal("E1111", row.EnummerSettl);
    }

    // ── The pinning is shown, and checked ─────────────────────────────────────────────────────

    [Fact]
    public void The_row_shows_the_contract_that_decided_the_account()
    {
        // Without these the sheet asserts an account and offers no way to test the assertion. The
        // reader can now hold the contract's end date up against the change-of-supplier date.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        var row = Assert.Single(rows);
        Assert.Equal(MainContract, row.MigratedContractNumber);
        Assert.Equal(Cos.AddDays(-1), row.SourceContractEndDate);
        Assert.Equal(Cos, row.Cos);
        Assert.Empty(row.Remarks);
    }

    [Fact]
    public void An_end_date_that_is_not_COS_minus_one_is_called_out()
    {
        // Should not occur now that only metering points in delivery are read — every one of those
        // has a change-of-supplier event to pin against. If it does, the account did not come from
        // that event and the attribution has to be checked by hand.
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)],
            owners: new(StringComparer.Ordinal)
            {
                [MpId] = new MeteringPointAccountResolver.OwnerContract(MainContract, Cos.AddDays(-40))
            });

        var row = Assert.Single(rows);
        Assert.Contains(MobilePayRowClassifier.PinningOffRemark, row.Remarks);
        Assert.Equal(MobilePayRowSeverity.Review, row.Severity);
    }

    [Fact]
    public void An_agreement_on_the_migrated_contract_says_so()
    {
        // Account 116581 in production: two uncancelled agreements, one on a contract that ended
        // 7½ months before cutover. The pinning already knew which was live; the sheet just never
        // said it, so a "FLERE AFTALER" row read as though the report had no idea.
        var leftoverContract = "10279962";
        var rows = Build(
            agreements:
            [
                Agreement(contract: MainContract, agreementId: "agr_live", agreementCount: 2),
                Agreement(contract: leftoverContract, agreementId: "agr_leftover", agreementCount: 2,
                          endDate: Abandoned)
            ],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        var live = rows.Single(r => r.MobilePayAgreementId == "agr_live");
        var leftover = rows.Single(r => r.MobilePayAgreementId == "agr_leftover");

        Assert.True(live.IsOnMigratedContract);
        Assert.False(leftover.IsOnMigratedContract);
        // The end date is the agreement's OWN contract's, not the migrated one's — otherwise both
        // rows would carry the same date and the leftover would look as current as the live one.
        Assert.Equal(Cos.AddDays(-1), live.SourceContractEndDate);
        Assert.Equal(Abandoned, leftover.SourceContractEndDate);
        Assert.Equal(MainContract, leftover.MigratedContractNumber);
        Assert.Contains(live.Remarks, r => r.Contains("denne sidder på den migrerede kontrakt"));
        Assert.Contains(leftover.Remarks, r => r.Contains("denne sidder IKKE på den migrerede kontrakt"));
    }

    [Fact]
    public void Without_a_pinning_the_question_is_left_open_rather_than_answered_No()
    {
        // Null, not false: "not on the migrated contract" would be a claim, and there is no owning
        // contract to compare against.
        var rows = Build(
            agreements: [Agreement(agreementCount: 2)],
            settlMps: [],
            records: []);

        var row = Assert.Single(rows);
        Assert.Null(row.IsOnMigratedContract);
        Assert.Contains(row.Remarks, r => r.EndsWith("(2)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pinning_shown_is_the_one_that_produced_the_match()
    {
        // Two metering points on the account, the match on the second. The provenance must follow
        // the matched metering point — showing the first would name a contract that had nothing to
        // do with the E-number on the row.
        var second = "571313179104952731";
        var rows = Build(
            agreements: [Agreement()],
            settlMps: Mps(Account, MpId, second),
            records: [Record(mpId: second)],
            owners: new(StringComparer.Ordinal)
            {
                [MpId] = new MeteringPointAccountResolver.OwnerContract("10400000", Cos.AddDays(-1)),
                [second] = new MeteringPointAccountResolver.OwnerContract("10400001", Cos.AddDays(-1))
            });

        Assert.Equal("10400001", Assert.Single(rows).MigratedContractNumber);
    }

    // ── Provenance survives ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_row_states_where_the_agreement_was_found()
    {
        // There is no rule for which contract a MobilePay agreement sits on, so the sheet has to say
        // where this one came from rather than leave it implied.
        var rows = Build(
            agreements: [Agreement(contract: SubContract, mainRef: MainContract, kind: MobilePaySourceKind.SubContract)],
            settlMps: Mps(Account, MpId),
            records: []);

        var row = Assert.Single(rows);
        Assert.Equal(SubContract, row.SourceContractNumber);
        Assert.Equal(MobilePaySourceKind.SubContract, row.SourceKind);
    }

    [Fact]
    public void One_row_per_agreement_even_on_the_same_account()
    {
        var rows = Build(
            agreements:
            [
                Agreement(agreementId: "agr_a", agreementCount: 2),
                Agreement(contract: SubContract, mainRef: MainContract, agreementId: "agr_b", agreementCount: 2)
            ],
            settlMps: Mps(Account, MpId),
            records: [Record(mpId: MpId)]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(MobilePayMatchStatus.Matched, r.Status));

        // The account has one metering point, owned by MainContract. The agreement sitting on that
        // contract is the live one and needs no decision; the one on the sub-contract owns no
        // migrated metering point, so which of the two the customer actually pays by is a question
        // only a human can close — and that row is the one that blocks.
        Assert.Equal(MobilePayRowSeverity.Review, rows.Single(r => r.MobilePayAgreementId == "agr_a").Severity);
        Assert.Equal(MobilePayRowSeverity.Blocking, rows.Single(r => r.MobilePayAgreementId == "agr_b").Severity);
    }

    [Fact]
    public void One_contract_per_metering_point_gives_each_agreement_its_own_point()
    {
        // Account 366645 in production: contracts 10355841 and 10355842, one metering point each,
        // one uncancelled MobilePay agreement each, both migrated at the same change of supply.
        // Both rows used to be reported against whichever metering point the account listed first,
        // so one metering point never appeared in the sheet at all and its agreement was flagged as
        // sitting on someone else's contract. These agreements do not compete.
        var mp41 = "571313175500233695";
        var mp42 = "571313175500233671";
        var rows = Build(
            agreements:
            [
                Agreement(contract: "10355841", agreementId: "agr_VaQXh8t", agreementCount: 2),
                Agreement(contract: "10355842", agreementId: "agr_qdvDmZ6", agreementCount: 2)
            ],
            // Listed with 10355842's point first, which is what exposed the bug.
            settlMps: Mps(Account, mp42, mp41),
            records: [Record(mpId: mp42)],
            owners: new(StringComparer.Ordinal)
            {
                [mp41] = new MeteringPointAccountResolver.OwnerContract("10355841", Cos.AddDays(-1)),
                [mp42] = new MeteringPointAccountResolver.OwnerContract("10355842", Cos.AddDays(-1))
            });

        var a41 = rows.Single(r => r.MobilePayAgreementId == "agr_VaQXh8t");
        var a42 = rows.Single(r => r.MobilePayAgreementId == "agr_qdvDmZ6");

        Assert.Equal(mp41, a41.MeteringPointId);
        Assert.Equal(mp42, a42.MeteringPointId);
        Assert.Equal("10355841", a41.MigratedContractNumber);
        Assert.Equal("10355842", a42.MigratedContractNumber);
        Assert.True(a41.IsOnMigratedContract);
        Assert.True(a42.IsOnMigratedContract);

        // Each sits on its own migrated contract, so the count is reported without blocking.
        Assert.All(rows, r => Assert.Equal(MobilePayRowSeverity.Review, r.Severity));
        Assert.All(rows, r => Assert.Contains(r.Remarks, x => x.Contains("denne sidder på den migrerede kontrakt")));
    }

    [Fact]
    public void The_agreements_own_metering_point_is_preferred_over_another_that_matched()
    {
        // Provenance follows the agreement, not the E-number: the E-number identifies the customer,
        // and any of their payment agreements supplies it. Showing the matched point instead would
        // name a contract that has nothing to do with where the agreement sits.
        var own = "571313175500233695";
        var other = "571313175500233671";
        var rows = Build(
            agreements: [Agreement(contract: "10355841")],
            settlMps: Mps(Account, other, own),
            records: [Record(mpId: other)],
            owners: new(StringComparer.Ordinal)
            {
                [own] = new MeteringPointAccountResolver.OwnerContract("10355841", Cos.AddDays(-1)),
                [other] = new MeteringPointAccountResolver.OwnerContract("10355842", Cos.AddDays(-1))
            });

        var row = Assert.Single(rows);
        Assert.Equal(MobilePayMatchStatus.Matched, row.Status);
        Assert.Equal(own, row.MeteringPointId);
        Assert.Equal(other, row.MatchKey);
        Assert.Equal("10355841", row.MigratedContractNumber);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unless a test says otherwise, every metering point pins cleanly: the owning contract ends the
    /// day before change of supply. That is what the scan produces now it only reads metering points
    /// in delivery, so it is the right default — a test that wants a broken pinning states it.
    /// </summary>
    private static List<MobilePayMatchReportRow> Build(
        MobilePayContract[] agreements,
        Dictionary<string, List<string>> settlMps,
        SettlCsvRecord[] records,
        Dictionary<string, MeteringPointAccountResolver.OwnerContract>? owners = null)
    {
        var allMps = settlMps.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToList();
        var cos = allMps.ToDictionary(mp => mp, _ => Cos, StringComparer.Ordinal);
        owners ??= allMps.ToDictionary(
            mp => mp,
            _ => new MeteringPointAccountResolver.OwnerContract(MainContract, Cos.AddDays(-1)),
            StringComparer.Ordinal);
        return MobilePayRowBuilder.Build(agreements, settlMps, cos, owners, records);
    }

    private static Dictionary<string, List<string>> Mps(string account, params string[] mpIds) =>
        new(StringComparer.Ordinal) { [account] = [.. mpIds] };

    private static MobilePayContract Agreement(
        string contract = MainContract,
        string? mainRef = null,
        MobilePaySourceKind kind = MobilePaySourceKind.MainContract,
        string agreementId = "agr_test",
        int agreementCount = 1,
        DateOnly? endDate = null,
        DateOnly? cancelDate = null)
        => new(
            Account: Account,
            MobilePayAgreementId: agreementId,
            Referencetomaincontract: mainRef,
            SourceContractNumber: contract,
            SourceKind: kind,
            // Defaults to a clean COS-1 end date; a test that wants an abandoned contract says so.
            SourceContractEndDate: endDate ?? Cos.AddDays(-1),
            AgreementCountOnAccount: agreementCount,
            CancelDate: cancelDate);

    // FI-Kort by default, because that is what Settl overwhelmingly holds: 8.262 FI-Kort and 3.233
    // Betalingsservice against 5 MobilePay agreements in production (2026-07-30).
    private static SettlCsvRecord Record(
        string? mpId = null, string? jointInvoice = null, string enummer = "E3521",
        string paymentMethod = "FI-Kort")
        => new()
        {
            PaymentAgreementNumber = "PA-1",
            PaymentMethod = paymentMethod,
            ExternalCustomerNumber = enummer,
            CustomerName = "Test",
            AgreementType = "Subscription",
            MeteringpointId = mpId,
            JointInvoiceNumber = jointInvoice
        };
}
