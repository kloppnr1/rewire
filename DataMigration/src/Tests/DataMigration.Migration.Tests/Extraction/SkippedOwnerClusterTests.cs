using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.Migration.Services.Extraction;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Tests.Extraction;

/// <summary>
/// En ejer-klynge der ikke bliver til en ejer-kunde stod før kun som en advarsel i loggen, hvor
/// den drukner i en all-active-kørsel. Rapporten skal kunne vise hvilke ejere der faldt fra, og
/// hvorfor — medlemmerne er migreret, de hænger bare ikke under en ejer.
/// </summary>
public class SkippedOwnerClusterTests
{
    private static CreateCustomerDto Customer(string account, string? cvr = "40626581") => new()
    {
        Id = Guid.NewGuid(),
        CustomerNumber = account,
        VatIdentificationNumber = cvr,
    };

    private static OwnerCluster Cluster(string key, params string[] members) =>
        new(ClusterKey: key, OwnerName: $"Klynge {key}", MemberAccountnums: [.. members], LowestAccountnum: members[0]);

    private static async Task<List<OwnerClusterSkipInfo>> RunAsync(
        List<OwnerCluster> clusters, List<CreateCustomerDto> customers, OwnerRunState? state = null)
    {
        var skipped = new List<OwnerClusterSkipInfo>();
        var resolver = new OwnerClusterResolver(
            new Fixtures.MockCustomerClient(), new WarningCollector(), _ => { }, state);

        await resolver.ApplyAsync(
            customers, clusters, new Dictionary<Guid, string>(), [], dryRun: true, CancellationToken.None, skipped);

        return skipped;
    }

    [Fact]
    public async Task No_member_in_the_extract_is_reported_with_the_lowest_account()
    {
        var clusters = new List<OwnerCluster> { Cluster("NSDades", "239286", "239290") };

        // Kørslen har ingen af klyngens konti med — den slags klynge kan ikke få stamdata.
        var skipped = await RunAsync(clusters, [Customer("500001")]);

        var row = Assert.Single(skipped);
        Assert.Equal("NSDades", row.ClusterKey);
        Assert.Equal("Klynge NSDades", row.GroupName);
        Assert.Contains("239286", row.Reason);
        Assert.Equal(new[] { "239286", "239290" }, row.MemberAccounts);
    }

    [Fact]
    public async Task A_member_without_cvr_is_reported_with_the_account_it_tried()
    {
        var clusters = new List<OwnerCluster> { Cluster("Uden", "600001", "600002") };

        var skipped = await RunAsync(clusters, [Customer("600001", cvr: null), Customer("600002")]);

        var row = Assert.Single(skipped);
        Assert.Equal("600001", row.SourceAccount);
        Assert.Contains("CVR", row.Reason);
    }

    [Fact]
    public async Task Too_few_members_to_link_is_reported()
    {
        var clusters = new List<OwnerCluster> { Cluster("Enlig", "700001", "700002") };

        // Kun ét medlem er med i udtrækket, og uden run-state kræver en ejer mindst to.
        var skipped = await RunAsync(clusters, [Customer("700001")]);

        var row = Assert.Single(skipped);
        Assert.Contains("1 kunde", row.Reason);
    }

    [Fact]
    public async Task A_cluster_that_becomes_an_owner_is_not_reported()
    {
        var clusters = new List<OwnerCluster> { Cluster("Fin", "800001", "800002") };

        var skipped = await RunAsync(clusters, [Customer("800001"), Customer("800002")]);

        Assert.Empty(skipped);
    }
}
