using DataMigration.Migration.Services.Extraction;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Migration.Services;

/// <summary>
/// Resolves the owner-clusters for a whole migration scope in one go, straight from Xellent.
///
/// <para>A whole-category run migrates in batches, and a batch that derives its own clusters can
/// only see the accounts it happens to hold. Newsec Dades — 222 accounts across many batches —
/// was then met as a new cluster every time and got an owner-customer of its own each time. The
/// cluster is a property of the scope, not of the batch, so it is resolved once here and handed
/// to every batch.</para>
///
/// <para>The rule is the same one <see cref="SettlMappingService.BuildOwnerClusters"/> applies to
/// a single-pass run: one cluster per customer group (TG4) holding two or more accounts, named
/// after the group's GROUPNAME, keyed by the group value. Groups in
/// <paramref name="ignoredGroups"/> never form a cluster. Accounts without a group are not
/// eligible.</para>
///
/// <para>For the batched path only, where representative merge is off and every account is its
/// own Settl customer — so the accounts passed in are the representatives. A merging run must
/// keep building its clusters from the merged groupings.</para>
///
/// <para>Owns the <see cref="XellentDbContext"/> lifecycle like
/// <see cref="ActiveAccountSelector"/>; callers pass a connection string.</para>
/// </summary>
public static class OwnerClusterSelector
{
    private const int SqlCompatibilityLevel = 110;
    private const int CommandTimeoutSeconds = 300;

    /// <summary>
    /// The account list goes back to the database as a predicate, so it is chunked: tens of
    /// thousands of values exceed SQL Server's parameter budget and the planner gives up. An
    /// account belongs to exactly one group, so concatenating the chunks is exact.
    /// </summary>
    private const int ChunkSize = 2000;

    public static async Task<List<OwnerCluster>> SelectAsync(
        string connectionString,
        IReadOnlyCollection<string> accounts,
        string dataAreaId = "hol",
        IReadOnlyCollection<string>? ignoredGroups = null,
        CancellationToken cancellationToken = default)
    {
        if (accounts.Count == 0) return [];

        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseXellent(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                sql.CommandTimeout(CommandTimeoutSeconds);
            })
            .Options;

        await using var context = new XellentDbContext(dbOptions);

        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Cannot connect to Xellent database. Check VPN connection and that the " +
                "connection string is reachable from this machine.");
        }

        var rows = new List<(string Accountnum, string Tg4, string? GroupName)>();
        foreach (var chunk in accounts.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            var page = await (
                from cust in context.CustTables
                join grp in context.ExuGroupTables
                    on new { Groupnum = cust.ExuTechnicgroup4!, cust.Dataareaid }
                    equals new { grp.Groupnum, grp.Dataareaid } into grpJoin
                from grp in grpJoin.DefaultIfEmpty()
                where cust.Dataareaid == dataAreaId
                    && chunk.Contains(cust.Accountnum)
                    && cust.ExuTechnicgroup4 != null && cust.ExuTechnicgroup4 != ""
                select new
                {
                    cust.Accountnum,
                    Tg4 = cust.ExuTechnicgroup4!,
                    GroupName = grp != null ? grp.Groupname : null
                }
            ).ToListAsync(cancellationToken);

            rows.AddRange(page.Select(r => (r.Accountnum, r.Tg4, (string?)r.GroupName)));
        }

        return BuildClusters(rows, ignoredGroups);
    }

    /// <summary>
    /// The grouping itself, split out so it can be exercised without a database. Mirrors
    /// <see cref="SettlMappingService.BuildOwnerClusters"/> — same case-insensitive group match,
    /// same two-member minimum, same ordering — so the batched path and the single-pass path
    /// cluster identically.
    /// </summary>
    internal static List<OwnerCluster> BuildClusters(
        IEnumerable<(string Accountnum, string Tg4, string? GroupName)> rows,
        IReadOnlyCollection<string>? ignoredGroups)
    {
        var ignored = ignoredGroups is { Count: > 0 }
            ? ignoredGroups.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        return rows
            .Where(r => ignored == null || !ignored.Contains(r.Tg4))
            .GroupBy(r => r.Tg4, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.Select(r => r.Accountnum)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(a => a, StringComparer.Ordinal)
                    .ToList();
                var groupName = g.Select(r => r.GroupName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                return new OwnerCluster(
                    ClusterKey: g.Key,
                    OwnerName: groupName ?? g.Key,
                    MemberAccountnums: members,
                    LowestAccountnum: members[0]);
            })
            .Where(c => c.MemberAccountnums.Count >= 2)
            .OrderBy(c => c.ClusterKey, StringComparer.Ordinal)
            .ToList();
    }
}
