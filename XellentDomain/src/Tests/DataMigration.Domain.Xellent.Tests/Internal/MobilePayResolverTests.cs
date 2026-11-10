using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// MobilePay agreements sit wherever the customer signed up — there is no rule. These cases are
/// the real shapes found in Xellent (prod, 2026-07); the account numbers name the customer each
/// case was taken from so the expectation can be re-checked against the database.
/// </summary>
public class MobilePayResolverTests
{
    private static readonly DateTime NotCancelled = MigrationConstants.EndDateNull;

    // ── The case that motivated the change ────────────────────────────────────────────────────

    [Fact]
    public void Agreement_on_sub_contract_is_found()
    {
        // Acct 100054: EV-charging sub-contract 10513943 carries the agreement; the main
        // contract has none. The old main-contract-only lookup dropped this customer entirely.
        var rows = new[]
        {
            Row("100054", contract: "10513943", mainRef: "10512000", agreement: "agr_yUAn5KHCYx"),
            Row("100054", contract: "10512000", mainRef: null, agreement: null),
        };

        var pick = MobilePayResolver.Resolve(rows)["100054"];

        Assert.Equal("agr_yUAn5KHCYx", pick.Winner.AgreementId);
        Assert.Equal("10513943", pick.Winner.ContractNumber);
        Assert.Equal(MobilePayOrigin.SubContract, pick.Winner.Origin);
        Assert.Equal(1, pick.CandidateCount);
    }

    [Fact]
    public void Agreement_on_main_contract_of_another_commodity_is_found()
    {
        // Acct 102704: agreement on the water/heat main contract, while the electricity and
        // EV-charging contracts hang under it.
        var rows = new[]
        {
            Row("102704", contract: "10067704", mainRef: null, agreement: "agr_CadmXa6"),
            Row("102704", contract: "10521819", mainRef: "10067704", agreement: null,
                mainAgreement: "agr_CadmXa6"),
        };

        var pick = MobilePayResolver.Resolve(rows)["102704"];

        Assert.Equal("agr_CadmXa6", pick.Winner.AgreementId);
        Assert.Equal("10067704", pick.Winner.ContractNumber);
        Assert.Equal(MobilePayOrigin.MainContract, pick.Winner.Origin);
        Assert.Equal(1, pick.CandidateCount);   // same agreement seen twice — one agreement
    }

    // ── Competing agreements ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_agreements_on_separate_main_contracts_are_counted_as_competing()
    {
        // Acct 102406: water/heat main contract and EV-charging main contract each carry their
        // own active agreement. Lowest contract number wins; the count exposes the other one.
        var rows = new[]
        {
            Row("102406", contract: "10067406", mainRef: null, agreement: "agr_a79en5TAqy"),
            Row("102406", contract: "10498956", mainRef: null, agreement: "agr_3QRvfXxE9N"),
        };

        var pick = MobilePayResolver.Resolve(rows)["102406"];

        Assert.Equal("agr_a79en5TAqy", pick.Winner.AgreementId);
        Assert.Equal(2, pick.CandidateCount);
    }

    [Fact]
    public void Main_contract_wins_over_sub_contract()
    {
        var rows = new[]
        {
            Row("123533", contract: "10556123", mainRef: "10088533", agreement: "agr_2USLXCR"),
            Row("123533", contract: "10088533", mainRef: null, agreement: "agr_qT2QYBC"),
        };

        var pick = MobilePayResolver.Resolve(rows)["123533"];

        Assert.Equal("agr_qT2QYBC", pick.Winner.AgreementId);
        Assert.Equal(MobilePayOrigin.MainContract, pick.Winner.Origin);
        Assert.Equal(2, pick.CandidateCount);
    }

    [Fact]
    public void Two_agreements_both_on_sub_contracts_still_pick_lowest_contract_number()
    {
        // Acct 424489: both agreements sit on sub-contracts.
        var rows = new[]
        {
            Row("424489", contract: "10588498", mainRef: "10490000", agreement: "agr_2B7AsDE"),
            Row("424489", contract: "10496939", mainRef: "10490000", agreement: "agr_PfvFuBATPk"),
        };

        var pick = MobilePayResolver.Resolve(rows)["424489"];

        Assert.Equal("agr_PfvFuBATPk", pick.Winner.AgreementId);
        Assert.Equal(MobilePayOrigin.SubContract, pick.Winner.Origin);
        Assert.Equal(2, pick.CandidateCount);
    }

