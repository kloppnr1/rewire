using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Migration.Services;

/// <summary>
/// Reads the contract template (<c>ExuContractTable.Contracttemplatenum</c>) for the run's
/// contracts. The domain model the create path runs on does not carry it — a contract there knows
/// its number, product and payment term, not the template it was written from — and the template is
/// what separates two customers who hold the same product but belong on different Settl contracts.
///
/// <para>Only read when a run actually has template rules configured; see
/// <c>CreateOptions.ContractTemplatesByContractNum</c>. Two short columns over the run's contracts,
/// so a whole-category run pays a single scan and a profile run pays almost nothing.</para>
/// </summary>
public static class ContractTemplateLoader
{
    private const int SqlCompatibilityLevel = 110;
    private const int CommandTimeoutSeconds = 300;

    // SQL Server caps parameters at 2100; keep each IN-batch well under that.
    private const int AccountBatchSize = 1500;

    /// <summary>
    /// Contract number → contract template, for the given accounts — or for the whole data area when
    /// <paramref name="accounts"/> is null or empty, which is what a whole-category run needs.
    /// Contracts with no template are left out: absent and empty mean the same to the caller, and a
    /// blank key would never match a rule anyway.
    /// </summary>
    public static async Task<Dictionary<string, string>> LoadAsync(
        string connectionString,
        string dataAreaId,
        IReadOnlyCollection<string>? accounts,
        CancellationToken cancellationToken = default)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                sql.CommandTimeout(CommandTimeoutSeconds);
            })
            .Options;

        await using var context = new XellentDbContext(dbOptions);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (accounts is null || accounts.Count == 0)
        {
            await AddAsync(context.ExuContractTables.Where(c => c.Dataareaid == dataAreaId));
            return result;
        }

        var distinct = accounts.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).ToList();
        for (var offset = 0; offset < distinct.Count; offset += AccountBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = distinct.GetRange(offset, Math.Min(AccountBatchSize, distinct.Count - offset));
            await AddAsync(context.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId && batch.Contains(c.Custaccount)));
        }
        return result;

        async Task AddAsync(IQueryable<DataMigration.XellentData.Entities.ExuContractTable> query)
        {
            var rows = await query
                .Select(c => new { c.Contractnum, c.Contracttemplatenum })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Contracttemplatenum)) continue;
                result[row.Contractnum] = row.Contracttemplatenum.Trim();
            }
        }
    }
}
