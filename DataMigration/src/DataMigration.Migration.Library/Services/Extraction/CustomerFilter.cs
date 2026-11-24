using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services;

namespace DataMigration.Migration.Services.Extraction;

/// <summary>
/// Result of <see cref="CustomerFilter.Apply"/>. <see cref="LoadResult"/> is the sliced
/// (or unchanged) input. <see cref="OriginalCount"/> + <see cref="FilteredCount"/> are
/// for diagnostic logging. <see cref="MissingAccounts"/> are SelectedAccounts that didn't
/// match any rep in the extract — caller decides whether to warn.
/// </summary>
internal sealed record CustomerFilterResult(
    XellentLoadResult LoadResult,
    int OriginalCount,
    int FilteredCount,
    IReadOnlyList<string> MissingAccounts)
{
    public bool WasFiltered => FilteredCount < OriginalCount || MissingAccounts.Count > 0;
}

/// <summary>
/// Slices <see cref="XellentLoadResult"/> based on either explicit account-selection
/// (live-run subset) or first-N limit (dry-run smoke test). Pure function — no I/O, no
/// logging. Caller wraps with log-line emission.
///
/// <para>The full Xellent extract is preserved by always running upstream; this helper
/// only trims the customer-projection plus rep-keyed sibling collections so the dashboard
/// view (groupings, fingerprint splits) reflects the limited subset.</para>
/// </summary>
internal static class CustomerFilter
{
    public static CustomerFilterResult Apply(
        XellentLoadResult loadResult,
        int? accountLimit,
        string[]? selectedAccounts,
        string? env = null)
    {
        var total = loadResult.Customers.Count;

        IReadOnlyList<Customer> filteredCustomers;
        IReadOnlyList<string> missing = Array.Empty<string>();

        if (selectedAccounts is { Length: > 0 } selected)
        {
            // The dashboard list renders the env-padded CustomerNumber (e.g. production
            // pad-9: raw 43049 → 943049), so a selection may arrive in padded form while
            // the extract holds the raw Accountnum. Match a customer if the selection set
            // contains EITHER its raw Accountnum or its pad-9 form for the target env.
            var set = new HashSet<string>(selected, StringComparer.Ordinal);
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<Customer>();
            foreach (var c in loadResult.Customers)
            {
                string? hit = null;
                if (set.Contains(c.AccountNumber))
                    hit = c.AccountNumber;
                else if (TryPadForEnv(c.AccountNumber, env, out var padded) && set.Contains(padded))
                    hit = padded;

                if (hit != null)
                {
                    kept.Add(c);
                    matched.Add(hit);
                }
            }
            filteredCustomers = kept;
            missing = set
                .Except(matched, StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
        }
        else if (accountLimit is { } n && n > 0 && n < total)
        {
            filteredCustomers = loadResult.Customers
                .OrderBy(c => c.AccountNumber, StringComparer.Ordinal)
                .Take(n)
                .ToList();
        }
        else
        {
            return new CustomerFilterResult(loadResult, total, total, Array.Empty<string>());
        }

        // Slice rep-keyed sibling collections so dashboard's grouping view + FingerprintSplits
        // reflect the limited set (don't render reps that aren't in customer list).
        var keptReps = filteredCustomers.Select(c => c.AccountNumber).ToHashSet(StringComparer.Ordinal);
        var keptMpIds = filteredCustomers
            .SelectMany(c => c.Contracts)
            .SelectMany(c => c.MeteringPoints)
            .Select(a => a.MeteringPointId)
            .ToHashSet(StringComparer.Ordinal);
        // Ladestandernes konto-opslag er keyed på det RÅ accountnum ladestander-kontrakten hænger
        // under — og det kan være et medlem af en merge-gruppe, ikke repræsentanten. Skæres der
        // kun på repræsentanterne, forsvinder ladestandere hos konti der faktisk er med.
        var keptAccounts = new HashSet<string>(keptReps, StringComparer.Ordinal);
        foreach (var g in loadResult.AccountGroupings.Where(g => keptReps.Contains(g.RepresentativeAccount)))
            foreach (var member in g.MemberAccounts)
                keptAccounts.Add(member);

        var sliced = loadResult with
        {
            Customers = filteredCustomers,
            AccountGroupings = loadResult.AccountGroupings
                .Where(g => keptReps.Contains(g.RepresentativeAccount))
                .ToList(),
            FingerprintByRepresentative = loadResult.FingerprintByRepresentative
                .Where(kv => keptReps.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            MeteringPoints = loadResult.MeteringPoints
                .Where(mp => keptMpIds.Contains(mp.Identification))
                .ToList(),
            JointInvoiceGroups = loadResult.JointInvoiceGroups
                .Where(g => g.MemberMeteringPointIds.Any(id => keptMpIds.Contains(id)))
                .ToList(),
            MpOrderIdentifiers = loadResult.MpOrderIdentifiers
                .Where(kv => keptMpIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            EvChargersByMeteringPoint = loadResult.EvChargersByMeteringPoint
                .Where(kv => keptMpIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            EvChargersByAccount = loadResult.EvChargersByAccount
                .Where(kv => keptAccounts.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };

        return new CustomerFilterResult(sliced, total, filteredCustomers.Count, missing);
    }

    /// <summary>
    /// Pad-9-form for <paramref name="env"/> hvis den adskiller sig fra det rå Accountnum.
    /// Returnerer false (og <paramref name="padded"/> = input) for env uden normalisering eller
    /// hvis padding ikke er muligt (non-numerisk/over-length) — så filtrering aldrig kaster på
    /// et enkelt afvigende Accountnum; den fejl fanges i selve migrate-loopet.
    /// </summary>
    private static bool TryPadForEnv(string accountNumber, string? env, out string padded)
    {
        try
        {
            padded = CustomerNumberPolicy.PadForEnv(accountNumber, env, out var wasPadded);
            return wasPadded;
        }
        catch
        {
            padded = accountNumber;
            return false;
        }
    }
}
