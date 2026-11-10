using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Lets contract headers the 2026-08-31 batch end-dated back into a run, by name.
///
/// <para>The batch did not only rewrite contract parts — it also stamped an end date on the headers
/// those parts hang under. <c>EXU_CONTRACTTABLE</c> has no audit log, so the old header values cannot
/// be read back, and unlike the parts there is nothing to restore: the header end date is never
/// migrated. <see cref="DataMigration.Domain.Customers.Contract"/> carries no end date at all — the
/// value exists purely as an extraction filter. So the affected contracts do not need their old value
/// back; they need to stop being filtered out.</para>
///
/// <para><b>Why an allow-list and not a wider date.</b> Widening the header cutoff to the batch date
/// would admit every contract that ended on or after it, and a genuine termination on that date looks
/// exactly like the batch's. Membership here is evidenced instead of inferred: a contract qualifies
/// only if the part log itself carries a blank-user row for it inside the window, and its header
/// carries the end date the batch stamped. A contract that is not in the log is filtered exactly as it
/// is today. That is the whole difference — the date is a coincidence anyone can match, the log row is
/// proof this batch touched this contract.</para>
///
/// <para>Both halves are required. The log row alone would exempt a contract the batch touched but
/// which someone had legitimately terminated months earlier; the date alone would exempt genuine
/// terminations that happen to fall on the same day.</para>
///
/// <para><b>The membership is carried as data, and that is not a detail.</b> Expressing it in SQL as
/// an <c>EXISTS</c> against the log, OR-ed with the date rule, is correct and unusably slow:
/// <c>EXU_CONTRACTTABLE</c> has no index touching <c>CONTRACTENDDATE</c>, and under an <c>OR</c> the
/// optimizer cannot combine a sargable range with a correlated subquery — it applies the subquery per
/// header row. Measured on AXDB50: 4,2 s and a worktable with 276.700 scans against 251 ms for the
/// same subquery once a date equality has narrowed it. The set is therefore resolved once, up front,
/// and pushed into each query as a short list of contract numbers that seeks the clustered key.</para>
///
/// <para><b>Public because the header rule has one caller outside the extraction.</b> A run's scope is
/// resolved before extraction starts — by <c>ActiveAccountSelector</c> in the migration consumer — with
/// its own <c>CONTRACTENDDATE</c> predicate. That pre-filter is applied to the extraction's output, so
/// without the same exemption it silently cuts back everything the restore just let in. Deriving the
/// set there uses this same type rather than a second copy of the rule.</para>
/// </summary>
public sealed record ContractHeaderExemption(
    DateTime BatchEndDate,
    IReadOnlySet<string> ContractNumbers,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ContractNumbersByAccount)
{
    /// <summary>
    /// Works out the end date the batch stamped on the headers, and the contracts that carry it,
    /// from the data rather than from a constant. Returns null when the restore is off, or when no
    /// contract named by the window carries an end date at all — in which case nothing is exempted.
    /// </summary>
    public static Task<ContractHeaderExemption?> DeriveAsync(
        XellentDbContext context,
        IWarningCollector warnings,
        MapperOptions options,
        string dataAreaId,
        CancellationToken cancellationToken = default)
        => options.RestorePartsWindow is { } window
            ? DeriveAsync(context, warnings, window.From, window.Before, dataAreaId, cancellationToken)
            : Task.FromResult<ContractHeaderExemption?>(null);

    /// <summary>
    /// The same derivation, for a caller that holds the half-open window <c>[from, before)</c> but no
    /// <see cref="MapperOptions"/> — the scope resolver runs before a mapper exists. This is the one
    /// implementation of the rule; the <see cref="MapperOptions"/> overload only unpacks the window.
    ///
    /// <para>Two queries, both of them seeks. The log gives the contract numbers it touched inside
    /// the window; those numbers then read their own header rows through the clustered key
    /// (<c>DATAAREAID, CONTRACTNUM</c>), chunked. The header table is never scanned and its
    /// unindexed end-date column is never used as a filter — only read back and counted.</para>
    ///
    /// <para>The stamp is derived because the caller configuring the window knows when the batch
    /// ran, not what it wrote. The distribution behind the choice is reported as an info line, so a
    /// run that picked the wrong date can be seen to have done so.</para>
    /// </summary>
    public static async Task<ContractHeaderExemption?> DeriveAsync(
        XellentDbContext context,
        IWarningCollector warnings,
        DateTime from,
        DateTime before,
        string dataAreaId,
        CancellationToken cancellationToken = default)
    {
        if (from >= before)
            throw new InvalidOperationException(
                $"Restore window is empty or inverted: from {from:O} is not before {before:O}.");

        var endDateNull = MigrationConstants.EndDateNull;

        // Every contract the window's blank-user rows name. Named-user rows are excluded for the
        // same reason as in the part restore: a person's decision is never undone.
        var touched = await context.ExuContractPartLogs
            .Where(l => l.Dataareaid == dataAreaId
                && l.Logdatetime >= from
                && l.Logdatetime < before
                && (l.Logemplid == null || l.Logemplid!.Trim() == ""))
            .Select(l => l.Contractnum)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (touched.Count == 0)
        {
            warnings.AddInfo(
                "Contract-header exemption: the log window names no contract, so no header is " +
                "exempted [Xellent: EXU_CONTRACTPARTLOG]");
            return null;
        }

        // Their header rows, read through the clustered key rather than filtered on the end date.
        var headers = new List<(string ContractNum, string Account, DateTime EndDate)>();
        foreach (var chunk in touched.Chunk(ChunkSize))
        {
            var page = await context.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId
                    && chunk.Contains(c.Contractnum)
                    && c.Contractenddate != endDateNull)
                .Select(c => new { c.Contractnum, c.Custaccount, c.Contractenddate })
                .ToListAsync(cancellationToken);
            headers.AddRange(page.Select(h => (h.Contractnum, h.Custaccount, h.Contractenddate)));
        }

        if (headers.Count == 0)
        {
            warnings.AddInfo(
                "Contract-header exemption: no end-dated contract header is named by the log window, " +
                "so no header is exempted [Xellent: EXU_CONTRACTTABLE + EXU_CONTRACTPARTLOG]");
            return null;
        }

        var byEndDate = headers
            .GroupBy(h => h.EndDate)
            .Select(g => new { EndDate = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.EndDate)
            .ToList();

        var stamp = byEndDate[0];
        var affected = headers.Where(h => h.EndDate == stamp.EndDate).ToList();

        warnings.AddInfo(
            $"Contract-header exemption: the batch stamped {stamp.EndDate:yyyy-MM-dd} on " +
            $"{stamp.Count} contract header(s) named by the log window" +
            (byEndDate.Count > 1
                ? "; other end dates seen on named contracts: " +
                  string.Join(", ", byEndDate.Skip(1).Take(4).Select(c => $"{c.EndDate:yyyy-MM-dd} ({c.Count})"))
                : "") +
            " [Xellent: EXU_CONTRACTTABLE + EXU_CONTRACTPARTLOG]");

        return new ContractHeaderExemption(
            stamp.EndDate,
            affected.Select(h => h.ContractNum).ToHashSet(StringComparer.Ordinal),
            affected
                .GroupBy(h => h.Account, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g.Select(h => h.ContractNum).Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal));
    }

    /// <summary>
    /// The exempted contracts among <paramref name="contractNums"/> — for a query already chunked on
    /// contract numbers. Null when none, so the caller's predicate stays the plain one.
    /// </summary>
    internal string[]? ForContracts(IEnumerable<string>? contractNums)
    {
        if (contractNums is null) return Materialise(ContractNumbers);
        var hit = contractNums.Where(ContractNumbers.Contains).Distinct(StringComparer.Ordinal).ToArray();
        return hit.Length == 0 ? null : hit;
    }

    /// <summary>
    /// The exempted contracts belonging to <paramref name="accounts"/> — for a query chunked on
    /// accounts rather than contract numbers, which is most of them. The account each exempted
    /// contract hangs under is already known from the header rows read during derivation, so this is
    /// a dictionary lookup and not another query. A chunk of 2.000 accounts yields a list of the same
    /// order of magnitude, which is what an <c>IN</c> list is for.
    /// </summary>
    internal string[]? ForAccounts(IEnumerable<string>? accounts)
    {
        if (accounts is null) return Materialise(ContractNumbers);

        List<string>? hit = null;
        foreach (var account in accounts)
        {
            if (!ContractNumbersByAccount.TryGetValue(account, out var contracts)) continue;
            hit ??= [];
            hit.AddRange(contracts);
        }
        return hit is null ? null : hit.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The whole set, for the rare caller that runs unscoped. Large by nature — a whole-data-area run
    /// has no chunk to intersect with, so there is nothing to narrow it against.
    /// </summary>
    private static string[]? Materialise(IReadOnlySet<string> set)
        => set.Count == 0 ? null : [.. set];
}
