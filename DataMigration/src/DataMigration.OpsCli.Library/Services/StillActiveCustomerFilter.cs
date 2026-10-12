using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Determines which input accounts are still active in Xellent on a given date
/// (typically <c>ChangeOfSupplierDate</c>) by reusing the same load path as
/// <c>create</c>: <see cref="IXellentDomainLoader.LoadAsync"/> with
/// <c>ReferenceDate = checkDate</c> returns customers with at least one active
/// or future contract on that date. Their representative AccountNumbers form
/// the "still in Xellent" set — exports/fix's targeting Settl should skip them
/// because their MPs have not been migrated yet (or remain dual-active and
/// would be hit by a future <c>create</c> run).
/// The <c>disableMerge</c> parameter must match the merge setting of the caller's own load:
/// the returned set is keyed by AccountNumber, which is a representative account when
/// merging is on and the account itself when it is off. Passing the two differently makes
/// the skip set miss accounts silently. Defaults to false, so existing callers merge as before.
/// </summary>
public static class StillActiveCustomerFilter
{
    public static async Task<HashSet<string>> ResolveAsync(
        IXellentDomainLoader loader,
        string[] accounts,
        DateTime checkDate,
        Commodity commodity,
        string dataAreaId,
        bool legacyTg4Merge,
        WarningCollector warnings,
        CancellationToken ct = default,
        bool disableMerge = false)
    {
        var load = await LoadAtAsync(loader, accounts, checkDate, commodity, dataAreaId, legacyTg4Merge, warnings, ct, disableMerge);
        return load.Customers
            .Select(c => c.AccountNumber)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the union of MeteringPointAssignment ids belonging to customers that
    /// are still active on <paramref name="checkDate"/>. Used by MP-level exports
    /// (EAV, Exemption) to drop rows whose owner is not ready for Settl.
    /// </summary>
    public static async Task<HashSet<string>> ResolveMpIdsAsync(
        IXellentDomainLoader loader,
        string[] accounts,
        DateTime checkDate,
        Commodity commodity,
        string dataAreaId,
        bool legacyTg4Merge,
        WarningCollector warnings,
        CancellationToken ct = default,
        bool disableMerge = false)
    {
        var load = await LoadAtAsync(loader, accounts, checkDate, commodity, dataAreaId, legacyTg4Merge, warnings, ct, disableMerge);
        return load.Customers
            .SelectMany(c => c.Contracts)
            .SelectMany(c => c.MeteringPoints)
            .Select(mpa => mpa.MeteringPointId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Task<XellentLoadResult> LoadAtAsync(
        IXellentDomainLoader loader, string[] accounts, DateTime checkDate,
        Commodity commodity, string dataAreaId, bool legacyTg4Merge,
        WarningCollector warnings, CancellationToken ct, bool disableMerge)
    {
        var opts = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(checkDate),
            Commodity: commodity,
            LegacyTg4Merge: legacyTg4Merge,
            DisableRepresentativeMerge: disableMerge);
        return loader.LoadAsync(accounts, opts, dataAreaId, warnings, ct);
    }
}
