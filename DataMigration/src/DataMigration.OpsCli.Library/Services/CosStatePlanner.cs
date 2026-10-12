using DataMigration.Domain.Customers;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// The date arithmetic and set algebra behind COS-driven state generation, kept free of database
/// and API calls so it can be tested on its own.
///
/// <para>Production was migrated in waves, so there is no single reference date that is right for
/// the whole population. Every customer gets its own change-of-supplier date, and everything that
/// depends on a date — which merge group the customer belonged to, how far back its invoice history
/// reaches — is derived from that date rather than from one flat value.</para>
/// </summary>
public static class CosStatePlanner
{
    /// <summary>
    /// True when a Settl metering point was actually migrated into delivery at some point.
    ///
    /// <para>Structural, not symptomatic: a metering point that is not in delivery, has no current
    /// or next delivery period and no completed one either has never been in delivery — it exists in
    /// Settl but no customer ever took supply through it, so it must not seed a state entry. The
    /// tempting shortcut is to test whether the derived COS equals today (that is what the loader
    /// falls back to when it has no delivery period at all), but that answer changes on any day a
    /// real migration goes live, and would then throw away a genuine wave.</para>
    /// </summary>
    public static bool WasEverInDelivery(MeteringPointInfoDto mp) =>
        mp.InDelivery == true
        || mp.CurrentOrNextDeliveryPeriod is not null
        || mp.MostRecentCompletedDeliveryPeriod is not null;

