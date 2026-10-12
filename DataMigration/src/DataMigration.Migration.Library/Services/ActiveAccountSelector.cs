using DataMigration.Core;
using DataMigration.Domain.Xellent;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Migration.Services;

/// <summary>
/// Resolves the migration scope straight from Xellent instead of a hand-maintained account
/// list. Migration terminates the Xellent contract, so every account that still holds a
/// non-terminated contract in the delivery category is by definition not yet migrated —
/// which makes "not terminated" the remaining population.
///
/// <para>Owns the <see cref="XellentDbContext"/> lifecycle the same way
/// <c>XellentDomainLoader</c> does; callers pass a connection string and get accounts back.</para>
///
/// <para><b>The restore window is honoured here too, and it has to be.</b> The scope is resolved
/// before extraction and handed to the run as <c>CreateOptions.SelectedAccounts</c>, which is applied
/// to the extraction's <i>output</i>. The 2026-08-31 batch end-dated the contract headers, so without
/// the same exemption the extraction restores those contracts and this pre-filter cuts them straight
/// back out. Measured on AXDB50, El-ekstern/hol: 60 accounts pass the plain predicate at the
/// 2026-09-01 cutover, against ~44.900 that the batch closed.</para>
/// </summary>
public static class ActiveAccountSelector
{
    private const int SqlCompatibilityLevel = 110;

    // Higher than the loader's 120s: this scans the full contract/part join without an
    // account pre-filter, which is the widest query the pipeline issues.
    private const int CommandTimeoutSeconds = 300;

    /// <summary>
    /// Contract numbers per query when the exempted set goes back to the database. ~48.000 values in
    /// one predicate is what SQL Server refuses to plan ("query processor ran out of internal
    /// resources"); chunked, each query seeks the clustered key (DATAAREAID, CONTRACTNUM). Same size
    /// the extraction's own chunked reads use.
    /// </summary>
    private const int ChunkSize = 2000;

    /// <summary>
    /// Every account in <paramref name="dataAreaId"/> holding a contract with a part in
    /// <paramref name="deliveryCategory"/> that is either open-ended
    /// (<see cref="MigrationConstants.EndDateNull"/>) or still running at
    /// <paramref name="asOf"/>. Ordered and de-duplicated.
    ///
    /// <para>With <paramref name="restorePartsChangedFrom"/>/<paramref name="restorePartsChangedBefore"/>
    /// set — the same half-open window <c>[from, before)</c> the extraction restores parts over — the
    /// accounts behind contracts that batch end-dated are added back, provided the contract still holds
    /// a part in <paramref name="deliveryCategory"/>. Without the window nothing changes: the same
    /// single query, the same SQL, the same result.</para>
    /// </summary>
    /// <param name="warnings">Where the exemption reports what it found ("the batch stamped
    /// 2026-08-31 on N contract headers"). Pass the run's collector so the operator can see the
    /// restore fire; omitted, the lines are discarded.</param>
    public static async Task<string[]> SelectAsync(
        string connectionString,
        string deliveryCategory,
        DateTime asOf,
        string dataAreaId = "hol",
        CancellationToken cancellationToken = default,
        DateTime? restorePartsChangedFrom = null,
        DateTime? restorePartsChangedBefore = null,
        IWarningCollector? warnings = null)
    {
        var window = ResolveWindow(restorePartsChangedFrom, restorePartsChangedBefore);

        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql =>
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

        return await SelectAsync(
            context, deliveryCategory, asOf, dataAreaId, window, warnings, cancellationToken);
    }

    /// <summary>
    /// The selection itself, against a context the caller owns — so it can be exercised without a
    /// SQL Server. The public entry point is the same query with the connection handling around it.
    /// </summary>
    internal static async Task<string[]> SelectAsync(
        XellentDbContext context,
        string deliveryCategory,
        DateTime asOf,
        string dataAreaId,
        (DateTime From, DateTime Before)? restoreWindow,
        IWarningCollector? warnings,
        CancellationToken cancellationToken = default)
    {
        var endDateNull = MigrationConstants.EndDateNull;

        var active = await (
            from contract in context.ExuContractTables
            join part in context.ExuContractPartTables
                on new { contract.Contractnum, contract.Dataareaid }
                equals new { part.Contractnum, part.Dataareaid }
            where contract.Dataareaid == dataAreaId
                && part.Deliverycategory == deliveryCategory
                && (contract.Contractenddate == endDateNull || contract.Contractenddate >= asOf)
            select contract.Custaccount)
            .Distinct()
            .OrderBy(account => account)
            .ToArrayAsync(cancellationToken);

        if (restoreWindow is not { } window) return active;

        var exemption = await ContractHeaderExemption.DeriveAsync(
            context, warnings ?? new WarningCollector(), window.From, window.Before, dataAreaId,
            cancellationToken);

        if (exemption is null || exemption.ContractNumbers.Count == 0) return active;

        var restored = await AccountsBehindExemptedContractsAsync(
            context, exemption, deliveryCategory, dataAreaId, cancellationToken);

        warnings?.AddInfo(
            $"All-active scope: {active.Length} account(s) active on their own merits, " +
            $"{restored.Count} further account(s) let back in by the contract-header exemption " +
            $"(of {exemption.ContractNumbersByAccount.Count} exempted account(s), the rest hold no " +
            $"'{deliveryCategory}' part) [Xellent: EXU_CONTRACTTABLE + EXU_CONTRACTPARTTABLE]");

        if (restored.Count == 0) return active;

        return active
            .Concat(restored)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(account => account, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The accounts behind the exempted contracts that still hold a part in
    /// <paramref name="deliveryCategory"/>.
    ///
    /// <para>The exemption is not category-filtered — it names every contract the batch end-dated,
    /// across all delivery categories — so the category rule has to be applied here or the scope would
    /// grow beyond the run's own commodity. The contract → account mapping comes from the header rows
    /// the derivation already read, so this is one query on the part table and no join.</para>
    /// </summary>
    private static async Task<List<string>> AccountsBehindExemptedContractsAsync(
        XellentDbContext context,
        ContractHeaderExemption exemption,
        string deliveryCategory,
        string dataAreaId,
        CancellationToken cancellationToken)
    {
        var accountByContract = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (account, contracts) in exemption.ContractNumbersByAccount)
            foreach (var contractNum in contracts)
                accountByContract[contractNum] = account;

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in exemption.ContractNumbers.Chunk(ChunkSize))
        {
            var withPart = await context.ExuContractPartTables
                .Where(p => p.Dataareaid == dataAreaId
                    && chunk.Contains(p.Contractnum)
                    && p.Deliverycategory == deliveryCategory)
                .Select(p => p.Contractnum)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var contractNum in withPart)
                if (accountByContract.TryGetValue(contractNum, out var account))
                    accounts.Add(account);
        }

        return [.. accounts];
    }

    /// <summary>
    /// Both bounds or neither. A half-configured window has no safe reading — the same rule
    /// <c>MapperOptions.RestorePartsWindow</c> applies, so the scope and the extraction cannot end up
    /// disagreeing about whether the restore is on.
    /// </summary>
    private static (DateTime From, DateTime Before)? ResolveWindow(DateTime? from, DateTime? before)
    {
        if (from is null && before is null) return null;
        if (from is not { } f || before is not { } b)
            throw new InvalidOperationException(
                "RestorePartsChangedFrom and RestorePartsChangedBefore must be set together: " +
                "the contract-part restore runs over a bounded window, never open-ended.");
        if (f >= b)
            throw new InvalidOperationException(
                $"Restore window is empty or inverted: from {f:O} is not before {b:O}.");
        return (f, b);
    }
}
