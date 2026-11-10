using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;

namespace DataMigration.Migration.Tests;

/// <summary>
/// A charger customer's electricity metering point resolves to a Settl contract that depends on
/// BOTH the electricity product and the charger's own product — not just its own product, which is
/// what every other rule looks at. These tests pin the business-confirmed combinations, the
/// fallthrough cases (no charger, ambiguous charger, consumption-only charger, unmapped pair,
/// still-unconfirmed or deliberately-excluded products), and that the rule sits ahead of the
/// template+product rule but behind an explicit account override.
/// </summary>
public class ElectricityChargerContractMappingTests
{
    private const string Account = "101550";
    private const string MeteringPoint = "571313179104952730";
    private const string Contract = "10263929";

    private static readonly Dictionary<string, Dictionary<string, string>> ConfirmedMap = new()
    {
        ["V LadVariP"] = new()
        {
            ["OAboEjeMEL"] = "LadeAbo_Eje_Variabel",
            ["OAboLejMEL"] = "LadeAbo_Leje_Variabel",
            ["OAboEje0"] = "LadeAbo_Eje0kr_Variabel",
            [ElectricityChargerContractRules.NoChargeKey] = "LadeAbo_Afventer_Installation",
            // Owned + Variabel only — Settl has no UA variant, so V LadVarUA deliberately has none.
            [ElectricityChargerContractRules.MultipleInstallationsKey] = "LadeAbo_Eje_2inst_1mp_Variabel"
        },
        ["V LadVarUA"] = new()
        {
            ["OAboEjeMEL"] = "LadeAbo_Eje_VariabelUA",
            ["OAboLejMEL"] = "LadeAbo_Leje_VariabelUA",
            ["OAboEje0"] = "LadeAbo_Eje0kr_VariabelUA",
            [ElectricityChargerContractRules.NoChargeKey] = "LadeAbo_Afventer_Installation"
        }
    };

    [Theory]
    [InlineData("V LadVariP", "O HCEjeUL3")]
    [InlineData("V LadVariP", "O HCEjeUL4")]
    [InlineData("V LadVariP", "O HCEjeUL5")]
    [InlineData("V LadVarUA", "O HCEjeUL3")]
    [InlineData("V LadVarUA", "O HCEjeUL4")]
    [InlineData("V LadVarUA", "O HCEjeUL5")]
    public void Confirmed_homecharge_products_resolve_for_ordinary_and_ua_periods(string electricity, string charger)
    {
        var rules = ElectricityChargerContractRules.Build(ConfirmedMap)!;
        Assert.Equal("HomeChargeLadeEjeMel(O HCEjeMEL)", rules.Resolve(electricity, charger));
        Assert.Contains(charger, ElectricityChargerContractRules.ConfirmedChargeAboProducts);
    }

    [Theory]
    [InlineData("V Kvt UA", "V Kvartal UA")]
    [InlineData("V Variabel", "V Variabel UA")]
    [InlineData("V Kvartal", "V Kvartal UA")]
    public void Discount_part_resolves_to_ua_contract_from_companion(string companion, string expected)
    {
        var result = ResolveDiscount(companion);
        Assert.Contains(result.Links, l => l.ProductNum == "V AboRabat" && l.ContractName == expected);
        Assert.DoesNotContain(result.Links, l => l.ContractName == "V AboRabat");
    }