    /// <summary>
    /// Per-customer change-of-supplier date: the earliest COS across the customer's metering points.
    /// Mirrors what <c>reconcile</c> does per account — a customer migrated in one wave has one date,
    /// and a customer whose metering points arrived over several waves is pinned to the first, which
    /// is the state its grouping and its invoice history have to be read against.
    /// </summary>
    public static Dictionary<string, DateOnly> MinCosByCustomer(
        IEnumerable<(string CustomerNumber, DateOnly Cos)> pairs)
    {
        var result = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var (customerNumber, cos) in pairs)
        {
            if (string.IsNullOrEmpty(customerNumber)) continue;
            if (result.TryGetValue(customerNumber, out var existing) && existing <= cos) continue;
            result[customerNumber] = cos;
        }
        return result;
    }

    /// <summary>
    /// The set of Xellent accounts a merge resolution has to cover in order to be exact for the given
    /// seed accounts: the seeds themselves plus every account in the data area carrying one of their
    /// CVR numbers.
    ///
    /// <para>This is closure, not sampling. Both merge edges in <c>AccountRepresentativeResolver</c>
    /// are prefixed with the CVR and union is only ever applied inside a single edge bucket, so no
    /// account outside a seed's CVR class can end up in a group with that seed. An account with a
    /// blank CVR forms no edges at all and is its own singleton, so it needs no siblings pulled in.
    /// The whole data area — 415k accounts — is therefore unnecessary, and passing it is what made
    /// the flat run time out.</para>
    /// </summary>
    /// <param name="seedAccounts">Xellent accounts behind the Settl customers in this slice.</param>
    /// <param name="cvrByAccount">CVR per seed account; missing or blank means "no CVR".</param>
    /// <param name="accountsByCvr">Every account in the data area, indexed by CVR.</param>
    public static string[] BuildCvrClosure(
        IEnumerable<string> seedAccounts,
        IReadOnlyDictionary<string, string> cvrByAccount,
        IReadOnlyDictionary<string, List<string>> accountsByCvr)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seedAccounts)
        {
            closure.Add(seed);
            if (!cvrByAccount.TryGetValue(seed, out var cvr) || string.IsNullOrWhiteSpace(cvr)) continue;
            if (!accountsByCvr.TryGetValue(cvr.Trim(), out var siblings)) continue;
            foreach (var sibling in siblings) closure.Add(sibling);
        }
        return [.. closure];
    }

    /// <summary>
    /// One resolver call's worth of a COS slice: the accounts to resolve, and the seeds whose
    /// representatives that call answers for.
    /// </summary>
    public sealed record CvrPortion(string[] Accounts, string[] SeedAccounts);

    /// <summary>
    /// The CVR class an account belongs to for portioning purposes, or null when it can never merge
    /// with anybody and may be packed anywhere.
    ///
    /// <para>This deliberately mirrors <c>AccountRepresentativeResolver</c>'s own blank test, which is
    /// <c>IsNullOrEmpty</c> on the raw CVR — <b>not</b> <c>IsNullOrWhiteSpace</c>. An account whose CVR
    /// is nothing but spaces still forms edges in the resolver (keyed on those spaces), so two such
    /// accounts can merge with each other and must not be split across two calls. It gets its raw value
    /// as its class key. Only a genuinely empty CVR is a free singleton.</para>
    ///
    /// <para>Everything else is keyed on the trimmed CVR, which is a superset of the resolver's class:
    /// the resolver keys on the raw value, so <c>"12345678 "</c> and <c>"12345678"</c> are two classes
    /// to it and one class here. Over-grouping is always safe — it only keeps accounts together that
    /// did not have to be. Under-grouping is what would split a merge group.</para>
    /// </summary>
    private static string? PortionClassKey(string? rawCvr)
    {
        if (string.IsNullOrEmpty(rawCvr)) return null;
        var trimmed = rawCvr.Trim();
        return trimmed.Length > 0 ? trimmed : rawCvr;
    }

    /// <summary>
    /// Splits the CVR closure of a COS slice into resolver-sized portions, cut on whole CVR classes.
    ///
    /// <para>Why this is answer-preserving: both of the resolver's union edge keys are CVR-prefixed and
    /// union is only ever applied inside one edge bucket, so a merge group can never span two CVR
    /// classes (the same invariant <see cref="BuildCvrClosure"/> rests on). Every account of a class
    /// therefore travels together, and a seed — which belongs to exactly one class — gets exactly one
    /// portion that holds its whole group. Chunking on raw accounts would not be safe: a split group
    /// becomes two Settl customers, and losing a lower-numbered member silently changes the
    /// representative for the rest.</para>
    ///
    /// <para>A single class larger than <paramref name="portionSize"/> is kept whole and forms its own
    /// portion — correctness over the parameter budget, same trade-off as <c>ChunkByMergeGroup</c> in
    /// <c>XellentDomain</c>. The caller is expected to say so in the log.</para>
    ///
    /// <para>The flattened portions are exactly <see cref="BuildCvrClosure"/> over the same seeds, and
    /// the result does not depend on the order the seeds arrive in: classes are ordered by their lowest
    /// account number, so two runs of the same population produce the same state file.</para>
    /// </summary>
    /// <param name="portionSize">Soft cap on accounts per portion. Must be positive.</param>
    public static IReadOnlyList<CvrPortion> BuildCvrPortions(
        IEnumerable<string> seedAccounts,
        IReadOnlyDictionary<string, string> cvrByAccount,
        IReadOnlyDictionary<string, List<string>> accountsByCvr,
        int portionSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(portionSize, 1);

        // Class key → the accounts and the seeds it holds. A blank-CVR seed cannot merge with anybody,
        // so it is its own class keyed on itself and packs as filler.
        var accountsByClass = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var seedsByClass = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var seed in seedAccounts)
        {
            var classKey = cvrByAccount.TryGetValue(seed, out var cvr) ? PortionClassKey(cvr) : null;
            var key = classKey is null ? " single|" + seed : "cvr|" + classKey;

            if (!accountsByClass.TryGetValue(key, out var accounts))
                accountsByClass[key] = accounts = new HashSet<string>(StringComparer.Ordinal);
            if (!seedsByClass.TryGetValue(key, out var seeds))
                seedsByClass[key] = seeds = new HashSet<string>(StringComparer.Ordinal);

            accounts.Add(seed);
            seeds.Add(seed);

            // Same sibling pull as BuildCvrClosure, so the flattened portions match it account for
            // account. A whitespace-only CVR has no entry here (accountsByCvr is keyed on trimmed,
            // non-empty values), which is why such a seed only ever brings itself.
            if (classKey is not null && accountsByCvr.TryGetValue(classKey, out var siblings))
                foreach (var sibling in siblings) accounts.Add(sibling);
        }

        // Ordered by lowest account number: independent of the order the seeds arrived in, and of any
        // dictionary iteration order.
        var classes = accountsByClass
            .Select(kvp => new
            {
                Accounts = kvp.Value.OrderBy(a => a, StringComparer.Ordinal).ToArray(),
                Seeds = seedsByClass[kvp.Key].OrderBy(a => a, StringComparer.Ordinal).ToArray()
            })
            .OrderBy(c => c.Accounts[0], StringComparer.Ordinal)
            .ToArray();

        var portions = new List<CvrPortion>();
        var currentAccounts = new List<string>(portionSize);
        var currentSeeds = new List<string>();

        foreach (var cls in classes)
        {
            if (currentAccounts.Count > 0 && currentAccounts.Count + cls.Accounts.Length > portionSize)
            {
                portions.Add(new CvrPortion([.. currentAccounts], [.. currentSeeds]));
                currentAccounts.Clear();
                currentSeeds.Clear();
            }
            currentAccounts.AddRange(cls.Accounts);
            currentSeeds.AddRange(cls.Seeds);
        }
        if (currentAccounts.Count > 0)
            portions.Add(new CvrPortion([.. currentAccounts], [.. currentSeeds]));

        return portions;
    }

    /// <summary>
    /// The customer's own invoice floor: Jan 1 of (cos.Year - <paramref name="historyYears"/>), not
    /// a rolling window off a run-wide reference date. Calendar-year aligned, so the floor keeps every
    /// full year back to (and including) that January whole. An invoice dated exactly on the floor is
    /// inside the window. Defaults to <see cref="Invoice.ExportHistoryYears"/> when the caller doesn't
    /// have a configured value.
    /// </summary>
    public static DateTime HistoryCutoffFor(DateOnly cos, int historyYears = Invoice.ExportHistoryYears) =>
        InvoiceAccountFilter.GetHistoryCutoffDate(cos.ToDateTime(TimeOnly.MinValue), historyYears);

    /// <summary>True when an invoice falls inside the customer's own history window.</summary>
    public static bool IsWithinHistory(DateTime invoiceDate, DateOnly cos, int historyYears = Invoice.ExportHistoryYears) =>
        invoiceDate >= HistoryCutoffFor(cos, historyYears);

    /// <summary>
    /// Which source accounts contribute invoices.
    ///
    /// <para>Default: only accounts closed with a Slutafregning — an account whose most recent invoice
    /// is still an Aconto or a Settlement is a live customer, and its history is not final. With
    /// <paramref name="includeActive"/> that test is dropped entirely and every account holding an
    /// invoice is a candidate; the only filter left is the customer's own five-year window
    /// (<see cref="IsWithinHistory"/>). That is what a run covering the full population — including
    /// customers still being billed — needs.</para>
    /// </summary>
    public static HashSet<string> EligibleInvoiceAccounts(IEnumerable<Invoice> allInvoices, bool includeActive) =>
        includeActive
            ? [.. allInvoices.Select(i => i.AccountNumber)]
            : InvoiceAccountFilter.GetEligibleAccounts(allInvoices);

    /// <summary>
    /// Counts per COS date, most populous first, for the "what is this run about to do" log line.
    /// </summary>
    public static IReadOnlyList<(DateOnly Cos, int Count)> Histogram(IEnumerable<DateOnly> dates) =>
        [.. dates.GroupBy(d => d)
            .Select(g => (Cos: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Cos)];
}
