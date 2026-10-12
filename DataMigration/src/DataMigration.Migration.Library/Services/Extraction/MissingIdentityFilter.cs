using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Runners;

namespace DataMigration.Migration.Services.Extraction;

/// <summary>
/// Result of <see cref="MissingIdentityFilter.Apply"/>. <see cref="LoadResult"/> is the
/// extract with the identity-less customers removed (fictive-CPR customers stay in);
/// <see cref="Excluded"/> is one row per removed customer, for the report section.
/// <see cref="FictiveCprAssigned"/> is one row per customer that was kept and given the
/// fictive CPR instead of being excluded.
/// </summary>
internal sealed record MissingIdentityFilterResult(
    XellentLoadResult LoadResult,
    IReadOnlyList<MissingIdentityCustomerInfo> Excluded,
    IReadOnlyList<MissingIdentityCustomerInfo> FictiveCprAssigned);

/// <summary>
/// Removes customers that have neither CVR nor CPR on disponent 1 from the extract, so the
/// rest of the pipeline — payers, metering points, links, groupings, owner clusters, every
/// counter and every report list — never sees them. Without a CVR or a CPR the customer
/// reaches Settl with an empty identification, which is not a customer anyone can bill.
///
/// <para>A blank CVR is also what an unusable one looks like: the mapper drops a CVR that
/// isn't 8 digits. Those customers need the same attention, and get it here.</para>
///
/// <para>Disponent 2 is carried out for the report to look at, but never decides: the
/// identification Settl gets is built from disponent 1.</para>
///
/// <para>Safe with respect to the representative rule: merging requires a CVR, so an
/// account without one is always alone in its group — removing it can never take another
/// account with it. Pure function, no I/O; the caller logs.</para>
///
/// <para><see cref="FictiveCprForNothingCause"/>: El-ekstern only, and only for
/// <see cref="MissingIdentityCause.Nothing"/> — a customer with genuinely nothing in Xellent
/// (no CVR, no CPR, nothing in the alternative number either). Set <c>assignFictiveCprForNothingCause</c>
/// to keep those customers instead of excluding them: they get stamped with the fictive CPR and
/// migrate as a Person. The other causes (a real CVR/CPR sitting unread in the alternative
/// number, or a malformed field) keep being excluded — they carry a real, fixable value and a
/// fictive CPR would bury it instead of surfacing it.</para>
/// </summary>
internal static class MissingIdentityFilter
{
    /// <summary>Stand-in CPR for El-ekstern customers with no identification anywhere in
    /// Xellent. Business decision, not a real CPR — see <see cref="MissingIdentityFilter"/>.</summary>
    public const string FictiveCprForNothingCause = "1111110000";

    public static MissingIdentityFilterResult Apply(
        XellentLoadResult loadResult, bool assignFictiveCprForNothingCause = false)
    {
        var identityLessCustomers = loadResult.Customers.Where(HasNoIdentity).ToList();
        if (identityLessCustomers.Count == 0)
            return new MissingIdentityFilterResult(
                loadResult, Array.Empty<MissingIdentityCustomerInfo>(), Array.Empty<MissingIdentityCustomerInfo>());

        var excluded = new List<MissingIdentityCustomerInfo>();
        var fictiveCprAssigned = new List<MissingIdentityCustomerInfo>();
        var patchedByAccount = new Dictionary<string, Customer>(StringComparer.Ordinal);

        foreach (var customer in identityLessCustomers)
        {
            var info = Describe(customer);
            if (assignFictiveCprForNothingCause && info.Cause == MissingIdentityCause.Nothing)
            {
                fictiveCprAssigned.Add(info);
                patchedByAccount[customer.AccountNumber] = customer with { Cpr = FictiveCprForNothingCause };
            }
            else
            {
                excluded.Add(info);
            }
        }

        if (excluded.Count == 0 && fictiveCprAssigned.Count == 0)
            return new MissingIdentityFilterResult(
                loadResult, Array.Empty<MissingIdentityCustomerInfo>(), Array.Empty<MissingIdentityCustomerInfo>());

        var excludedAccounts = excluded.Select(e => e.AccountNumber).ToHashSet(StringComparer.Ordinal);
        var keptCustomers = loadResult.Customers
            .Where(c => !excludedAccounts.Contains(c.AccountNumber))
            .Select(c => patchedByAccount.TryGetValue(c.AccountNumber, out var patched) ? patched : c)
            .ToList();

        // Same slicing as CustomerFilter: the rep-keyed and MP-keyed siblings have to follow
        // the customer list, or the report renders rows for customers that aren't there.
        var keptReps = keptCustomers.Select(c => c.AccountNumber).ToHashSet(StringComparer.Ordinal);
        var keptMpIds = keptCustomers
            .SelectMany(c => c.Contracts)
            .SelectMany(c => c.MeteringPoints)
            .Select(a => a.MeteringPointId)
            .ToHashSet(StringComparer.Ordinal);

        var sliced = loadResult with
        {
            Customers = keptCustomers,
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
        };

        return new MissingIdentityFilterResult(sliced, excluded, fictiveCprAssigned);
    }

    private static bool HasNoIdentity(Customer customer) =>
        string.IsNullOrWhiteSpace(customer.Cvr) && string.IsNullOrWhiteSpace(customer.Cpr);

    private static MissingIdentityCustomerInfo Describe(Customer customer)
    {
        var raw = customer.RawIdentification;
        var cause = MissingIdentityClassifier.Classify(
            raw.Cvr, raw.Cpr, raw.AlternativeNumber, raw.AlternativeNumberCode);

        return new MissingIdentityCustomerInfo(
            AccountNumber: customer.AccountNumber,
            Name: customer.Name,
            AdditionalName: customer.AdditionalName,
            StreetName: customer.Address.StreetName,
            PostalCode: customer.Address.PostalCode,
            City: customer.Address.City,
            SecondaryCvr: customer.Cvr2,
            SecondaryCpr: customer.Cpr2,
            MeteringPointCount: customer.Contracts
                .SelectMany(c => c.MeteringPoints)
                .Select(a => a.MeteringPointId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            RawCvr: raw.Cvr,
            RawCpr: raw.Cpr,
            AlternativeNumber: raw.AlternativeNumber,
            AlternativeNumberCode: raw.AlternativeNumberCode,
            Cause: cause,
            CauseText: MissingIdentityClassifier.Describe(cause));
    }
}
