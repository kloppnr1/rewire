using DataMigration.Migration.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Tests;

public class BaseMappingLoaderTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Returns_null_when_section_absent()
    {
        Assert.Null(BaseMappingLoader.Load(Config(("Unrelated:Key", "value"))));
    }

    [Fact]
    public void Reads_scalar_product_entry()
    {
        var mappings = BaseMappingLoader.Load(Config(("Mappings:ProductNum:V ErhKvt", "E_ErhKvt")));

        var entry = mappings!.ProductNum!["V ErhKvt"];
        Assert.Equal("E_ErhKvt", entry.Resolve(paymentTerm: null));
        Assert.Equal("E_ErhKvt", entry.Resolve("anything"));
    }

    [Fact]
    public void Reads_payment_term_keyed_product_entry()
    {
        var mappings = BaseMappingLoader.Load(Config(
            ("Mappings:ProductNum:VGGSpot10:lb+15", "E_VGGSpot10_Lb+15d"),
            ("Mappings:ProductNum:VGGSpot10:lbm+1d", "E_VGGSpot10_Lb+1d")));

        var entry = mappings!.ProductNum!["VGGSpot10"];
        Assert.Equal("E_VGGSpot10_Lb+15d", entry.Resolve("lb+15"));
        Assert.Equal("E_VGGSpot10_Lb+1d", entry.Resolve("lbm+1d"));
        Assert.Null(entry.Resolve("unknown-term"));
    }

    [Fact]
    public void Payment_term_keys_from_appsettings_match_case_insensitively()
    {
        // The shared mapping in appsettings is where the production incident lived: the config
        // says 'lbm+1d', every live contract part in Xellent says 'Lbm+1d', and the accounts fell
        // through to the raw product name. This is the path a profile-less run takes.
        var mappings = BaseMappingLoader.Load(Config(
            ("Mappings:ProductNum:VGGSpotH0:lb+31", "E_VGGSpot0H_Lb+31d"),
            ("Mappings:ProductNum:VGGSpotH0:lbm+1d", "E_VGGSpot0H_Lb+1d")));

        var entry = mappings!.ProductNum!["VGGSpotH0"];
        Assert.Equal("E_VGGSpot0H_Lb+1d", entry.Resolve("Lbm+1d"));
        Assert.Equal("E_VGGSpot0H_Lb+31d", entry.Resolve("Lb+31"));
    }

    [Fact]
    public void Wildcard_key_becomes_default()
    {
        var mappings = BaseMappingLoader.Load(Config(
            ("Mappings:ProductNum:VH Spot:M14", "VH Spot Måned"),
            ("Mappings:ProductNum:VH Spot:*", "VH Spot")));

        var entry = mappings!.ProductNum!["VH Spot"];
        Assert.Equal("VH Spot Måned", entry.Resolve("M14"));
        Assert.Equal("VH Spot", entry.Resolve("noget-andet"));
    }

    [Fact]
    public void Reads_settlement_group_map()
    {
        var mappings = BaseMappingLoader.Load(Config(
            ("Mappings:SettlementGroup:Kvartal", "Kvartalsvis Opgørelse")));

        Assert.Equal("Kvartalsvis Opgørelse", mappings!.SettlementGroup!["Kvartal"]);
    }

    [Fact]
    public void Merge_lets_profile_win_per_key_and_keeps_base_for_the_rest()
    {
        var baseMappings = BaseMappingLoader.Load(Config(
            ("Mappings:ProductNum:V ErhKvt", "E_ErhKvt"),
            ("Mappings:ProductNum:V Vari UA", "V Variabel UA")))!;

        var profileMappings = new MappingOptions
        {
            ProductNum = new Dictionary<string, ProductNumContractEntry>
            {
                ["V Vari UA"] = ProductNumContractEntry.FromString("Profil-specifik")
            }
        };

        var merged = BaseMappingLoader.Merge(baseMappings, profileMappings)!;

        Assert.Equal("Profil-specifik", merged.ProductNum!["V Vari UA"].Resolve(null));
        Assert.Equal("E_ErhKvt", merged.ProductNum["V ErhKvt"].Resolve(null));
    }

    [Fact]
    public void Merge_does_not_mutate_the_base()
    {
        var baseMappings = BaseMappingLoader.Load(Config(
            ("Mappings:ProductNum:V ErhKvt", "E_ErhKvt")))!;

        var profileMappings = new MappingOptions
        {
            ProductNum = new Dictionary<string, ProductNumContractEntry>
            {
                ["V ErhKvt"] = ProductNumContractEntry.FromString("Override")
            }
        };

        BaseMappingLoader.Merge(baseMappings, profileMappings);

        Assert.Equal("E_ErhKvt", baseMappings.ProductNum!["V ErhKvt"].Resolve(null));
    }

    [Fact]
    public void Merge_falls_back_to_whichever_side_exists()
    {
        var only = new MappingOptions();
        Assert.Same(only, BaseMappingLoader.Merge(null, only));
        Assert.Same(only, BaseMappingLoader.Merge(only, null));
    }
}
