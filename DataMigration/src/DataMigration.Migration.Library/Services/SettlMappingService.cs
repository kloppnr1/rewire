using DataMigration.Core;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services.Extraction;
using AccountGrouping = DataMigration.Migration.Runners.AccountGrouping;

namespace DataMigration.Migration.Services;

public partial class SettlMappingService
{
    private readonly XellentLoadResult _loadResult;
    private readonly IWarningCollector _warnings;
    private readonly DateTime _referenceDate;
    private readonly string _deliveryCategory;
    private readonly bool _isInstallationBased;
    public SettlMappingService(
        XellentLoadResult loadResult,
        IWarningCollector warnings,
        DateTime referenceDate,
        string? deliveryCategory = null)
    {
        _loadResult = loadResult;
        _warnings = warnings;
        _referenceDate = referenceDate;
        _deliveryCategory = deliveryCategory ?? "El-ekstern";
        _isInstallationBased = !MeteringPointStrategy.MeteringPointStrategyFactory.DelpointCategories.Contains(_deliveryCategory);
    }

    public Task<List<AccountGrouping>> ExtractAccountGroupingsAsync(string[] accountNumbers)
    {
        var result = _loadResult.AccountGroupings
            .Select(g => new AccountGrouping(
                g.RepresentativeAccount,
                g.MemberAccounts.ToList(),
                g.GroupNum,
                g.GroupName,
                g.Cvr,
                g.AccountNames.ToDictionary(kv => kv.Key, kv => kv.Value)))
            .ToList();
        return Task.FromResult(result);
    }

    public List<OwnerCluster> BuildOwnerClusters(
        List<AccountGrouping> accountGroupings,
        IReadOnlyCollection<string>? ignoredGroups = null)
    {
        // TG4-værdier i ignoredGroups danner aldrig en OwnerCluster (konti migreres standalone).
        // Mirror af pakkens BuildOwnerClustersAsync, så this-repo-stien og loadResult er konsistente.
        var ignored = ignoredGroups is { Count: > 0 }
            ? ignoredGroups.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        return accountGroupings
            .Where(g => !string.IsNullOrEmpty(g.GroupNum))
            .Where(g => ignored == null || !ignored.Contains(g.GroupNum!))
            .GroupBy(g => g.GroupNum!, StringComparer.OrdinalIgnoreCase)
            .Select(grp =>
            {
                var members = grp.Select(g => g.RepresentativeAccount)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                return (Tg4: grp.Key, Members: members, GroupName: grp.Select(g => g.GroupName).FirstOrDefault(n => !string.IsNullOrEmpty(n)));
            })
            .Where(x => x.Members.Count >= 2)
            .Select(x => new OwnerCluster(
                ClusterKey: x.Tg4,
                OwnerName: x.GroupName ?? x.Tg4,
                MemberAccountnums: x.Members,
                LowestAccountnum: x.Members[0]))
            .OrderBy(c => c.ClusterKey, StringComparer.Ordinal)
            .ToList();
    }

}