    // ── Not-an-agreement ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancelled_agreement_is_ignored()
    {
        // Acct 100029: agreement on the main contract, cancelled 2024-10-09.
        var rows = new[]
        {
            Row("100029", contract: "10065029", mainRef: null, agreement: "agr_9JJF864",
                cancelDate: new DateTime(2024, 10, 9)),
            Row("100029", contract: "10288038", mainRef: "10065029", agreement: null,
                mainAgreement: "agr_9JJF864", mainCancelDate: new DateTime(2024, 10, 9)),
        };

        Assert.Empty(MobilePayResolver.Resolve(rows));
    }

    [Fact]
    public void Empty_agreement_id_is_ignored()
    {
        var rows = new[] { Row("111111", contract: "10000001", mainRef: null, agreement: "   ") };

        Assert.Empty(MobilePayResolver.Resolve(rows));
    }

    // ── Regression guards ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Agreement_on_ended_main_contract_is_still_found_via_active_sub_contract()
    {
        // Acct 474066: the main contract carrying the agreement has ended, but an active
        // sub-contract still hangs under it. This customer was migrated with MobilePay before
        // the cross-contract scan existed and must not lose it.
        var rows = new[]
        {
            Row("474066", contract: "10528999", mainRef: "10528354", agreement: null,
                mainContract: "10528354", mainAgreement: "agr_ks8Jx7k"),
        };

        var pick = MobilePayResolver.Resolve(rows)["474066"];

        Assert.Equal("agr_ks8Jx7k", pick.Winner.AgreementId);
        Assert.Equal("10528354", pick.Winner.ContractNumber);
        Assert.Equal(MobilePayOrigin.MainContract, pick.Winner.Origin);
    }

    [Fact]
    public void Same_main_contract_reached_from_many_sub_contracts_counts_once()
    {
        var rows = new[]
        {
            Row("222222", contract: "10000010", mainRef: "10000001", agreement: null,
                mainContract: "10000001", mainAgreement: "agr_one"),
            Row("222222", contract: "10000011", mainRef: "10000001", agreement: null,
                mainContract: "10000001", mainAgreement: "agr_one"),
            Row("222222", contract: "10000001", mainRef: null, agreement: "agr_one"),
        };

        var pick = MobilePayResolver.Resolve(rows)["222222"];

        Assert.Equal(1, pick.CandidateCount);
        Assert.Equal("10000001", pick.Winner.ContractNumber);
    }

    [Fact]
    public void Accounts_without_any_agreement_are_absent_from_the_result()
    {
        var rows = new[]
        {
            Row("333333", contract: "10000020", mainRef: null, agreement: null),
            Row("444444", contract: "10000021", mainRef: null, agreement: "agr_x"),
        };

        var result = MobilePayResolver.Resolve(rows);

        Assert.False(result.ContainsKey("333333"));
        Assert.True(result.ContainsKey("444444"));
    }

    [Fact]
    public void Account_and_contract_numbers_are_trimmed()
    {
        var rows = new[] { Row("  555555 ", contract: " 10000030 ", mainRef: null, agreement: " agr_y ") };

        var pick = MobilePayResolver.Resolve(rows)["555555"];

        Assert.Equal("agr_y", pick.Winner.AgreementId);
        Assert.Equal("10000030", pick.Winner.ContractNumber);
    }

    // ── Helper ────────────────────────────────────────────────────────────────────────────────

    private static MobilePayScanRow Row(
        string account,
        string contract,
        string? mainRef,
        string? agreement,
        DateTime? cancelDate = null,
        string? mainContract = null,
        string? mainAgreement = null,
        DateTime? mainCancelDate = null)
        => new(
            Account: account,
            OwnContract: contract,
            OwnReference: mainRef,
            OwnAgreement: agreement,
            OwnCancelDate: cancelDate ?? NotCancelled,
            // The query's self-join resolves to the contract itself when it has no main reference.
            MainContract: mainContract ?? mainRef ?? contract,
            MainAgreement: mainAgreement,
            MainCancelDate: mainCancelDate ?? NotCancelled);
}
