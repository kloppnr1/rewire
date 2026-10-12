using System.Text;
using DataMigration.Core;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Tests;

/// <summary>
/// One product, two right answers. <c>V Variabel</c> on contract template <c>V_Privat</c> belongs on
/// the Settl contract <c>E_ErhVari</c> for the ~40 companies that hold it, and on the contract of its
/// own name for the ~17,600 private customers — who are already correct and must not move. A plain
/// product mapping cannot say that; it would move all of them.
///
/// <para>These tests pin the precedence chain, both directions of the split, and the
/// case-insensitivity that the <c>lbm+1d</c> incident bought.</para>
/// </summary>
public class TemplateProductContractMappingTests
{
    private const string Account = "102560";
    private const string MeteringPoint = "571313179104952730";
    private const string Contract = "10263929";
    private const string Product = "V Variabel";
    private const string Template = "V_Privat";

    private static Dictionary<string, Dictionary<string, TemplateProductContractEntry>> CvrRule(
        string template = Template, string product = Product, string contract = "E_ErhVari") =>
        new() { [template] = new() { [product] = new TemplateProductContractEntry { Cvr = contract } } };

    [Fact]
    public void A_company_on_V_Privat_holding_V_Variabel_lands_on_E_ErhVari()
    {
        var result = Resolve(templateProducts: CvrRule(), identity: CustomerIdentity.Cvr);

        Assert.Equal("E_ErhVari", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void A_private_customer_with_the_same_template_and_product_keeps_the_product_name()
    {
        // The 17,589. The rule says nothing about a CPR customer, so resolution falls through and
        // the run ends up exactly where it does today.
        var result = Resolve(templateProducts: CvrRule(), identity: CustomerIdentity.Cpr);

        Assert.Equal(Product, result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void A_customer_with_neither_CVR_nor_CPR_is_left_alone_too()
    {
        var result = Resolve(templateProducts: CvrRule(), identity: CustomerIdentity.Blank);

        Assert.Equal(Product, result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Template_plus_product_beats_a_plain_product_mapping()
    {
        var result = Resolve(
            templateProducts: CvrRule(),
            identity: CustomerIdentity.Cvr,
            productMap: new Dictionary<string, ProductNumContractEntry>
            {
                [Product] = ProductNumContractEntry.FromString("E_Plain_Product")
            });

        Assert.Equal("E_ErhVari", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void The_plain_product_mapping_still_answers_when_the_template_rule_does_not()
    {
        var result = Resolve(
            templateProducts: CvrRule(),
            identity: CustomerIdentity.Cpr,
            productMap: new Dictionary<string, ProductNumContractEntry>
            {
                [Product] = ProductNumContractEntry.FromString("E_Plain_Product")
            });

        Assert.Equal("E_Plain_Product", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void An_explicit_account_mapping_outranks_everything()
    {
        var result = Resolve(
            templateProducts: CvrRule(),
            identity: CustomerIdentity.Cvr,
            accountMap: new Dictionary<string, string> { [Account] = "E_Named_By_Hand" });

        Assert.Equal("E_Named_By_Hand", result.MpContractNames[MeteringPoint]);
    }

    [Theory]
    // The config key's casing must not decide the outcome — that is the lbm+1d incident again.
    [InlineData("v_privat", "v variabel")]
    [InlineData("V_PRIVAT", "V VARIABEL")]
    // Xellent holds both spellings of the template on this very product, so they are one key.
    [InlineData("V Privat", "V Variabel")]
    public void Template_and_product_keys_match_case_insensitively_and_across_the_space_underscore_spelling(
        string templateKey, string productKey)
    {
        var result = Resolve(
            templateProducts: CvrRule(template: templateKey, product: productKey),
            identity: CustomerIdentity.Cvr);

        Assert.Equal("E_ErhVari", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void The_metering_points_own_template_is_matched_the_same_way_whichever_way_Xellent_spells_it()
    {
        var result = Resolve(
            templateProducts: CvrRule(template: "V_Privat"),
            identity: CustomerIdentity.Cvr,
            contractTemplate: "V Privat");

        Assert.Equal("E_ErhVari", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void A_rule_for_another_template_does_not_touch_this_metering_point()
    {
        var result = Resolve(
            templateProducts: CvrRule(template: "V_Erhverv"),
            identity: CustomerIdentity.Cvr);

        Assert.Equal(Product, result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void An_exact_product_wins_over_a_prefix_and_a_catch_all_whatever_order_they_are_written_in()
    {
        var templateProducts = new Dictionary<string, Dictionary<string, TemplateProductContractEntry>>
        {
            [Template] = new()
            {
                ["*"] = new TemplateProductContractEntry { Cvr = "E_Catch_All" },
                ["V*"] = new TemplateProductContractEntry { Cvr = "E_Prefix" },
                [Product] = new TemplateProductContractEntry { Cvr = "E_ErhVari" }
            }
        };

        var result = Resolve(templateProducts, identity: CustomerIdentity.Cvr);

        Assert.Equal("E_ErhVari", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void A_star_entry_answers_for_every_identification()
    {
        var templateProducts = new Dictionary<string, Dictionary<string, TemplateProductContractEntry>>
        {
            [Template] = new()
            {
                [Product] = new TemplateProductContractEntry { Cvr = "E_ErhVari", Default = "V Variabel" }
            }
        };

        Assert.Equal("E_ErhVari", Resolve(templateProducts, CustomerIdentity.Cvr).MpContractNames[MeteringPoint]);
        Assert.Equal("V Variabel", Resolve(templateProducts, CustomerIdentity.Cpr).MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void Without_the_contract_template_the_rule_stays_inert_rather_than_guessing()
    {
        // A run whose template lookup found nothing for this contract must behave as it did before
        // the rule existed — never as if the template had matched.
        var result = Resolve(CvrRule(), CustomerIdentity.Cvr, contractTemplate: null);

        Assert.Equal(Product, result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void The_appsettings_key_this_ships_with_binds_to_the_rule_it_is_meant_to_be()
    {
        // Guards the exact shape added to DataMigration.Migration/appsettings.json.
        const string json = """
        {
          "Mappings": {
            "TemplateProducts": {
              "V_Privat": {
                "V Variabel": { "cvr": "E_ErhVari" }
              }
            }
          }
        }
        """;
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var mappings = BaseMappingLoader.Load(config);
        var rules = TemplateProductContractRules.Build(mappings!.TemplateProducts);

        Assert.Equal("E_ErhVari", rules!.Resolve("V_Privat", "V Variabel", CustomerIdentity.Cvr));
        Assert.Null(rules.Resolve("V_Privat", "V Variabel", CustomerIdentity.Cpr));
        Assert.Null(rules.Resolve("V_Erhverv", "V Variabel", CustomerIdentity.Cvr));
    }

    [Fact]
    public void The_mapped_contract_reaches_the_report_payload_the_HTML_report_is_built_from()
    {
        // The report shows the contract per metering point (MpContractNames) and groups metering
        // points by it (MpContractLinks). Both must carry the MAPPED contract, not the product —
        // otherwise the rule fires and the report still says V Variabel.
        var result = Resolve(CvrRule(), CustomerIdentity.Cvr);

        Assert.Equal("E_ErhVari", result.MpContractNames[MeteringPoint]);
        var link = Assert.Single(result.Links);
        Assert.Equal("E_ErhVari", link.ContractName);
        Assert.Equal(Product, link.ProductNum);
        Assert.Equal(["E_ErhVari"], result.ContractNamesByAccount[Account]);
    }

    [Fact]
    public void The_report_can_say_which_rule_mapped_the_contract()
    {
        var moved = Resolve(CvrRule(), CustomerIdentity.Cvr);

        var rule = moved.MpContractRules[MeteringPoint].Single();
        Assert.Equal(Template, rule.ContractTemplate);
        Assert.Equal(Product, rule.ProductNum);
        Assert.Equal("cvr", rule.Identification);
        Assert.Equal("E_ErhVari", rule.ContractName);

        // A metering point no rule moved carries no rule — its contract needs no explanation.
        var untouched = Resolve(CvrRule(), CustomerIdentity.Cpr);
        Assert.Empty(untouched.MpContractRules);
        Assert.Equal(Product, untouched.MpContractNames[MeteringPoint]);
    }

    /// <summary>
    /// One account, one metering point, one contract part — through the same local resolution the
    /// runner uses, so what is asserted is what a run would do.
    /// </summary>
    private static ContractNameResolver.Result Resolve(
        Dictionary<string, Dictionary<string, TemplateProductContractEntry>>? templateProducts,
        CustomerIdentity identity,
        Dictionary<string, ProductNumContractEntry>? productMap = null,
        Dictionary<string, string>? accountMap = null,
        string? contractTemplate = Template)
    {
        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 1, 1),
            ProductNumContractMap = productMap,
            AccountNumContractMap = accountMap,
            TemplateProductContractMap = templateProducts,
            ContractTemplatesByContractNum = contractTemplate is null
                ? []
                : new Dictionary<string, string> { [Contract] = contractTemplate }
        };

        var day = new DateTime(2026, 1, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] = [(MeteringPoint, day, day, day, false, Contract, Product, "", "Lbm+1d")]
        };

        var groupings = accountMap is null
            ? new List<AccountGrouping>()
            : [new AccountGrouping(Account, [Account], null, null, null, [])];

        var resolver = new ContractNameResolver(
            new Fixtures.MockContractClient(), new WarningCollector(), _ => { });

        return resolver.ResolveLocal(
            options,
            groupings,
            mapping,
            new Dictionary<string, CustomerIdentity> { [Account] = identity });
    }
}