    [Fact]
    public void Discount_part_with_matching_charger_resolves_to_charger_ua()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>
        {
            [MeteringPoint] = [Charger("OAboEje0") with { AddressMatchesMeteringPoint = true }]
        };
        var result = ResolveDiscount("V LadVariP", chargers);
        Assert.Contains(result.Links, l => l.ProductNum == "V AboRabat"
            && l.ContractName == "LadeAbo_Eje0kr_VariabelUA");
    }

    [Fact]
    public void Discount_part_without_unambiguous_companion_stops_create()
    {
        Assert.Throws<InvalidOperationException>(() => ResolveDiscount("V LadVariP"));
    }

    private static ContractNameResolver.Result ResolveDiscount(string companion,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? chargers = null)
    {
        var day = new DateTime(2026, 8, 31);
        var options = new CreateOptions
        {
            Accounts = [Account], DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = day, ElectricityChargerContractMap = ConfirmedMap
        };
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate,
            DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined,
            string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] =
            [
                (MeteringPoint, day, day, day, false, Contract, companion, "", "Lbm+1d"),
                (MeteringPoint, day, day, day, false, Contract, "V AboRabat", "", "Lbm+1d")
            ]
        };
        return new ContractNameResolver(new Fixtures.MockContractClient(), new WarningCollector(), _ => { })
            .ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);
    }

    [Theory]
    [InlineData("V LadVariP", "OAboEjeMEL", "LadeAbo_Eje_Variabel")]
    [InlineData("V LadVarUA", "OAboEjeMEL", "LadeAbo_Eje_VariabelUA")]
    [InlineData("V LadVariP", "OAboLejMEL", "LadeAbo_Leje_Variabel")]
    [InlineData("V LadVarUA", "OAboLejMEL", "LadeAbo_Leje_VariabelUA")]
    [InlineData("V LadVariP", "OAboEje0", "LadeAbo_Eje0kr_Variabel")]
    [InlineData("V LadVarUA", "OAboEje0", "LadeAbo_Eje0kr_VariabelUA")]
    public void Charger_with_a_live_subscription_product_resolves_the_confirmed_contract(
        string elProduct, string chargeAboProduct, string expectedContract)
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger(chargeAboProduct)]
        };

        var result = Resolve(elProduct, evChargersByMeteringPoint: chargers);

        Assert.Equal(expectedContract, result.MpContractNames[MeteringPoint]);
        var rule = Assert.Single(result.MpChargerContractRules).Value.Single();
        Assert.Equal(elProduct, rule.ElProductNum);
        Assert.Equal(chargeAboProduct, rule.ChargeAboProductNum);
        Assert.Equal(expectedContract, rule.ContractName);
    }

    // ── paused scope: still-unconfirmed and deliberately-excluded Charge_Abo products fall
    // through regardless of what appsettings.json maps them to. A Charge_Abo or El-ekstern product
    // changing over time is not paused any more — each contract-part tuple resolves against the
    // charger's state as of that tuple's own effective date. ──

    [Theory]
    [InlineData("OAbLejBero")] // low-volume code, not yet investigated (docs/charge-account-classification.md)
    [InlineData("OAboEjeUEL")] // confirmed to have NO Settl contract at all — deliberately excluded, not "pending"
    public void Charge_Abo_product_not_confirmed_falls_through_even_when_configured(string chargeAboProduct)
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger(chargeAboProduct)]
        };

        var result = Resolve("V LadVariP", evChargersByMeteringPoint: chargers);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Account_with_no_live_charge_part_at_all_resolves_to_awaiting_installation()
    {
        // __NoCharge__ is confirmed (LadeAbo_Afventer_Installation) — an el-ekstern MP pointing at a
        // charger with no live Charge/Charge_Abo part at all now resolves through the charger rule
        // rather than pausing.
        var result = Resolve("V LadVariP",
            evChargersByMeteringPoint: new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal));

        Assert.Equal("LadeAbo_Afventer_Installation", result.MpContractNames[MeteringPoint]);
        var rule = Assert.Single(result.MpChargerContractRules).Value.Single();
        Assert.Equal(ElectricityChargerContractRules.NoChargeKey, rule.ChargeAboProductNum);
    }

    [Fact]
    public void Two_owned_chargers_with_neither_address_confirmed_resolve_to_the_multi_installation_contract()
    {
        // Neither charger is address-confirmed (the flag defaults to false). Where the installations
        // are registered is not a condition of the multi-installation rule (user decision
        // 2026-09-01 on accounts 109909, 471352, 498288) — owned products alone decide.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                Charger("OAboEjeMEL"),
                new EvCharger("1086590", Account, "10562434", null,
                    [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 3, 19), null)])
            ]
        };

        var result = Resolve("V LadVariP", evChargersByMeteringPoint: chargers);

        Assert.Equal(MultiInstallationContract, result.MpContractNames[MeteringPoint]);
        var rule = Assert.Single(result.MpChargerContractRules).Value.Single();
        Assert.Equal(ElectricityChargerContractRules.MultipleInstallationsKey, rule.ChargeAboProductNum);
    }

    // ── several charger installations on ONE metering point (user decisions 2026-09-01, accounts
    // 491704, 109909, 471352, 498288): one household, several charging boxes. When every live
    // Charge_Abo product is owned, the MP gets the one multi-installation contract for the whole
    // tuple — wherever the installations are registered. Anything else stays paused, with a reason
    // that says which test failed. ──

    private const string MultiInstallationContract = "LadeAbo_Eje_2inst_1mp_Variabel";

    [Fact]
    public void Two_address_confirmed_owned_chargers_resolve_to_the_one_multi_installation_contract()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                ConfirmedCharger("1090220", "OAboEje0"),
                ConfirmedCharger("1090295", "OAboEjeMEL"),
            ]
        };
        var log = new List<string>();

        var result = ResolveAccount([(MeteringPoint, "V LadVariP")], evChargersByMeteringPoint: chargers, log: log);

        var link = Assert.Single(result.Links);
        Assert.Equal(MultiInstallationContract, link.ContractName);
        Assert.Equal(new DateTime(2026, 9, 1), link.ContractPartStartdate);
        Assert.Equal(OpenEnded, link.ContractPartEnddate);
        Assert.Equal(MultiInstallationContract, result.MpContractNames[MeteringPoint]);
        var rule = Assert.Single(result.MpChargerContractRules[MeteringPoint]);
        Assert.Equal("V LadVariP", rule.ElProductNum);
        Assert.Equal(ElectricityChargerContractRules.MultipleInstallationsKey, rule.ChargeAboProductNum);
        Assert.Equal(MultiInstallationContract, rule.ContractName);
        Assert.Contains(log, line => line.Contains($"1x  V LadVariP + __MultipleInstallations__ → {MultiInstallationContract}", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("paused for now", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("Charger contracts skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Three_address_confirmed_owned_chargers_resolve_to_the_same_single_contract()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                ConfirmedCharger("1090220", "OAboEje0"),
                ConfirmedCharger("1090295", "OAboEjeMEL"),
                ConfirmedCharger("1090296", "OAboEjeMEL"),
            ]
        };

        var result = ResolveAccount([(MeteringPoint, "V LadVariP")], evChargersByMeteringPoint: chargers);

        var link = Assert.Single(result.Links);
        Assert.Equal(MultiInstallationContract, link.ContractName);
        Assert.Single(result.MpChargerContractRules[MeteringPoint]);
    }

    [Fact]
    public void Multi_installation_contract_is_not_split_by_a_future_charger_change()
    {
        // One of the chargers converts OAboEje0 -> OAboEjeMEL on 2026-12-01 (both owned). A single
        // charger would get its link capped and a second one appended; the multi-installation
        // contract covers the whole tuple and must stay one open-ended link.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                new EvCharger("1090220", Account, "10562434", null,
                    [
                        new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEje0",
                            new DateOnly(2026, 6, 1), new DateOnly(2026, 11, 30)),
                        new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                            new DateOnly(2026, 12, 1), null),
                    ],
                    AddressMatchesMeteringPoint: true),
                ConfirmedCharger("1090295", "OAboEjeMEL"),
            ]
        };

        var result = ResolveSingleTuple("V LadVariP", chargers);

        var link = Assert.Single(result.Links);
        Assert.Equal(MultiInstallationContract, link.ContractName);
        Assert.Equal(OpenEnded, link.ContractPartEnddate);
        Assert.Single(result.MpChargerContractRules[MeteringPoint]);
    }

    [Fact]
    public void Several_owned_chargers_with_one_not_address_confirmed_still_get_the_multi_installation_contract()
    {
        // One installation registered elsewhere (user decision 2026-09-01 on accounts 109909,
        // 471352, 498288): the address is not a condition — owned products are.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                ConfirmedCharger("1090220", "OAboEje0"),
                new EvCharger("1090295", Account, "10562435", null,
                    [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 3, 19), null)],
                    AddressMatchesMeteringPoint: false),
            ]
        };
        var log = new List<string>();

        var result = ResolveAccount([(MeteringPoint, "V LadVariP")], evChargersByMeteringPoint: chargers, log: log);

        var link = Assert.Single(result.Links);
        Assert.Equal(MultiInstallationContract, link.ContractName);
        Assert.Equal(MultiInstallationContract, result.MpContractNames[MeteringPoint]);
        var rule = Assert.Single(result.MpChargerContractRules[MeteringPoint]);
        Assert.Equal(ElectricityChargerContractRules.MultipleInstallationsKey, rule.ChargeAboProductNum);
        Assert.DoesNotContain(log, line => line.Contains("paused for now", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_chargers_with_a_rented_product_in_the_mix_stay_paused_and_name_the_products()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                ConfirmedCharger("1090220", "OAboEje0"),
                ConfirmedCharger("1090295", "OAboLejMEL"),
            ]
        };
        var log = new List<string>();

        var result = ResolveAccount([(MeteringPoint, "V LadVariP")], evChargersByMeteringPoint: chargers, log: log);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
        Assert.Contains(log, line => line.Contains(
            "1x  several EV chargers on metering point, product mix has no multi-installation contract: OAboEje0, OAboLejMEL (paused for now)",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Several_owned_chargers_on_the_no_fee_product_stay_paused_because_settl_has_no_UA_variant()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] =
            [
                ConfirmedCharger("1090220", "OAboEje0"),
                ConfirmedCharger("1090295", "OAboEjeMEL"),
            ]
        };
        var log = new List<string>();

        var result = ResolveAccount([(MeteringPoint, "V LadVarUA")], evChargersByMeteringPoint: chargers, log: log);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVarUA", result.MpContractNames[MeteringPoint]);
        Assert.Contains(log, line => line.Contains(
            "1x  several EV chargers on metering point, no multi-installation contract configured for 'V LadVarUA' (paused for now)",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Multiple_installations_key_passes_the_confirmed_products_gate()
    {
        Assert.Contains(ElectricityChargerContractRules.MultipleInstallationsKey, ElectricityChargerContractRules.ConfirmedChargeAboProducts);
        Assert.Contains(ElectricityChargerContractRules.NoChargeKey, ElectricityChargerContractRules.ConfirmedChargeAboProducts);
    }

    /// <summary>An address-confirmed charger with one open-ended live Charge_Abo product.</summary>
    private static EvCharger ConfirmedCharger(string installation, string chargeAboProduct) =>
        new(installation, Account, "1056" + installation, null,
            [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, chargeAboProduct, new DateOnly(2025, 3, 19), null)],
            AddressMatchesMeteringPoint: true);

    [Fact]
    public void Charge_Abo_product_that_changed_over_its_history_resolves_the_currently_live_product()
    {
        // The charger's PAST product was OAboEje0; it's OAboEjeMEL now. The swap itself is no
        // longer a reason to pause — the tuple's own effective date (today, via ReferenceDate)
        // already lands on the confirmed OAboEjeMEL period, so it resolves normally.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEje0",
                        new DateOnly(2025, 1, 1), new DateOnly(2026, 4, 22)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2026, 4, 23), null),
                ])]
        };

        var result = Resolve("V LadVariP", evChargersByMeteringPoint: chargers);

        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        var rule = Assert.Single(result.MpChargerContractRules).Value.Single();
        Assert.Equal("OAboEjeMEL", rule.ChargeAboProductNum);
    }

    [Fact]
    public void A_staged_future_Charge_Abo_change_resolves_once_its_own_period_arrives()
    {
        // A routine ~6-month-out conversion already entered in Xellent (OAboEje0 -> OAboEjeMEL on
        // 2026-10-23). The el-ekstern side doesn't swap here, but Xellent still gives the MP two
        // contract-part tuples across that boundary — each one resolves against the charger's state
        // as of ITS OWN effective date: the first period sees OAboEje0 (its own confirmed contract),
        // the second sees OAboEjeMEL.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEje0",
                        new DateOnly(2026, 4, 23), new DateOnly(2026, 10, 22)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2026, 10, 23), null),
                ])]
        };

        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = ConfirmedMap,
        };
        var day1 = new DateTime(2026, 9, 1);
        var day2 = new DateTime(2026, 10, 23);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] =
            [
                (MeteringPoint, day1, day1, day2.AddDays(-1), false, Contract, "V LadVariP", "", "Lbm+1d"),
                (MeteringPoint, day2, day2, MigrationConstants.EndDateNull, false, Contract, "V LadVariP", "", "Lbm+1d"),
            ]
        };

        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), new WarningCollector(), _ => { });
        var result = resolver.ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);
        Assert.Equal("LadeAbo_Eje0kr_Variabel", mpLinks[0].ContractName);
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[1].ContractName);
    }

    // ── several El-ekstern metering points on one account: the charger rule is decided per MP
    // (business decision 2026-09-01, confirmed on account 105212). ──

    private const string SecondMp = "571313179104952731";
    private const string ThirdMp = "571313179104952732";

    [Fact]
    public void Charger_account_with_an_ordinary_metering_point_resolves_each_metering_point_on_its_own_rule()
    {
        // MP1 carries the charger and resolves through the charger table; MP2 is an ordinary
        // El-ekstern MP on the same account and takes the product rule. Nothing is paused.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("OAboEjeMEL")]
        };
        var log = new List<string>();

        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V Kvartal")],
            evChargersByMeteringPoint: chargers,
            productMap: new Dictionary<string, ProductNumContractEntry>
            {
                ["V Kvartal"] = ProductNumContractEntry.FromString("E_ErhKvt")
            },
            log: log);

        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        Assert.Equal("E_ErhKvt", result.MpContractNames[SecondMp]);
        var chargerRuleMp = Assert.Single(result.MpChargerContractRules);
        Assert.Equal(MeteringPoint, chargerRuleMp.Key);
        Assert.Equal(2, result.Links.Count);
        Assert.DoesNotContain(log, line => line.Contains("paused for now", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("Charger contracts skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordinary_metering_point_on_a_charger_account_never_gets_awaiting_installation()
    {
        // MP2 has no charger of its own, but the account HAS one (on MP1) — so MP2 is not "no
        // charge part at all" and must not fire the __NoCharge__ sentinel. With no product mapping
        // it lands on the raw product name, exactly like an ordinary MP. The charger's own staged
        // future change extends MP1 only; MP2 stays a single period.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 14)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                        new DateOnly(2026, 10, 15), null),
                ])]
        };

        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V LadVariP")],
            evChargersByMeteringPoint: chargers);

        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        Assert.Equal("V LadVariP", result.MpContractNames[SecondMp]);
        Assert.DoesNotContain(result.Links, l => l.MeteringPointId == SecondMp && l.ContractName == "LadeAbo_Afventer_Installation");
        Assert.Equal(2, result.Links.Count(l => l.MeteringPointId == MeteringPoint));
        Assert.Single(result.Links, l => l.MeteringPointId == SecondMp);
        Assert.False(result.MpChargerContractRules.ContainsKey(SecondMp));
    }

    [Fact]
    public void Chargers_unresolved_to_a_metering_point_fall_through_for_every_metering_point()
    {
        // The charger could not be tied to exactly one MP (lives in ByAccount). No MP on the
        // account resolves through the charger table, and none gets the sentinel either.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [Account] = [Charger("OAboEjeMEL")]
        };

        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V LadVariP")],
            evChargersByAccount: chargers);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
        Assert.Equal("V LadVariP", result.MpContractNames[SecondMp]);
    }

    [Fact]
    public void Several_charger_linked_metering_points_on_the_account_each_resolve_with_their_own_charger()
    {
        // Account 105909 (business decision 2026-09-01): two El-ekstern MPs, two charger
        // installations, each linked to its own MP by the postal-code tie-break. Each MP asks the
        // charger rule with its own charger and lands on its own charger-table contract. Nothing
        // is paused and no scope skip is counted.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("OAboEjeMEL")],
            [SecondMp] = [new EvCharger("1090295", Account, "10562434", null,
                [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                    new DateOnly(2025, 3, 19), null)])]
        };
        var log = new List<string>();

        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V LadVarUA")],
            evChargersByMeteringPoint: chargers,
            log: log);

        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        Assert.Equal("LadeAbo_Eje_VariabelUA", result.MpContractNames[SecondMp]);
        Assert.Equal(2, result.MpChargerContractRules.Count);
        Assert.Equal("OAboEjeMEL", Assert.Single(result.MpChargerContractRules[MeteringPoint]).ChargeAboProductNum);
        Assert.Equal("OAboEjeMEL", Assert.Single(result.MpChargerContractRules[SecondMp]).ChargeAboProductNum);
        Assert.Equal(2, result.Links.Count);
        Assert.Equal(["LadeAbo_Eje_Variabel", "LadeAbo_Eje_VariabelUA"],
            result.ContractNamesByAccount[Account].OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("paused for now", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("Charger contracts skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_charger_linked_metering_points_plus_an_ordinary_one_leave_the_ordinary_one_on_the_product_rule()
    {
        // Two charger MPs and one ordinary El-ekstern MP on the same account: the charger MPs
        // resolve through the charger table, the ordinary MP takes the product rule — never the
        // sentinel — and is never extended by either charger's staged future change.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 14)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                        new DateOnly(2026, 10, 15), null),
                ])],
            [SecondMp] = [new EvCharger("1090295", Account, "10562434", null,
                [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                    new DateOnly(2025, 3, 19), null)])]
        };
        var log = new List<string>();

        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V LadVariP"), (ThirdMp, "V Kvartal")],
            evChargersByMeteringPoint: chargers,
            productMap: new Dictionary<string, ProductNumContractEntry>
            {
                ["V Kvartal"] = ProductNumContractEntry.FromString("E_ErhKvt")
            },
            log: log);

        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        Assert.Equal("LadeAbo_Leje_Variabel", result.MpContractNames[SecondMp]);
        Assert.Equal("E_ErhKvt", result.MpContractNames[ThirdMp]);
        Assert.False(result.MpChargerContractRules.ContainsKey(ThirdMp));
        Assert.DoesNotContain(result.Links, l => l.MeteringPointId == ThirdMp && l.ContractName == "LadeAbo_Afventer_Installation");
        // MP1's own future change splits MP1 only; MP2 and MP3 stay a single period each.
        Assert.Equal(2, result.Links.Count(l => l.MeteringPointId == MeteringPoint));
        Assert.Single(result.Links, l => l.MeteringPointId == SecondMp);
        Assert.Single(result.Links, l => l.MeteringPointId == ThirdMp);
        Assert.DoesNotContain(log, line => line.Contains("paused for now", StringComparison.Ordinal));
    }

    [Fact]
    public void Metering_point_with_two_chargers_is_paused_alone_while_its_sibling_resolves()
    {
        // Two charger-linked MPs; one of them has two installations linked to it on the UA
        // product, for which Settl has no multi-installation contract. That MP alone is paused and
        // lands on its raw product name; the other resolves through the charger table as usual.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("OAboEjeMEL")],
            [SecondMp] =
            [
                new EvCharger("1090295", Account, "10562434", null,
                    [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 3, 19), null)]),
                new EvCharger("1090296", Account, "10562435", null,
                    [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 3, 19), null)])
            ]
        };
        var log = new List<string>();

        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V LadVarUA")],
            evChargersByMeteringPoint: chargers,
            log: log);

        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        Assert.Equal("V LadVarUA", result.MpContractNames[SecondMp]);
        var chargerRuleMp = Assert.Single(result.MpChargerContractRules);
        Assert.Equal(MeteringPoint, chargerRuleMp.Key);
        Assert.Contains(log, line => line.Contains(
            "1x  several EV chargers on metering point, no multi-installation contract configured for 'V LadVarUA' (paused for now)",
            StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("several charger-linked", StringComparison.Ordinal));
    }

    [Fact]
    public void Account_with_no_charger_data_resolves_each_charger_product_metering_point_to_awaiting_installation()
    {
        // No charger anywhere on the account (extraction ran, found nothing): every MP asks the
        // charger rule, so a charger-product MP gets the sentinel and an ordinary product falls
        // through untouched — the same per-MP answer a single-MP account already got.
        var result = ResolveAccount(
            [(MeteringPoint, "V LadVariP"), (SecondMp, "V Kvartal")],
            evChargersByMeteringPoint: new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal));

        Assert.Equal("LadeAbo_Afventer_Installation", result.MpContractNames[MeteringPoint]);
        Assert.Equal("V Kvartal", result.MpContractNames[SecondMp]);
        Assert.Single(result.MpChargerContractRules);
    }

    /// <summary>One account, several MPs, one contract-part tuple each (open-ended, starting
    /// 2026-09-01) -- for tests exercising the per-MP charger scope.</summary>
    private static ContractNameResolver.Result ResolveAccount(
        IReadOnlyList<(string MeteringPointId, string ElProduct)> meteringPoints,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint = null,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByAccount = null,
        Dictionary<string, ProductNumContractEntry>? productMap = null,
        List<string>? log = null)
    {
        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = ConfirmedMap,
            ProductNumContractMap = productMap,
        };
        var today = new DateTime(2026, 9, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] = meteringPoints
                .Select(mp => (mp.MeteringPointId, today, today, MigrationConstants.EndDateNull, false, Contract, mp.ElProduct, "", "Lbm+1d"))
                .ToList()
        };

        var resolver = new ContractNameResolver(
            new Fixtures.MockContractClient(), new WarningCollector(), line => log?.Add(line));
        return resolver.ResolveLocal(
            options, [], mapping,
            evChargersByMeteringPoint: evChargersByMeteringPoint,
            evChargersByAccount: evChargersByAccount);
    }

    [Fact]
    public void Elekstern_product_that_changed_over_its_history_resolves_each_period_via_the_charger_rule()
    {
        // El-ekstern swapping over time is no longer a reason to pause the whole account — each
        // contract-part tuple resolves independently against the (unchanged, always-confirmed)
        // charger state as of that tuple's own effective date.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("OAboEjeMEL")]
        };

        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = ConfirmedMap,
        };
        var today = new DateTime(2026, 9, 1);
        var nextMonth = new DateTime(2026, 10, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] =
            [
                (MeteringPoint, today, today, nextMonth.AddDays(-1), false, Contract, "V LadVariP", "", "Lbm+1d"),
                (MeteringPoint, nextMonth, nextMonth, MigrationConstants.EndDateNull, false, Contract, "V LadVarUA", "", "Lbm+1d"),
            ]
        };

        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), new WarningCollector(), _ => { });
        var result = resolver.ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[0].ContractName);
        Assert.Equal("LadeAbo_Eje_VariabelUA", mpLinks[1].ContractName);
        Assert.Equal(2, result.MpChargerContractRules[MeteringPoint].Count);
    }

    [Fact]
    public void Unmapped_charge_product_falls_through_unchanged()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("Gl.CLejMEL")]
        };

        var result = Resolve("V LadVariP", evChargersByMeteringPoint: chargers);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Ambiguous_charger_unlinked_to_a_metering_point_falls_through_unchanged()
    {
        // The charger lives in ByAccount (0 or 2+ candidate MPs) — never ByMeteringPoint. That is a
        // different fact from "no charge contract at all" and must not resolve to the sentinel.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [Account] = [Charger("OAboEjeMEL")]
        };

        var result = Resolve("V LadVariP", evChargersByAccount: chargers);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
    }

    // ── two Charge_Abo products live at once (business decision 2026-09-01): both are sent, with
    // Xellent's own overlapping periods, and one warning names what was sent. ──

    [Fact]
    public void Two_products_genuinely_live_at_once_link_both_contracts_with_xellents_own_overlap()
    {
        // The confirmed Xellent data-quality bug: the old row from a completed product change was
        // never closed out (it still carries a real future end date instead of ending the day the
        // new row started), so both products read as live simultaneously right now. Neither is
        // guessed at and the account is not refused: each resolves to its own contract, and each
        // link spans its own product's live window clipped to the electricity tuple — the overlap
        // Settl gets is exactly the overlap Xellent has.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEje0",
                        new DateOnly(2026, 8, 11), new DateOnly(2027, 2, 10)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2026, 2, 11), null),
                ])]
        };

        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = ConfirmedMap,
        };
        // The electricity tuple started before either charger row, so both links must start on
        // their own row's start, not the tuple's.
        var tupleStart = new DateTime(2025, 6, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] = [(MeteringPoint, tupleStart, tupleStart, OpenEnded, false, Contract, "V LadVariP", "", "Lbm+1d")]
        };

        var warnings = new WarningCollector();
        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), warnings, _ => { });
        var result = resolver.ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractName, StringComparer.Ordinal).ToList();
        Assert.Equal(2, mpLinks.Count);

        // The stale row: its own window, ending where Xellent says it ends.
        Assert.Equal("LadeAbo_Eje0kr_Variabel", mpLinks[0].ContractName);
        Assert.Equal(new DateTime(2026, 8, 11), mpLinks[0].ContractPartStartdate);
        Assert.Equal(new DateTime(2027, 2, 10), mpLinks[0].ContractPartEnddate);

        // The real row: its own start, open like the tuple.
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[1].ContractName);
        Assert.Equal(new DateTime(2026, 2, 11), mpLinks[1].ContractPartStartdate);
        Assert.Equal(OpenEnded, mpLinks[1].ContractPartEnddate);

        // The MP-keyed name view shows the open-ended (current) product, and both rules are booked.
        Assert.Equal("LadeAbo_Eje_Variabel", result.MpContractNames[MeteringPoint]);
        Assert.Equal(2, result.MpChargerContractRules[MeteringPoint].Count);
        Assert.Equal(["LadeAbo_Eje0kr_Variabel", "LadeAbo_Eje_Variabel"],
            result.ContractNamesByAccount[Account].OrderBy(n => n, StringComparer.Ordinal));

        // One warning for the account, naming the products and the contracts sent — not one per
        // date the future walk revisits the same state on.
        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("Charge_Abo products live at once", warning, StringComparison.Ordinal);
        Assert.Contains("OAboEje0 → LadeAbo_Eje0kr_Variabel", warning, StringComparison.Ordinal);
        Assert.Contains("OAboEjeMEL → LadeAbo_Eje_Variabel", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_products_live_at_once_with_one_unmapped_links_the_mapped_one_and_names_the_other()
    {
        // One of the two live products has no confirmed entry in the charger table. The MP is not
        // refused for it: the mapped product links as if it were alone (tuple's own period), and
        // the warning says which product was left out and why.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 3, 19), null),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAbLejBero",
                        new DateOnly(2026, 8, 11), new DateOnly(2027, 2, 10)),
                ])]
        };

        var warnings = new WarningCollector();
        var result = ResolveSingleTuple("V LadVariP", chargers, warnings);

        var link = Assert.Single(result.Links);
        Assert.Equal("LadeAbo_Eje_Variabel", link.ContractName);
        Assert.Equal(new DateTime(2026, 9, 1), link.ContractPartStartdate);
        Assert.Equal(OpenEnded, link.ContractPartEnddate);
        Assert.Single(result.MpChargerContractRules[MeteringPoint]);

        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("OAboEjeMEL → LadeAbo_Eje_Variabel", warning, StringComparison.Ordinal);
        Assert.Contains("Left out: OAbLejBero", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_products_live_at_once_with_neither_mapped_falls_through_and_warns()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAbLejBero",
                        new DateOnly(2025, 3, 19), null),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeUEL",
                        new DateOnly(2026, 8, 11), new DateOnly(2027, 2, 10)),
                ])]
        };

        var warnings = new WarningCollector();
        var result = ResolveSingleTuple("V LadVariP", chargers, warnings);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("None of them has a contract", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_row_that_overlaps_only_in_the_future_opens_and_closes_its_own_link_alone()
    {
        // Today one product is live. On 2026-10-01 a second row starts while the first still runs
        // to its stale end 2027-02-10. The future walk must open the second contract's link on its
        // own start and close the first on its own end — per product, not one-for-the-other.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 1, 1), new DateOnly(2027, 2, 10)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                        new DateOnly(2026, 10, 1), null),
                ])]
        };

        var warnings = new WarningCollector();
        var result = ResolveSingleTuple("V LadVariP", chargers, warnings);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[0].ContractName);
        Assert.Equal(new DateTime(2026, 9, 1), mpLinks[0].ContractPartStartdate);
        Assert.Equal(new DateTime(2027, 2, 10), mpLinks[0].ContractPartEnddate);
        Assert.Equal("LadeAbo_Leje_Variabel", mpLinks[1].ContractName);
        Assert.Equal(new DateTime(2026, 10, 1), mpLinks[1].ContractPartStartdate);
        Assert.Equal(OpenEnded, mpLinks[1].ContractPartEnddate);
        Assert.Equal(2, result.MpChargerContractRules[MeteringPoint].Count);
        Assert.Single(warnings.GetWarnings());
    }

    [Fact]
    public void Charger_with_only_a_future_staged_product_and_nothing_live_resolves_to_awaiting_installation_then_synthesizes_the_future_change()
    {
        // A charger row exists (so it's not "no charger at all"), but nothing on it has started yet
        // — that resolves to the NoChargeKey sentinel today, which is confirmed
        // (LadeAbo_Afventer_Installation), and the charger's own future start is then synthesized as
        // a second period.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                    new DateOnly(2027, 1, 1), null)])]
        };

        var result = ResolveSingleTuple("V LadVariP", chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);
        Assert.Equal("LadeAbo_Afventer_Installation", mpLinks[0].ContractName);
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[1].ContractName);
        Assert.Equal(new DateTime(2027, 1, 1), mpLinks[1].ContractPartStartdate);
    }

    [Fact]
    public void Consumption_only_charger_with_no_live_subscription_falls_through_unchanged()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [new EvChargerPart("Charge", EvChargerPartKind.Consumption, "OCha", new DateOnly(2025, 1, 1), null)])]
        };

        var result = Resolve("V LadVariP", evChargersByMeteringPoint: chargers);

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void No_charger_map_configured_falls_through_unchanged()
    {
        var result = Resolve("V LadVariP", chargerMap: new Dictionary<string, Dictionary<string, string>>());

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Charger_extraction_not_run_at_all_does_not_fire_the_no_charge_sentinel()
    {
        // Both lookups null (not merely empty) means charger extraction never ran for this build —
        // a different fact from "ran and found nothing" — so the sentinel must not fire even though
        // a confirmed map exists.
        var result = Resolve("V LadVariP");

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("V LadVariP", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Explicit_account_override_still_wins_over_a_matching_charger_pair()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("OAboEjeMEL")]
        };

        var result = Resolve(
            "V LadVariP", evChargersByMeteringPoint: chargers,
            accountMap: new Dictionary<string, string> { [Account] = "Manual_Override" });

        Assert.Empty(result.MpChargerContractRules);
        Assert.Equal("Manual_Override", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Template_product_rule_still_fires_when_the_charger_rule_does_not_match()
    {
        var result = Resolve(
            "V LadVariP",
            templateProducts: new Dictionary<string, Dictionary<string, TemplateProductContractEntry>>
            {
                ["V_Privat"] = new() { ["V LadVariP"] = TemplateProductContractEntry.FromString("Fallback_Template_Contract") }
            });

        Assert.Equal("Fallback_Template_Contract", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Both_sides_swapping_at_once_resolves_every_period_on_its_own_effective_date()
    {
        // Both sides swap here — el-ekstern on 2026-11-01, charger on 2026-10-15 — this is the shape
        // the domain owner confirmed must resolve in full, not pause: each contract-part tuple
        // resolves against the charger's state as of THAT tuple's own effective date.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 14)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                        new DateOnly(2026, 10, 15), null),
                ])]
        };

        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = ConfirmedMap,
        };

        var today = new DateTime(2026, 9, 1);
        var futureSwap = new DateTime(2026, 11, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] =
            [
                (MeteringPoint, today, today, futureSwap.AddDays(-1), false, Contract, "V LadVariP", "", "Lbm+1d"),
                (MeteringPoint, futureSwap, futureSwap, MigrationConstants.EndDateNull, false, Contract, "V LadVarUA", "", "Lbm+1d"),
            ]
        };

        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), new WarningCollector(), _ => { });
        var result = resolver.ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);
        // Period 1 (today): V LadVariP + still-live OAboEjeMEL.
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[0].ContractName);
        // Period 2 (2026-11-01, after both swaps land): V LadVarUA + OAboLejMEL.
        Assert.Equal("LadeAbo_Leje_VariabelUA", mpLinks[1].ContractName);
        Assert.Equal(2, result.MpChargerContractRules[MeteringPoint].Count);
    }

    [Fact]
    public void Account_522917_both_sides_swap_and_every_period_resolves()
    {
        // The concrete case confirmed with the domain owner: Charge_Abo goes OAboEje0 -> OAboEjeMEL
        // on 2026-12-25; El-ekstern goes V LadVarUA -> V LadVariP on 2027-06-20. Both swaps must
        // resolve for the account to land — neither one pauses it any more.
        const string account = "522917";
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEje0",
                        new DateOnly(2026, 6, 25), new DateOnly(2026, 12, 24)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2026, 12, 25), null),
                ])]
        };

        var options = new CreateOptions
        {
            Accounts = [account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 8, 27),
            ElectricityChargerContractMap = ConfirmedMap,
        };

        var elStart1 = new DateTime(2026, 6, 20);
        var elStart2 = new DateTime(2027, 6, 20);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [account] =
            [
                (MeteringPoint, elStart1, elStart1, elStart2.AddDays(-1), false, Contract, "V LadVarUA", "", "Lbm+1d"),
                (MeteringPoint, elStart2, elStart2, MigrationConstants.EndDateNull, false, Contract, "V LadVariP", "", "Lbm+1d"),
            ]
        };

        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), new WarningCollector(), _ => { });
        var result = resolver.ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);

        // Period 1 (2026-06-20, effective 2026-08-27): charger is on OAboEje0, its own confirmed
        // no-fee contract.
        Assert.Equal("LadeAbo_Eje0kr_VariabelUA", mpLinks[0].ContractName);

        // Period 2 (2027-06-20): both the el-ekstern product and the charger's product have swapped
        // by then, and the charger side is now the confirmed OAboEjeMEL — resolves via the charger
        // rule.
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[1].ContractName);
        Assert.Equal(2, result.MpChargerContractRules[MeteringPoint].Count);
        var rule = result.MpChargerContractRules[MeteringPoint][1];
        Assert.Equal("V LadVariP", rule.ElProductNum);
        Assert.Equal("OAboEjeMEL", rule.ChargeAboProductNum);
    }

    // ── charger-only future changes (both periods resolve, one link each side of the swap) ─────

    private static readonly DateTime OpenEnded = MigrationConstants.EndDateNull;

    [Fact]
    public void Single_real_tuple_with_a_future_charger_segment_synthesizes_a_second_period()
    {
        // Electricity side never changes (one tuple), but the charger switches owned -> rented on
        // 2026-10-15 — today's period resolves against OAboEjeMEL, and the charger's own future
        // change is picked up afterwards, splitting the open tuple into two periods.
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 14)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                        new DateOnly(2026, 10, 15), null),
                ])]
        };
        var result = ResolveSingleTuple("V LadVariP", chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint)
            .OrderBy(l => l.ContractPartStartdate).ToList();
        Assert.Equal(2, mpLinks.Count);
        Assert.Equal("LadeAbo_Eje_Variabel", mpLinks[0].ContractName);
        Assert.Equal(new DateTime(2026, 10, 14), mpLinks[0].ContractPartEnddate);
        Assert.Equal("LadeAbo_Leje_Variabel", mpLinks[1].ContractName);
        Assert.Equal(new DateTime(2026, 10, 15), mpLinks[1].ContractPartStartdate);
        Assert.Equal(2, result.MpChargerContractRules[MeteringPoint].Count);
    }

    [Fact]
    public void Single_real_tuple_with_no_future_charger_segment_produces_one_link_unchanged()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [Charger("OAboEjeMEL")]
        };
        var result = ResolveSingleTuple("V LadVariP", chargers);

        Assert.Single(result.Links);
        Assert.Single(result.MpChargerContractRules[MeteringPoint]);
    }

    [Fact]
    public void Adjacent_future_segments_resolving_to_the_same_contract_are_not_duplicated()
    {
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
        {
            [MeteringPoint] = [new EvCharger("1086589", Account, "10562433", null,
                [
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 14)),
                    new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboEjeMEL",
                        new DateOnly(2026, 10, 15), null),
                ])]
        };
        var result = ResolveSingleTuple("V LadVariP", chargers);

        var mpLinks = result.Links.Where(l => l.MeteringPointId == MeteringPoint).ToList();
        Assert.Single(mpLinks);
        Assert.Equal(OpenEnded, mpLinks[0].ContractPartEnddate);
        Assert.Single(result.MpChargerContractRules[MeteringPoint]);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>One account, one MP, one open-ended electricity contract-part tuple starting
    /// 2026-09-01 -- for tests exercising charger-only future changes.</summary>
    private static ContractNameResolver.Result ResolveSingleTuple(
        string elProduct, IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> chargers,
        WarningCollector? warnings = null)
    {
        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = ConfirmedMap,
        };
        var today = new DateTime(2026, 9, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] = [(MeteringPoint, today, today, OpenEnded, false, Contract, elProduct, "", "Lbm+1d")]
        };

        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), warnings ?? new WarningCollector(), _ => { });
        return resolver.ResolveLocal(options, [], mapping, evChargersByMeteringPoint: chargers);
    }

    private static EvCharger Charger(string chargeAboProduct) =>
        new("1086589", Account, "10562433", "Fiktivgadec 90, 8920 Randers NV",
            [new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, chargeAboProduct, new DateOnly(2025, 3, 19), null)]);

    private static ContractNameResolver.Result Resolve(
        string elProduct,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint = null,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByAccount = null,
        Dictionary<string, Dictionary<string, string>>? chargerMap = null,
        Dictionary<string, string>? accountMap = null,
        Dictionary<string, Dictionary<string, TemplateProductContractEntry>>? templateProducts = null,
        WarningCollector? warnings = null)
    {
        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 9, 1),
            ElectricityChargerContractMap = chargerMap ?? ConfirmedMap,
            AccountNumContractMap = accountMap,
            TemplateProductContractMap = templateProducts,
            ContractTemplatesByContractNum = templateProducts is null
                ? []
                : new Dictionary<string, string> { [Contract] = "V_Privat" }
        };

        var day = new DateTime(2026, 9, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] = [(MeteringPoint, day, day, day, false, Contract, elProduct, "", "Lbm+1d")]
        };

        var groupings = accountMap is null
            ? new List<AccountGrouping>()
            : [new AccountGrouping(Account, [Account], null, null, null, [])];

        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), warnings ?? new WarningCollector(), _ => { });

        return resolver.ResolveLocal(
            options, groupings, mapping,
            evChargersByMeteringPoint: evChargersByMeteringPoint,
            evChargersByAccount: evChargersByAccount);
    }
}
