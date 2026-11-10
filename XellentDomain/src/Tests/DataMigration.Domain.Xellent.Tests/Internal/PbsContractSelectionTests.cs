using DataMigration.Core.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// Which Xellent PBS contract owns a metering point at the cutover. Getting this wrong pairs a
/// customer's stale PBS agreement with the payment agreements of whoever holds the metering point
/// now — see the class comment on <see cref="PbsContractSelection"/> for the production case.
/// </summary>
public class PbsContractSelectionTests
{
    private static readonly DateTime NeverEnds = new(1900, 1, 1);
    private static readonly DateTime Cos = new(2026, 7, 1);

    private static PbsContractCandidate Candidate(
        string contract, string account, string mp, DateTime start, DateTime end, int code = 2) =>
        new(contract, account, $"Kunde {account}", 12345, mp, null, start, end,
            PbsTransdate: NeverEnds, PbsTransactionCode: code);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void Only_known_active_codes_qualify_without_the_termination_opt_in(int code)
    {
        Assert.True(PbsContractSelection.CountsAsAgreement(code, NeverEnds, false, Cos));
        Assert.True(PbsContractSelection.CountsAsAgreement(code, NeverEnds, true, Cos));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(99)]
    public void Missing_and_unknown_codes_never_qualify_even_with_termination_opt_in(int code)
    {
        var afterCos = Cos.AddDays(1);
        Assert.False(PbsContractSelection.CountsAsAgreement(code, afterCos, false, Cos));
        Assert.False(PbsContractSelection.CountsAsAgreement(code, afterCos, true, Cos));
        Assert.False(PbsContractSelection.CountsAsAgreement(code, afterCos, true, null));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Known_terminated_codes_require_opt_in_and_termination_strictly_after_COS(int code)
    {
        Assert.False(PbsContractSelection.CountsAsAgreement(code, Cos.AddDays(1), false, Cos));
        Assert.False(PbsContractSelection.CountsAsAgreement(code, Cos.AddDays(-1), true, Cos));
        Assert.False(PbsContractSelection.CountsAsAgreement(code, Cos, true, Cos));
        Assert.True(PbsContractSelection.CountsAsAgreement(code, Cos.AddDays(1), true, Cos));
        Assert.True(PbsContractSelection.CountsAsAgreement(code, NeverEnds, true, null));
    }

    [Fact]
    public void Code_three_is_cancelled_by_PI_and_preserved_on_the_excluded_contract()
    {
        var result = PbsContractSelection.Select(
            [Candidate("c3", "111", "571000000000000001", new DateTime(2025, 1, 1), NeverEnds, code: 3)],
            includeTerminated: false, changeOfSupplierDate: Cos);

        Assert.Empty(result.Contracts);
        Assert.Equal(3, result.ExcludedByMp["571000000000000001"].Pbstransactioncode);
        Assert.Equal("Aftale afmeldt af PI", result.ContractStatusMap["c3"]);
        Assert.False(PbsContractSelection.CountsAsAgreement(3, Cos, true, Cos));
        Assert.True(PbsContractSelection.CountsAsAgreement(3, NeverEnds, true, null));
    }

    [Fact]
    public void Contract_that_never_ends_ran_at_the_cutover()
    {
        Assert.True(PbsContractSelection.RanAtCutover(NeverEnds, Cos));
    }

    [Fact]
    public void Contract_ended_by_the_migration_itself_still_counts()
    {
        // Migrating ends the Xellent contract the day before the change of supplier. Those are the
        // contracts the handover report exists to describe, so they must survive the filter.
        Assert.True(PbsContractSelection.RanAtCutover(new DateTime(2026, 6, 30), Cos));
    }

    [Fact]
    public void Contract_that_ended_before_the_cutover_does_not_count()
    {
        // Account 406242 in production: contract on metering point 571313115200207046 ended
        // 2026-01-31, five months before the cutover. The customer had moved out.
        Assert.False(PbsContractSelection.RanAtCutover(new DateTime(2026, 1, 31), Cos));
        // Even a move-out days before the cutover is a move-out.
        Assert.False(PbsContractSelection.RanAtCutover(new DateTime(2026, 6, 29), Cos));
    }

    [Fact]
    public void Without_a_cutover_date_no_contract_is_ruled_out()
    {
        // There is nothing to measure against, so the filter must not invent a reference date —
        // measuring at today would discard every migrated contract.
        Assert.True(PbsContractSelection.RanAtCutover(new DateTime(2020, 1, 1), null));
    }

    [Fact]
    public void A_customer_who_moved_out_before_the_cutover_no_longer_owns_the_metering_point()
    {
        var result = PbsContractSelection.Select(
            [Candidate("10585973", "406242", "571313115200207046",
                start: new DateTime(2025, 11, 1), end: new DateTime(2026, 1, 31))],
            includeTerminated: false, changeOfSupplierDate: Cos);

        // No matchable contract: the metering point is not this profile's any more.
        Assert.Empty(result.Contracts);

        // And nothing is said about the row. Annotating it with account 406242 would hang this
        // customer's number on the agreement of whoever holds the metering point now — the same
        // conflation the filter exists to remove.
        Assert.DoesNotContain("571313115200207046", result.ExcludedByMp);
        Assert.DoesNotContain("10585973", result.ContractStatusMap);

        // The count is the only trace, so the filter does not work in silence.
        Assert.Equal(1, result.ExpiredBeforeCutoverMeteringPoints);
    }

    [Fact]
    public void An_expired_contract_does_not_shadow_the_one_still_in_force()
    {
        // Overlapping contracts happen: the expired one can carry the later start date, so filtering
        // has to come before picking a winner.
        var result = PbsContractSelection.Select(
            [
                Candidate("live", "111", "571000000000000001",
                    start: new DateTime(2024, 1, 1), end: NeverEnds),
                Candidate("stale", "111", "571000000000000001",
                    start: new DateTime(2025, 6, 1), end: new DateTime(2025, 12, 31))
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        var contract = Assert.Single(result.Contracts);
        Assert.Equal("live", contract.ContractNumber);
        Assert.DoesNotContain("571000000000000001", result.ExcludedByMp);
    }

    [Fact]
    public void Newest_in_force_contract_wins_the_metering_point()
    {
        var result = PbsContractSelection.Select(
            [
                Candidate("older", "111", "571000000000000001",
                    start: new DateTime(2024, 1, 1), end: new DateTime(2026, 6, 30)),
                Candidate("newer", "222", "571000000000000001",
                    start: new DateTime(2026, 2, 1), end: NeverEnds)
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        Assert.Equal("newer", Assert.Single(result.Contracts).ContractNumber);
    }

    [Fact]
    public void Contract_without_pbs_is_still_annotated()
    {
        // Code 0 means the customer holds the metering point but has no PBS agreement. Unlike a
        // move-out, the account really is the one behind the metering point, so naming it on the
        // row says something true.
        var result = PbsContractSelection.Select(
            [Candidate("c1", "111", "571000000000000001",
                start: new DateTime(2025, 1, 1), end: NeverEnds, code: 0)],
            includeTerminated: false, changeOfSupplierDate: Cos);

        Assert.Empty(result.Contracts);
        var excluded = Assert.Contains("571000000000000001", result.ExcludedByMp);
        Assert.Equal("111", excluded.CustomerAccount);
        Assert.Equal(0, excluded.Pbstransactioncode);
        Assert.Equal(0, result.ExpiredBeforeCutoverMeteringPoints);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Terminated_pbs_is_excluded_unless_it_was_terminated_after_the_cutover(int code)
    {
        PbsContractQueryResult Run(bool includeTerminated, DateTime pbsTransdate) =>
            PbsContractSelection.Select(
                [new PbsContractCandidate("c1", "111", "Kunde", 12345, "571000000000000001", null,
                    ContractStartDate: new DateTime(2025, 1, 1), ContractEndDate: NeverEnds,
                    PbsTransdate: pbsTransdate, PbsTransactionCode: code)],
                includeTerminated, Cos);

        Assert.Empty(Run(includeTerminated: false, new DateTime(2026, 8, 1)).Contracts);
        Assert.Empty(Run(includeTerminated: true, new DateTime(2026, 5, 1)).Contracts);
        Assert.Single(Run(includeTerminated: true, new DateTime(2026, 8, 1)).Contracts);
    }

    [Fact]
    public void A_contested_metering_point_reports_how_many_lost_it_and_why()
    {
        // The PBS answer to MobilePay's "is the agreement on the migrated contract": the winning row
        // has to say how contested the metering point was, and whether the competition was real
        // (another live contract) or historical (a former holder who moved out).
        var result = PbsContractSelection.Select(
            [
                Candidate("live", "111", "571000000000000001",
                    start: new DateTime(2026, 2, 1), end: NeverEnds),
                Candidate("moved-out", "222", "571000000000000001",
                    start: new DateTime(2024, 1, 1), end: new DateTime(2025, 12, 31)),
                Candidate("also-live", "111", "571000000000000001",
                    start: new DateTime(2024, 6, 1), end: NeverEnds)
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        var contract = Assert.Single(result.Contracts);
        Assert.Equal("live", contract.ContractNumber);
        Assert.Equal(3, contract.CandidatesOnMeteringPoint);
        Assert.Equal(1, contract.ExpiredCandidatesOnMeteringPoint);
    }

    [Fact]
    public void An_uncontested_metering_point_reports_one_candidate_and_no_expiry()
    {
        var result = PbsContractSelection.Select(
            [Candidate("c1", "111", "571000000000000001",
                start: new DateTime(2025, 1, 1), end: NeverEnds)],
            includeTerminated: false, changeOfSupplierDate: Cos);

        var contract = Assert.Single(result.Contracts);
        Assert.Equal(1, contract.CandidatesOnMeteringPoint);
        Assert.Equal(0, contract.ExpiredCandidatesOnMeteringPoint);
    }

    [Fact]
    public void The_contract_period_is_carried_onto_the_row_with_the_sentinel_blanked()
    {
        var result = PbsContractSelection.Select(
            [
                Candidate("open", "111", "571000000000000001",
                    start: new DateTime(2025, 1, 1), end: NeverEnds),
                Candidate("migrated", "222", "571000000000000002",
                    start: new DateTime(2024, 3, 4), end: new DateTime(2026, 6, 30))
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        var open = result.Contracts.Single(c => c.ContractNumber == "open");
        Assert.Equal(new DateOnly(2025, 1, 1), open.ContractStartDate);
        // 1900-01-01 is AX for "no end date", not a date to put in front of a reader.
        Assert.Null(open.ContractEndDate);

        var migrated = result.Contracts.Single(c => c.ContractNumber == "migrated");
        Assert.Equal(new DateOnly(2024, 3, 4), migrated.ContractStartDate);
        Assert.Equal(new DateOnly(2026, 6, 30), migrated.ContractEndDate);
    }

    [Fact]
    public void Source_kind_separates_a_main_contract_from_a_standalone_one()
    {
        PbsContractCandidate WithPlacement(string contract, string mp, string? mainRef, bool hasSubs) =>
            new(contract, "111", "Kunde", 12345, mp, mainRef,
                ContractStartDate: new DateTime(2025, 1, 1), ContractEndDate: NeverEnds,
                PbsTransdate: NeverEnds, PbsTransactionCode: 2, HasSubContracts: hasSubs);

        var result = PbsContractSelection.Select(
            [
                WithPlacement("main", "571000000000000001", null, hasSubs: true),
                WithPlacement("sub", "571000000000000002", "main", hasSubs: false),
                // Having no parent is not what makes a contract a main one — the case that made
                // every standalone contract report "Hovedkontrakt" before the kind was three-valued.
                WithPlacement("alone", "571000000000000003", null, hasSubs: false)
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        Assert.Equal(PbsSourceKind.MainContract, result.Contracts.Single(c => c.ContractNumber == "main").SourceKind);
        Assert.Equal(PbsSourceKind.SubContract, result.Contracts.Single(c => c.ContractNumber == "sub").SourceKind);
        Assert.Equal(PbsSourceKind.Standalone, result.Contracts.Single(c => c.ContractNumber == "alone").SourceKind);
    }

    [Fact]
    public void Agreements_on_the_account_count_only_the_ones_in_force()
    {
        // An agreement number sits on exactly one contract, and such a contract names exactly one
        // consumption metering point (AXDB50 2026-08-07). The count is therefore equally
        // "agreements", "the contracts bearing them" and "metering points paid for separately".
        //
        // It is not the account's metering-point count: a main contract's agreement also pays for
        // sub-contract points that have none of their own, up to 411 of them on one agreement. What
        // the number decides is whether the customer pays for everything the same way.
        //
        // Which leaves the filtering as the only rule worth stating: what is NOT in force does not
        // count.
        PbsContractCandidate WithAgreement(
            string contract, string mp, int agreementNumber, DateTime end, int code = 2) =>
            new(contract, "111", "Kunde", agreementNumber, mp, null,
                ContractStartDate: new DateTime(2025, 1, 1), ContractEndDate: end,
                PbsTransdate: NeverEnds, PbsTransactionCode: code);

        var result = PbsContractSelection.Select(
            [
                WithAgreement("a", "571000000000000001", 111, NeverEnds),
                WithAgreement("b", "571000000000000002", 222, NeverEnds),
                // Expired before the cutover: not one of the account's current ways to pay.
                WithAgreement("c", "571000000000000003", 333, new DateTime(2025, 12, 31)),
                // Terminated PBS is likewise not in force.
                WithAgreement("d", "571000000000000004", 444, NeverEnds, code: 4)
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        Assert.All(result.Contracts, c => Assert.Equal(2, c.AgreementCountOnAccount));
    }

    // --- The agreement may sit on the metering point's main contract ---
    //
    // create has read billing that way since 4dc57f2: CustomerMapper joins through
    // Referencetomaincontract, without a delivery-category filter. The report did not, so a customer
    // migrated with PaymentChannel=BS could be reported as having no PBS at all.

    private static PbsAccountAgreement Agreement(
        string contract, string account, int agreementNumber,
        string deliveryCategory = "", int code = 2, DateTime? end = null, DateTime? transdate = null) =>
        new(account, contract, deliveryCategory, agreementNumber, code,
            transdate ?? NeverEnds, end ?? NeverEnds);

    /// <summary>A metering-point contract with no PBS of its own, hanging under a main contract.</summary>
    private static PbsContractCandidate SubContractWithoutPbs(
        string contract, string account, string mp, string mainContract) =>
        new(contract, account, $"Kunde {account}", 0, mp, mainContract,
            ContractStartDate: new DateTime(2025, 1, 1), ContractEndDate: NeverEnds,
            PbsTransdate: NeverEnds, PbsTransactionCode: 0);

    [Fact]
    public void An_agreement_on_the_main_contract_rescues_a_metering_point_whose_own_contract_has_none()
    {
        // The shape this fallback is for, and by far the common one: the metering point hangs on a
        // sub-contract carrying no PBS, while the account's payment arrangement sits on the
        // blank-category main contract above it. 8.830 accounts in AXDB50_TEST look like this.
        var result = PbsContractSelection.Select(
            [SubContractWithoutPbs("el-kontrakt", "947686", "571000000000000001", "varme-kontrakt")],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("varme-kontrakt", "947686", 98765, deliveryCategory: "Varme")]);

        var contract = Assert.Single(result.Contracts);
        Assert.Equal(98765, contract.PbsAgreementNumber);

        // The row still belongs to the contract that owns the metering point — only the agreement
        // came from elsewhere, and the row has to say so. Otherwise ContractNumber and
        // PbsAgreementNumber name two different contracts without a word about it.
        Assert.Equal("el-kontrakt", contract.ContractNumber);
        Assert.Equal("varme-kontrakt", contract.PbsAgreementSourceContract);
        Assert.Equal("Varme", contract.PbsAgreementSourceDeliveryCategory);

        // And it is no longer reported as a customer without PBS.
        Assert.DoesNotContain("571000000000000001", result.ExcludedByMp);

        // The status shown is the reported agreement's, not the owning contract's "Ingen PBS" —
        // which would sit next to a PBS number and contradict it.
        Assert.Equal("Aktiv", result.ContractStatusMap["el-kontrakt"]);
    }

    [Fact]
    public void An_agreement_on_a_sibling_contract_is_not_borrowed()
    {
        // A sibling's agreement is another contract's arrangement, not the account's. create reads
        // only the main contract, so borrowing here would report PBS for customers the migration
        // gave none — a new disagreement in place of the one this fallback removes. 3.395 accounts
        // in AXDB50_TEST have an agreement only on a sibling, and they must stay unreported.
        var result = PbsContractSelection.Select(
            [SubContractWithoutPbs("el-kontrakt", "111", "571000000000000001", "hovedkontrakt")],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("soeskende", "111", 98765)]);

        Assert.Empty(result.Contracts);
        Assert.Contains("571000000000000001", result.ExcludedByMp);
    }

    [Fact]
    public void A_contract_without_a_main_contract_has_nothing_to_inherit()
    {
        // Standalone: no parent, so its own "Ingen PBS" is the whole answer.
        var result = PbsContractSelection.Select(
            [Candidate("alene", "111", "571000000000000001",
                start: new DateTime(2025, 1, 1), end: NeverEnds, code: 0)],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("anden", "111", 98765)]);

        Assert.Empty(result.Contracts);
    }

    [Fact]
    public void A_contract_with_its_own_agreement_does_not_borrow_one()
    {
        var result = PbsContractSelection.Select(
            [Candidate("egen", "111", "571000000000000001",
                start: new DateTime(2025, 1, 1), end: NeverEnds)],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("anden", "111", 98765)]);

        var contract = Assert.Single(result.Contracts);
        Assert.Equal(12345, contract.PbsAgreementNumber);
        // Null is what says "this came from the metering point's own contract". Filling it in on
        // every row would make the fallback impossible to spot.
        Assert.Null(contract.PbsAgreementSourceContract);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_terminated_agreement_is_not_reached_past(int code)
    {
        // A terminated code is a decision about this contract, not an absence of one. Falling back here would
        // re-admit exactly what a run without --include-terminated leaves out — and note the
        // contract does have a main contract to inherit from, so the test is not passing merely for
        // want of a parent.
        var result = PbsContractSelection.Select(
            [new PbsContractCandidate("opsagt", "111", "Kunde", 12345, "571000000000000001", "hovedkontrakt",
                ContractStartDate: new DateTime(2025, 1, 1), ContractEndDate: NeverEnds,
                PbsTransdate: new DateTime(2026, 5, 1), PbsTransactionCode: code)],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("hovedkontrakt", "111", 98765)]);

        Assert.Empty(result.Contracts);
        Assert.Contains("571000000000000001", result.ExcludedByMp);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void The_fallback_judges_termination_by_the_same_rule_the_winner_is_judged_by(int code)
    {
        // Migrating a customer terminates their PBS in Xellent, so terminated is the ordinary state
        // for the population this report describes. A fallback held to a stricter rule than
        // `matchable` would drop precisely those customers.
        PbsContractQueryResult Run(bool includeTerminated, DateTime transdate) =>
            PbsContractSelection.Select(
                [SubContractWithoutPbs("el", "111", "571000000000000001", "hovedkontrakt")],
                includeTerminated, Cos,
                accountAgreements: [Agreement("hovedkontrakt", "111", 98765, code: code, transdate: transdate)]);

        // Terminated by the migration itself (after COS), and the flag says to include those.
        Assert.Single(Run(includeTerminated: true, new DateTime(2026, 8, 1)).Contracts);
        // Same agreement, but the run does not include terminated ones.
        Assert.Empty(Run(includeTerminated: false, new DateTime(2026, 8, 1)).Contracts);
        // Terminated before the cutover: the customer stopped paying that way, flag or not.
        Assert.Empty(Run(includeTerminated: true, new DateTime(2026, 5, 1)).Contracts);
    }

    [Fact]
    public void A_main_contract_the_customer_left_is_not_borrowed_from()
    {
        // Still code 2, but on a contract that ended long before the cutover. That is a leftover,
        // not the way the customer pays now — the same rule the candidates are held to.
        var result = PbsContractSelection.Select(
            [SubContractWithoutPbs("el", "111", "571000000000000001", "hovedkontrakt")],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("hovedkontrakt", "111", 98765, end: new DateTime(2025, 3, 31))]);

        Assert.Empty(result.Contracts);
    }

    [Fact]
    public void A_main_contract_number_belonging_to_another_customer_is_not_borrowed_from()
    {
        // Contract numbers are only unique within a data area, so the lookup is keyed by account as
        // well. Matching on the number alone would hang one customer's agreement on another's
        // metering point.
        var result = PbsContractSelection.Select(
            [SubContractWithoutPbs("el", "111", "571000000000000001", "hovedkontrakt")],
            includeTerminated: false, changeOfSupplierDate: Cos,
            accountAgreements: [Agreement("hovedkontrakt", "222", 98765)]);

        Assert.Empty(result.Contracts);
        Assert.Contains("571000000000000001", result.ExcludedByMp);
    }

    [Fact]
    public void A_metering_point_with_a_live_contract_is_not_counted_as_expired()
    {
        var result = PbsContractSelection.Select(
            [
                Candidate("live", "111", "571000000000000001",
                    start: new DateTime(2024, 1, 1), end: NeverEnds),
                Candidate("stale", "111", "571000000000000001",
                    start: new DateTime(2025, 6, 1), end: new DateTime(2025, 12, 31))
            ],
            includeTerminated: false, changeOfSupplierDate: Cos);

        // The count is per metering point, not per contract: this one still has an owner.
        Assert.Equal(0, result.ExpiredBeforeCutoverMeteringPoints);
    }
}
