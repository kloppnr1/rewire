using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

/// <summary>
/// The portioning the loader applies before handing an account list to
/// <c>AccountRepresentativeResolver</c>. Two things have to hold: a portion never straddles a CVR
/// class (splitting one changes the representative), and the merged result is what a single call
/// would have produced.
/// </summary>
public class RepresentativePortioningTests
{
    private static readonly Dictionary<string, List<string>> NoSiblings =
        new(StringComparer.Ordinal);

    private static AccountRepresentativeResolution Resolution(
        params (string Account, string Representative)[] pairs) =>
        new(
            [.. pairs.Where(p => p.Account == p.Representative).Select(p => p.Account)],
            pairs.Length - pairs.Count(p => p.Account == p.Representative),
            pairs.ToDictionary(p => p.Account, p => "name-" + p.Account, StringComparer.OrdinalIgnoreCase),
            pairs.ToDictionary(p => p.Account, p => p.Representative, StringComparer.Ordinal),
            new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal));

    // --- portioning: no sibling pull-in, classes stay whole ---

    [Fact]
    public void Portions_hold_exactly_the_accounts_they_were_given()
    {
        var cvr = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["100"] = "11111111",
            ["200"] = "11111111",
            ["300"] = "22222222",
            ["400"] = ""
        };

        var portions = CosStatePlanner.BuildCvrPortions(cvr.Keys, cvr, NoSiblings, portionSize: 1);

        Assert.Equal(
            new[] { "100", "200", "300", "400" },
            portions.SelectMany(p => p.Accounts).OrderBy(a => a, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_cvr_class_is_never_split_across_two_portions()
    {
        var cvr = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["100"] = "11111111",
            ["200"] = "11111111",
            ["300"] = "11111111",
            ["400"] = "22222222"
        };

        var portions = CosStatePlanner.BuildCvrPortions(cvr.Keys, cvr, NoSiblings, portionSize: 1);

        foreach (var account in new[] { "100", "200", "300" })
            Assert.Single(portions, p => p.Accounts.Contains(account));

        var shared = portions.Single(p => p.Accounts.Contains("100"));
        Assert.Contains("200", shared.Accounts);
        Assert.Contains("300", shared.Accounts);
    }

    [Fact]
    public void Account_with_no_cvr_row_is_its_own_portion_class()
    {
        // Absent from cvrByAccount = no CustTable row. It forms no merge edges in the resolver, so it
        // is free to pack anywhere — but it must still be resolved.
        var portions = CosStatePlanner.BuildCvrPortions(
            ["100", "200"], new Dictionary<string, string>(StringComparer.Ordinal), NoSiblings, portionSize: 1);

        Assert.Equal(2, portions.Count);
        Assert.Equal(
            new[] { "100", "200" },
            portions.SelectMany(p => p.Accounts).OrderBy(a => a, StringComparer.Ordinal).ToArray());
    }

    // --- merging ---

    [Fact]
    public void Merging_one_part_returns_it_untouched()
    {
        var only = Resolution(("100", "100"));
        Assert.Same(only, RepresentativePortioning.Merge([only]));
    }

    [Fact]
    public void Merged_result_matches_what_one_call_would_have_produced()
    {
        var single = Resolution(("100", "100"), ("200", "100"), ("300", "300"));
        var merged = RepresentativePortioning.Merge(
            [Resolution(("100", "100"), ("200", "100")), Resolution(("300", "300"))]);

        Assert.Equal(single.Accounts, merged.Accounts);
        Assert.Equal(single.MergedCount, merged.MergedCount);
        Assert.Equal(single.RepresentativeByAccount, merged.RepresentativeByAccount);
        Assert.Equal(single.NameByAccount, merged.NameByAccount);
    }

    [Fact]
    public void Merged_accounts_are_ordered_regardless_of_portion_order()
    {
        var merged = RepresentativePortioning.Merge(
            [Resolution(("300", "300")), Resolution(("100", "100")), Resolution(("200", "200"))]);

        Assert.Equal(new[] { "100", "200", "300" }, merged.Accounts);
    }
}
