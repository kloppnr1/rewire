using DataMigration.Migration.Services;

namespace DataMigration.Migration.Tests.Extraction;

/// <summary>
/// The scope-wide clustering a batched run leans on. It has to land on the same clusters the
/// single-pass path lands on — otherwise a batched run and a profile run would disagree about who
/// belongs under which owner.
/// </summary>
public class OwnerClusterSelectorTests
{
    private static (string, string, string?) Row(string account, string tg4, string? groupName = "Newsec Dades") =>
        (account, tg4, groupName);

    [Fact]
    public void Accounts_sharing_a_group_become_one_cluster_named_after_the_group()
    {
        var clusters = OwnerClusterSelector.BuildClusters(
            [Row("239250", "NSDades"), Row("239246", "NSDades")], null);

        var cluster = Assert.Single(clusters);
        Assert.Equal("NSDades", cluster.ClusterKey);
        Assert.Equal("Newsec Dades", cluster.OwnerName);
        Assert.Equal(["239246", "239250"], cluster.MemberAccountnums);
        Assert.Equal("239246", cluster.LowestAccountnum);
    }

    /// <summary>
    /// The whole point: members that a batched run would have met in four separate batches are
    /// one cluster here, because the clustering sees the scope.
    /// </summary>
    [Fact]
    public void Members_are_gathered_regardless_of_which_batch_they_would_have_fallen_in()
    {
        var clusters = OwnerClusterSelector.BuildClusters(
            [Row("239246", "NSDades"), Row("412001", "TestGrp", "Fiktiv Personh A/S"),
             Row("239254", "NSDades"), Row("412002", "TestGrp", "Fiktiv Personh A/S"),
             Row("523991", "NSDades")], null);

        Assert.Equal(["NSDades", "TestGrp"], clusters.Select(c => c.ClusterKey));
        Assert.Equal(3, clusters[0].MemberAccountnums.Count);
    }

    [Fact]
    public void A_group_with_one_account_is_not_a_cluster()
    {
        var clusters = OwnerClusterSelector.BuildClusters([Row("239246", "NSDades")], null);

        Assert.Empty(clusters);
    }

    [Fact]
    public void An_ignored_group_never_clusters()
    {
        var clusters = OwnerClusterSelector.BuildClusters(
            [Row("100", "Medarbej"), Row("101", "Medarbej"), Row("200", "NSDades"), Row("201", "NSDades")],
            ["medarbej"]);

        Assert.Equal(["NSDades"], clusters.Select(c => c.ClusterKey));
    }

    /// <summary>Group values are compared case-insensitively, as everywhere else in the pipeline.</summary>
    [Fact]
    public void Group_values_that_differ_only_in_case_are_the_same_cluster()
    {
        var clusters = OwnerClusterSelector.BuildClusters(
            [Row("100", "NSDades"), Row("101", "nsdades")], null);

        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].MemberAccountnums.Count);
    }

    [Fact]
    public void A_group_without_a_name_falls_back_to_the_group_value()
    {
        var clusters = OwnerClusterSelector.BuildClusters(
            [Row("100", "NSDades", null), Row("101", "NSDades", null)], null);

        Assert.Equal("NSDades", Assert.Single(clusters).OwnerName);
    }

    /// <summary>The same account can come back on more than one row; it is still one member.</summary>
    [Fact]
    public void A_repeated_account_counts_once()
    {
        var clusters = OwnerClusterSelector.BuildClusters(
            [Row("100", "NSDades"), Row("100", "NSDades")], null);

        Assert.Empty(clusters);
    }
}
