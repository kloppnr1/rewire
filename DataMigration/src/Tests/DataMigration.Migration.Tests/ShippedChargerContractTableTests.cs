using DataMigration.Migration.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Tests;

/// <summary>
/// The charger table as it ships in <c>src/DataMigration.Migration/appsettings.json</c> — the file
/// that deploys — read through the same loader the CLI uses. Pins the reserved keys the resolver
/// depends on, so a hand edit of the JSON cannot silently drop one.
/// </summary>
public class ShippedChargerContractTableTests
{
    [Fact]
    public void The_multi_installation_key_round_trips_from_the_shipped_appsettings()
    {
        var rules = ShippedRules();

        Assert.Equal(
            "LadeAbo_Eje_2inst_1mp_Variabel",
            rules.Resolve("V LadVariP", ElectricityChargerContractRules.MultipleInstallationsKey));
    }

    [Fact]
    public void The_no_fee_product_has_no_multi_installation_contract_because_settl_has_none()
    {
        Assert.Null(ShippedRules().Resolve("V LadVarUA", ElectricityChargerContractRules.MultipleInstallationsKey));
    }

    [Fact]
    public void The_no_charge_sentinel_round_trips_on_both_electricity_products()
    {
        var rules = ShippedRules();

        Assert.Equal("LadeAbo_Afventer_Installation", rules.Resolve("V LadVariP", ElectricityChargerContractRules.NoChargeKey));
        Assert.Equal("LadeAbo_Afventer_Installation", rules.Resolve("V LadVarUA", ElectricityChargerContractRules.NoChargeKey));
    }

    [Fact]
    public void The_rules_loader_accepts_the_reserved_keys_like_any_productnum()
    {
        var rules = ElectricityChargerContractRules.Build(new Dictionary<string, Dictionary<string, string>>
        {
            ["V LadVariP"] = new()
            {
                [ElectricityChargerContractRules.MultipleInstallationsKey] = "Multi",
                [ElectricityChargerContractRules.NoChargeKey] = "None",
            }
        });

        Assert.NotNull(rules);
        Assert.Equal("Multi", rules.Resolve("V LadVariP", "__MultipleInstallations__"));
        Assert.Equal("Multi", rules.Resolve("v ladvarip", "__multipleinstallations__"));
        Assert.Equal("None", rules.Resolve("V LadVariP", "__NoCharge__"));
        Assert.Null(rules.Resolve("V LadVarUA", "__MultipleInstallations__"));
    }

    private static ElectricityChargerContractRules ShippedRules()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ShippedTemplateProductTableTests.RepoRoot(), "src", "DataMigration.Migration", "appsettings.json"))
            .Build();

        var mappings = BaseMappingLoader.Load(config);
        return ElectricityChargerContractRules.Build(mappings!.ElectricityChargerContractMap)
            ?? throw new InvalidOperationException("appsettings.json carries no ElectricityChargerContractMap table.");
    }
}
