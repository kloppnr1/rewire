using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services;
using AccountGrouping = DataMigration.Migration.Runners.AccountGrouping;

namespace DataMigration.Migration.Tests.Extraction;

public class OwnerClusterFilterTests
{
    [Fact]
    public void BuildOwnerClusters_NoIgnore_ClustersAllTg4GroupsWithTwoReps()
    {
        var service = Service();

        var clusters = service.BuildOwnerClusters(Groupings());

        Assert.Equal(new[] { "Foo", "Medarbej" }, clusters.Select(c => c.ClusterKey).OrderBy(x => x));
    }

    [Fact]
    public void BuildOwnerClusters_IgnoredTg4_DoesNotCluster()
    {
        var service = Service();

        var clusters = service.BuildOwnerClusters(Groupings(), ignoredGroups: ["Medarbej"]);

        // "Medarbej" excluded; "Foo" still forms a cluster.
        Assert.Equal(new[] { "Foo" }, clusters.Select(c => c.ClusterKey));
    }

    [Fact]
    public void BuildOwnerClusters_EmptyIgnore_BehavesLikeNoIgnore()
    {
        var service = Service();

        var clusters = service.BuildOwnerClusters(Groupings(), ignoredGroups: []);

        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void BuildOwnerClusters_IgnoredTg4_IsCaseInsensitive()
    {
        var service = Service();

        // Profile-supplied casing differs from the Xellent GroupNum ("Medarbej").
        var clusters = service.BuildOwnerClusters(Groupings(), ignoredGroups: ["MEDARBEJ"]);

        Assert.Equal(new[] { "Foo" }, clusters.Select(c => c.ClusterKey));
    }

    [Fact]
    public void BuildOwnerClusters_Tg4DifferingOnlyByCase_ClustersTogether()
    {
        var service = Service();

        // "Foo" and "foo" are the same kundegruppe → one cluster of two reps.
        var groupings = new List<AccountGrouping>
        {
            new("200", ["200"], "Foo", "Foo Group", "33333333", new Dictionary<string, string>()),
            new("201", ["201"], "foo", "Foo Group", "44444444", new Dictionary<string, string>()),
        };

        var clusters = service.BuildOwnerClusters(groupings);

        Assert.Single(clusters);
        Assert.Equal(new[] { "200", "201" }, clusters[0].MemberAccountnums);
    }

    // Two reps under "Medarbej", two under "Foo" → each TG4 is cluster-eligible (≥2 reps).
    private static List<AccountGrouping> Groupings() =>
    [
        new AccountGrouping("100", ["100"], "Medarbej", "Medarbejdere", "11111111", new Dictionary<string, string>()),
        new AccountGrouping("101", ["101"], "Medarbej", "Medarbejdere", "22222222", new Dictionary<string, string>()),
        new AccountGrouping("200", ["200"], "Foo", "Foo Group", "33333333", new Dictionary<string, string>()),
        new AccountGrouping("201", ["201"], "Foo", "Foo Group", "44444444", new Dictionary<string, string>()),
    ];

    private static SettlMappingService Service() =>
        new(EmptyLoad(), new WarningCollector(), new DateTime(2026, 1, 1));

    private static XellentLoadResult EmptyLoad() =>
        new(
            Customers: Array.Empty<Customer>(),
            MeteringPoints: Array.Empty<DataMigration.Domain.MeteringPoints.MeteringPoint>(),
            OwnerClusters: Array.Empty<DataMigration.Domain.Customers.OwnerCluster>(),
            JointInvoiceGroups: Array.Empty<JointInvoiceGroup>(),
            SnapshotDate: new DateOnly(2026, 1, 1),
            MpOrderIdentifiers: new Dictionary<string, string?>(),
            AccountGroupings: Array.Empty<DataMigration.Domain.Xellent.AccountGrouping>(),
            FingerprintByRepresentative: new Dictionary<string, AccountFingerprint>());
}
