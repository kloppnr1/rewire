using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// The charger↔metering-point tie. A charger contract holds no reference back to the electricity it
/// charges from, so the link is made through the charger products on the electricity side. When the
/// account offers more than one candidate, the installation's postal code picks among them — and
/// the charger is left on the account, loudly, when that does not settle it.
/// </summary>
public class EvChargerLookupTests
{
    private static readonly DateTime Open = MigrationConstants.EndDateNull;
    private static readonly DateOnly Reference = new(2026, 1, 1);
    private const string Mp1 = "571313100000000001";
    private const string Mp2 = "571313100000000002";

    private static EvChargerElectricityLinkRow Link(
        string account, string mpId, string product = "V LadVariP", string? zip = null, EvChargerDelpointAddress? address = null)
        => new(account, mpId, product, zip, address);

    private static EvChargerPartRow Part(
        string account,
        string category,
        string contract = "C001",
        string installation = "I001",
        string product = "V Lader",
        string? address = "Fiktivvejewj 1",
        DateTime? start = null,
        DateTime? end = null,
        string? zip = null,
        string? darId = null)
        => new(account, contract, installation, address, zip, category, product,
            start ?? new DateTime(2025, 1, 1), end ?? Open, darId);

    [Fact]
    public void One_candidate_metering_point_links_the_charger()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge)],
            Reference,
            new WarningCollector());

        var charger = Assert.Single(byMp[Mp1]);
        Assert.Equal("I001", charger.InstallationNumber);
        Assert.Equal("A1", charger.CustomerAccount);
        Assert.Equal("C001", charger.ContractNumber);
        Assert.Equal("Fiktivvejewj 1", charger.Address);
        Assert.False(charger.AddressMatchesMeteringPoint);   // no delpoint address to compare with
        Assert.Empty(byAccount);
    }

    [Fact]
    public void Same_metering_point_reached_by_two_charger_products_is_still_one_candidate()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, "V LadVariP"), Link("A1", Mp1, "V LadVarUA")],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge)],
            Reference,
            new WarningCollector());

        Assert.Single(byMp[Mp1]);
        Assert.Empty(byAccount);
    }

    [Fact]
    public void Two_candidate_metering_points_leave_the_charger_on_the_account_and_warn()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1), Link("A1", Mp2)],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge)],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.Single(byAccount["A1"]);

        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("A1", warning);
        Assert.Contains(Mp1, warning);
        Assert.Contains(Mp2, warning);
    }

    [Fact]
    public void Two_candidates_with_distinct_postal_codes_send_each_installation_to_its_own_metering_point()
    {
        var warnings = new WarningCollector();

        // Account 105909 in production: two charger MPs, two Charge_Abo installations.
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, "V LadVariP", zip: "8900"), Link("A1", Mp2, "V LadVarUA", zip: "8960")],
            [
                Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription, contract: "C001", installation: "I001", zip: "8960"),
                Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription, contract: "C002", installation: "I002", zip: "8900")
            ],
            Reference,
            warnings);

        Assert.Equal("I002", Assert.Single(byMp[Mp1]).InstallationNumber);
        Assert.Equal("I001", Assert.Single(byMp[Mp2]).InstallationNumber);
        Assert.Empty(byAccount);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void Postal_code_tie_break_compares_trimmed_values()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8900 "), Link("A1", Mp2, zip: "8960")],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: " 8900")],
            Reference,
            new WarningCollector());

        Assert.Single(byMp[Mp1]);
        Assert.Empty(byAccount);
    }

    [Fact]
    public void Two_candidates_sharing_the_installations_postal_code_leave_the_charger_on_the_account_and_warn()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8900"), Link("A1", Mp2, zip: "8900")],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8900")],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.Single(byAccount["A1"]);

        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains(Mp1, warning);
        Assert.Contains(Mp2, warning);
        Assert.Contains("postal-code tie-break", warning);
        Assert.Contains("I001 (postal code 8900) matches 2", warning);
    }

    [Fact]
    public void Two_candidates_and_no_matching_postal_code_leave_the_charger_on_the_account_and_warn()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8900"), Link("A1", Mp2, zip: "8960")],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8000")],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.Single(byAccount["A1"]);
        Assert.Contains("I001 (postal code 8000) matches 0", Assert.Single(warnings.GetWarnings()));
    }

    [Fact]
    public void Two_candidates_and_a_blank_installation_postal_code_leave_the_charger_on_the_account_and_warn()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8900"), Link("A1", Mp2, zip: "8960")],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "  ")],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.Single(byAccount["A1"]);
        Assert.Contains("I001 has no postal code", Assert.Single(warnings.GetWarnings()));
    }

    [Fact]
    public void Two_candidates_resolve_one_installation_and_leave_the_other_on_the_account()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8900"), Link("A1", Mp2, zip: "8960")],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C001", installation: "I001", zip: "8900"),
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C002", installation: "I002", zip: "8000")
            ],
            Reference,
            warnings);

        Assert.Equal("I001", Assert.Single(byMp[Mp1]).InstallationNumber);
        Assert.False(byMp.ContainsKey(Mp2));
        Assert.Equal("I002", Assert.Single(byAccount["A1"]).InstallationNumber);

        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("1 charger installation(s) left unlinked", warning);
        Assert.Contains("I002", warning);
    }

    [Fact]
    public void Single_candidate_links_the_charger_even_when_postal_codes_differ()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8900", address: At("Fiktivvejm", 83))],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8000", address: "Fiktivgadep  1, 8000 Aarhus C")],
            Reference,
            warnings);

        Assert.False(Assert.Single(byMp[Mp1]).AddressMatchesMeteringPoint);
        Assert.Empty(byAccount);
        Assert.Empty(warnings.GetWarnings());
    }

    // ── Address tie-break and the address flag ────────────────────────────────────────────────
    //
    // Production 2026-09-01: four accounts (475575, 491068, 520309, 484622) with two charger MPs and
    // two installations in one postal code, where only the street tells them apart — and 491704, one
    // MP with two installations at its own address, both of which belong to it.

    private const string DarA = "31876bdc-5a0b-4584-9091-701e5bde6153";
    private const string DarB = "649762c9-dcf0-4759-a175-8756d98a92b1";
    private const string DarZero = "00000000-0000-0000-0000-000000000000";

    private static EvChargerDelpointAddress At(string? street, int? number, string? letter = "", string? dar = "")
        => new(dar, street, number, letter);

    [Fact]
    public void Two_candidates_in_one_postal_code_are_told_apart_by_street()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8960", address: At("Fiktivvejm", 83)),
                Link("A1", Mp2, zip: "8960", address: At("Fiktivvejek", 23))
            ],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C001", installation: "I001", zip: "8960", address: "Fiktivvejek  23, 8960 Randers SØ"),
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C002", installation: "I002", zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ")
            ],
            Reference,
            warnings);

        var onMp1 = Assert.Single(byMp[Mp1]);
        var onMp2 = Assert.Single(byMp[Mp2]);
        Assert.Equal("I002", onMp1.InstallationNumber);
        Assert.Equal("I001", onMp2.InstallationNumber);
        Assert.True(onMp1.AddressMatchesMeteringPoint);
        Assert.True(onMp2.AddressMatchesMeteringPoint);
        Assert.Empty(byAccount);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void Two_candidates_on_one_street_are_told_apart_by_house_number()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8960", address: At("Fiktivvejm", 83)),
                Link("A1", Mp2, zip: "8960", address: At("Fiktivvejm", 85))
            ],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C001", installation: "I001", zip: "8960", address: "Fiktivvejm  85, 8960 Randers SØ"),
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C002", installation: "I002", zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ")
            ],
            Reference,
            warnings);

        Assert.Equal("I002", Assert.Single(byMp[Mp1]).InstallationNumber);
        Assert.Equal("I001", Assert.Single(byMp[Mp2]).InstallationNumber);
        Assert.Empty(byAccount);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void Two_candidates_at_the_same_address_leave_the_charger_on_the_account_and_warn_without_printing_it()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8960", address: At("Fiktivvejm", 83)),
                Link("A1", Mp2, zip: "8960", address: At("Fiktivvejm", 83))
            ],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ")],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.False(Assert.Single(byAccount["A1"]).AddressMatchesMeteringPoint);

        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("postal-code tie-break", warning);
        Assert.Contains("I001 (postal code 8960) matches 2, address matches 2/2", warning);
        Assert.DoesNotContain("Fiktivvejm", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_candidates_with_no_street_on_record_leave_the_charger_on_the_account_and_warn()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8960", address: At(null, null)),
                Link("A1", Mp2, zip: "8960", address: At("", 0))
            ],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ")],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.Single(byAccount["A1"]);

        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("I001 (postal code 8960) matches 2, address matches 0/2", warning);
        Assert.DoesNotContain("Fiktivvejm", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_dar_id_match_wins_over_differing_street_text()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8960", address: At("Fiktivgadep", 1, dar: DarA)),
                Link("A1", Mp2, zip: "8960", address: At("Fiktivvejm", 83, dar: DarB))
            ],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ", darId: DarA)],
            Reference,
            new WarningCollector());

        Assert.True(Assert.Single(byMp[Mp1]).AddressMatchesMeteringPoint);
        Assert.False(byMp.ContainsKey(Mp2));
        Assert.Empty(byAccount);
    }

    [Fact]
    public void An_all_zero_dar_id_is_ignored_and_the_street_decides()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8960", address: At("Fiktivvejm", 83, dar: DarZero)),
                Link("A1", Mp2, zip: "8960", address: At("Fiktivgadep", 1, dar: DarB))
            ],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ", darId: DarZero)],
            Reference,
            new WarningCollector());

        Assert.True(Assert.Single(byMp[Mp1]).AddressMatchesMeteringPoint);
        Assert.Empty(byAccount);
    }

    [Fact]
    public void One_candidate_with_two_installations_at_its_address_links_both_with_the_flag_set()
    {
        // Account 491704 in production: one charger MP, a Charge and a Charge_Abo installation, both
        // at the metering point's own address.
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1, zip: "8960", address: At("Fiktivvejm", 83))],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C001", installation: "I001", zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ"),
                Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription, contract: "C002", installation: "I002", zip: "8960", address: "Fiktivvejm  83, 8960 Randers SØ")
            ],
            Reference,
            new WarningCollector());

        Assert.Equal(["I001", "I002"], byMp[Mp1].Select(c => c.InstallationNumber));
        Assert.All(byMp[Mp1], c => Assert.True(c.AddressMatchesMeteringPoint));
        Assert.Empty(byAccount);
    }

    [Fact]
    public void A_postal_code_settled_tie_still_reports_whether_the_address_matched()
    {
        var (byMp, _) = XellentDomainMapper.BuildEvChargerLookups(
            [
                Link("A1", Mp1, zip: "8900", address: At("Fiktivvejm", 83)),
                Link("A1", Mp2, zip: "8960", address: At("Fiktivgadep", 1))
            ],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C001", installation: "I001", zip: "8900", address: "Fiktivvejm  83, 8900 Randers C"),
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C002", installation: "I002", zip: "8960", address: "Fiktivgadeq  5, 8960 Randers SØ")
            ],
            Reference,
            new WarningCollector());

        Assert.True(Assert.Single(byMp[Mp1]).AddressMatchesMeteringPoint);
        Assert.False(Assert.Single(byMp[Mp2]).AddressMatchesMeteringPoint);
    }

    [Fact]
    public void No_charger_electricity_product_leaves_the_charger_on_the_account_without_a_warning()
    {
        var warnings = new WarningCollector();

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge)],
            Reference,
            warnings);

        Assert.Empty(byMp);
        Assert.Single(byAccount["A1"]);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void Consumption_and_subscription_stay_apart_on_the_same_installation()
    {
        var (byMp, _) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, product: "V LadForb"),
                Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription, product: "V LadAbo")
            ],
            Reference,
            new WarningCollector());

        var charger = Assert.Single(byMp[Mp1]);
        Assert.Equal(2, charger.Parts.Count);
        Assert.True(charger.HasConsumption);
        Assert.True(charger.HasSubscription);

        var subscription = Assert.Single(charger.Parts, p => p.Kind == EvChargerPartKind.Subscription);
        Assert.Equal("Charge_Abo", subscription.DeliveryCategory);
        Assert.Equal("V LadAbo", subscription.ProductCode);

        var consumption = Assert.Single(charger.Parts, p => p.Kind == EvChargerPartKind.Consumption);
        Assert.Equal("Charge", consumption.DeliveryCategory);
    }

    [Fact]
    public void Subscription_only_installation_reports_no_consumption()
    {
        var (byMp, _) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription)],
            Reference,
            new WarningCollector());

        var charger = Assert.Single(byMp[Mp1]);
        Assert.False(charger.HasConsumption);
        Assert.True(charger.HasSubscription);
    }

    [Fact]
    public void Open_ended_part_reports_no_end_date_and_a_real_end_date_survives()
    {
        var (byMp, _) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, product: "V LadForb"),
                Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription, product: "V LadAbo",
                    start: new DateTime(2026, 2, 1), end: new DateTime(2026, 12, 31))
            ],
            Reference,
            new WarningCollector());

        var parts = Assert.Single(byMp[Mp1]).Parts;
        Assert.Null(parts[0].EndDate);                                  // ordered by start date
        Assert.Equal(new DateOnly(2025, 1, 1), parts[0].StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), parts[1].EndDate);
    }

    [Fact]
    public void A_part_that_ended_before_the_reference_date_is_left_out()
    {
        var (byMp, _) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, product: "V LadForb"),
                Part("A1", MigrationConstants.DeliveryCategoryChargeSubscription, product: "V LadAbo",
                    start: new DateTime(2024, 1, 1), end: new DateTime(2025, 6, 30))
            ],
            Reference,
            new WarningCollector());

        var charger = Assert.Single(byMp[Mp1]);
        Assert.Equal("V LadForb", Assert.Single(charger.Parts).ProductCode);
        Assert.False(charger.HasSubscription);
    }

    [Fact]
    public void An_installation_whose_only_part_has_ended_disappears_entirely()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [Part("A1", MigrationConstants.DeliveryCategoryCharge, end: new DateTime(2025, 6, 30))],
            Reference,
            new WarningCollector());

        Assert.Empty(byMp);
        Assert.Empty(byAccount);
    }

    [Fact]
    public void Two_installations_on_one_account_both_land_on_its_metering_point()
    {
        var (byMp, _) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C001", installation: "I001"),
                Part("A1", MigrationConstants.DeliveryCategoryCharge, contract: "C002", installation: "I002")
            ],
            Reference,
            new WarningCollector());

        Assert.Equal(["I001", "I002"], byMp[Mp1].Select(c => c.InstallationNumber));
    }

    [Fact]
    public void Chargers_from_different_accounts_do_not_borrow_each_others_metering_point()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [
                Part("A1", MigrationConstants.DeliveryCategoryCharge, installation: "I001"),
                Part("A2", MigrationConstants.DeliveryCategoryCharge, installation: "I002")
            ],
            Reference,
            new WarningCollector());

        Assert.Equal("I001", Assert.Single(byMp[Mp1]).InstallationNumber);
        Assert.Equal("I002", Assert.Single(byAccount["A2"]).InstallationNumber);
        Assert.False(byAccount.ContainsKey("A1"));
    }

    [Fact]
    public void An_account_with_a_metering_point_but_no_charger_produces_nothing()
    {
        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [],
            Reference,
            new WarningCollector());

        Assert.Empty(byMp);
        Assert.Empty(byAccount);
    }

    [Fact]
    public void A_delivery_category_that_is_not_a_charger_category_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => XellentDomainMapper.BuildEvChargerLookups(
            [Link("A1", Mp1)],
            [Part("A1", MigrationConstants.DeliveryCategoryWater)],
            Reference,
            new WarningCollector()));
    }

    // ── The electricity side under the contract-part restore ──────────────────────────────────
    //
    // The 2026-08-31 batch end-dated live El-ekstern parts and deleted future ones. The link rows
    // must be read as the restore sees them, or the contract mapper migrates a charger-product part
    // the charger lookup never saw — the charger stays on the account and the raw product name
    // reaches Settl.

    /// <summary>What the batch wrote into ENDDATE: the day before the reference date.</summary>
    private static readonly DateTime BatchEndDate = new(2025, 12, 31);

    private static ContractPartLogKey LinkKey(string agreement = "A001", string product = "V LadVariP", DateTime? start = null)
        => new("C001", agreement, MigrationConstants.DeliveryCategoryElectricity, "01", product, "", start ?? new DateTime(2020, 1, 1));

    private static EvChargerLinkPartRow LinkPart(string account, string mpId, ContractPartLogKey key, DateTime end, string? zip = null)
        => new(account, mpId, zip, key, end);

    private static ContractPartRestore RestoreWith(params (ContractPartLogKey Key, DateTime EndDate)[] endDates)
        => ContractPartRestore.FromPlan(
            new ContractPartRestorePlan(endDates.ToDictionary(e => e.Key, e => e.EndDate), []),
            new Dictionary<(string, string, string), IReadOnlyList<string>>(),
            new WarningCollector());

    [Fact]
    public void A_charger_product_part_the_batch_deleted_links_the_charger_once_restored()
    {
        // Account 105332 in production: the only charger-product part is a future one the batch
        // deleted. Resolved by the caller, it comes in as a deleted row — open-ended, start in future.
        var deleted = LinkPart("A1", Mp1, LinkKey(start: new DateTime(2026, 11, 1)), Open);

        var linkRows = XellentDomainMapper.RestoreEvChargerLinkRows(
            liveRows: [], deletedRows: [deleted], ContractPartRestore.Empty, Reference);

        Assert.Equal([Link("A1", Mp1)], linkRows);

        var (byMp, byAccount) = XellentDomainMapper.BuildEvChargerLookups(
            linkRows, [Part("A1", MigrationConstants.DeliveryCategoryCharge)], Reference, new WarningCollector());

        Assert.Equal("I001", Assert.Single(byMp[Mp1]).InstallationNumber);
        Assert.Empty(byAccount);
    }

    [Fact]
    public void A_charger_product_part_the_batch_end_dated_links_again_with_its_open_end_date_restored()
    {
        var key = LinkKey();
        var live = LinkPart("A1", Mp1, key, BatchEndDate, zip: "8000");

        var linkRows = XellentDomainMapper.RestoreEvChargerLinkRows(
            [live], [], RestoreWith((key, Open)), Reference);

        Assert.Equal([Link("A1", Mp1, zip: "8000")], linkRows);
    }

    [Fact]
    public void A_restored_end_date_that_is_still_in_the_past_drops_the_link_again()
    {
        // The batch overwrote an end date that had already passed: putting it back must not revive it.
        var key = LinkKey();
        var live = LinkPart("A1", Mp1, key, BatchEndDate);

        var linkRows = XellentDomainMapper.RestoreEvChargerLinkRows(
            [live], [], RestoreWith((key, new DateTime(2025, 6, 30))), Reference);

        Assert.Empty(linkRows);
    }

    [Fact]
    public void An_empty_restore_leaves_the_ordinary_rule_in_charge()
    {
        // Same outcome as the un-restored query: open or still running stays, ended goes. The
        // widened SQL cutoff is what lets the ended row reach memory; this is the rule that removes it.
        var open = LinkPart("A1", Mp1, LinkKey("A001"), Open);
        var future = LinkPart("A1", Mp1, LinkKey("A001", "V LadVarUA"), MigrationConstants.EndDateFuture);
        var ended = LinkPart("A1", Mp2, LinkKey("A002"), BatchEndDate);

        var linkRows = XellentDomainMapper.RestoreEvChargerLinkRows(
            [open, future, ended], [], ContractPartRestore.Empty, Reference);

        Assert.Equal([Link("A1", Mp1, "V LadVariP"), Link("A1", Mp1, "V LadVarUA")], linkRows);
    }

    [Fact]
    public void The_same_link_reached_twice_is_still_one_row()
    {
        var key = LinkKey();

        var linkRows = XellentDomainMapper.RestoreEvChargerLinkRows(
            [LinkPart("A1", Mp1, key, Open)], [LinkPart("A1", Mp1, key, Open)], ContractPartRestore.Empty, Reference);

        Assert.Single(linkRows);
    }
}
