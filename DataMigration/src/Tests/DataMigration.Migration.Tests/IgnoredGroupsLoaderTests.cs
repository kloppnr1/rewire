using DataMigration.Migration.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Tests;

public class IgnoredGroupsLoaderTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Missing_section_gives_empty_list()
    {
        Assert.Empty(IgnoredGroupsLoader.Load(Config(("Unrelated:Key", "value"))));
    }

    [Fact]
    public void Reads_the_shared_list_in_order()
    {
        var groups = IgnoredGroupsLoader.Load(Config(
            ("Extraction:IgnoredGroups:0", "Medarbej"),
            ("Extraction:IgnoredGroups:1", "Hybel"),
            ("Extraction:IgnoredGroups:2", "Velk strøm")));

        Assert.Equal(["Medarbej", "Hybel", "Velk strøm"], groups);
    }

    [Fact]
    public void Profile_groups_are_added_on_top_of_the_base_not_replacing_it()
    {
        var union = IgnoredGroupsLoader.Union(["Medarbej", "Hybel"], ["Kun i profilen"]);

        Assert.Equal(["Medarbej", "Hybel", "Kun i profilen"], union!);
    }

    [Fact]
    public void Casing_does_not_produce_a_duplicate()
    {
        // Xellents GroupNum-casing er ikke stabil; MEDARBEJ og Medarbej er samme gruppe.
        var union = IgnoredGroupsLoader.Union(["Medarbej"], ["MEDARBEJ", "Hybel"]);

        Assert.Equal(["Medarbej", "Hybel"], union!);
    }

    [Fact]
    public void Applying_the_same_list_twice_changes_nothing()
    {
        // Listen påføres både i options-fabrikken og i OpsCli's Xellent-loader.
        var once = IgnoredGroupsLoader.Union(["Medarbej", "Hybel"], null);
        var twice = IgnoredGroupsLoader.Union(["Medarbej", "Hybel"], once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Empty_stays_null_so_nothing_is_ignored()
    {
        Assert.Null(IgnoredGroupsLoader.Union(null, null));
        Assert.Null(IgnoredGroupsLoader.Union([], []));
        Assert.Null(IgnoredGroupsLoader.ToSet([]));
    }

    [Fact]
    public void Blank_entries_are_dropped()
    {
        Assert.Equal(["Medarbej"], IgnoredGroupsLoader.Union(["  ", "Medarbej", ""], null)!);
    }

    [Fact]
    public void Set_form_matches_case_insensitively()
    {
        var set = IgnoredGroupsLoader.ToSet(["Medarbej"]);

        Assert.True(set!.Contains("MEDARBEJ"));
    }

    [Fact]
    public void Profile_without_its_own_list_still_gets_the_base()
    {
        var profile = new ProfileOptions
        {
            Name = "all-active",
            Extraction = new ExtractionOptions { DeliveryCategory = "El-ekstern" }
        };

        var options = CreateOptionsFactory.Build(
            profile, env: null, referenceDate: new DateTime(2026, 1, 1),
            exemption: new BillingExemptionConfig(null, null, null, 1),
            baseIgnoredGroups: ["Medarbej", "Hybel"]);

        Assert.Equal(["Medarbej", "Hybel"], options.IgnoredGroups!);
    }

    [Fact]
    public void Reconcile_mapping_without_its_own_list_still_gets_the_base()
    {
        var mapping = new ReconcileMappingOptions
        {
            DataAreaId = "hol",
            DeliveryCategory = "El-ekstern",
        };

        var options = CreateOptionsFactory.BuildFromMapping(
            mapping, accounts: ["100"], env: null, referenceDate: new DateTime(2026, 1, 1),
            exemption: new BillingExemptionConfig(null, null, null, 1),
            baseIgnoredGroups: ["Medarbej"],
            cos: new DateOnly(2026, 1, 2));

        Assert.Equal(["Medarbej"], options.IgnoredGroups!);
    }
}
