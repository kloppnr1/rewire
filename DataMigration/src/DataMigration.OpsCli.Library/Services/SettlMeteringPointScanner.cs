using DataMigration.SettlApi.Clients;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Env-wide Settl metering point selection for the scanning report commands
/// (<c>report-meteringpoints</c>, <c>report-meter-info --scan</c>), following the same rule as
/// <c>reconcile</c>: paginate market-v3 and filter on the list itself.
///
/// <para>The billing-v1 list this replaced carried neither <c>ParentMeteringPoint</c> nor
/// <c>InDelivery</c>, so child MPs could only be dropped after fetching each MP, and MPs that had
/// left delivery were scanned as if they were still live. Market-v3 exposes both, so the population
/// is decided before any per-MP fetch.</para>
/// </summary>
public static class SettlMeteringPointScanner
{
    public sealed record ScanSelection(
        List<Guid> MeteringPointIds,
        int Scanned,
        int SkippedChildren,
        int SkippedNotInDelivery);

    /// <summary>
    /// Returns the Settl ids to scan. Child MPs are Settl relations rather than customers of their
    /// own and are skipped unless <paramref name="includeChildren"/>; MPs not in delivery are skipped
    /// unless <paramref name="includeNotInDelivery"/> — those have left delivery since migration, so
    /// drift on them is usually historic rather than actionable.
    /// </summary>
    public static async Task<ScanSelection> SelectAsync(
        ISettlMeteringPointClient client,
        bool includeChildren,
        bool includeNotInDelivery,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        const int pageSize = 500;
        var ids = new List<Guid>();
        var skip = 0;
        var total = 0;
        var seen = 0;
        var skippedChildren = 0;
        var skippedNotInDelivery = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (items, count, err) = await client.FindMeteringPointsAsync(pageSize, skip, doCount: skip == 0, ct);
            if (err != null)
                throw new InvalidOperationException($"Settl FindMeteringPoints failed at skip {skip}: {err}");
            if (skip == 0 && count > 0) total = count;
            if (items.Count == 0) break;

            foreach (var mp in items)
            {
                seen++;
                if (!includeChildren && mp.ParentMeteringPoint?.Id is { } parentId && parentId != Guid.Empty)
                {
                    skippedChildren++;
                    continue;
                }
                if (!includeNotInDelivery && mp.InDelivery != true)
                {
                    skippedNotInDelivery++;
                    continue;
                }
                ids.Add(mp.Id);
            }

            progress?.Invoke($"\r  Loading metering points... {seen}{(total > 0 ? $"/{total}" : "")} (selected: {ids.Count})");

            skip += pageSize;
            if (items.Count < pageSize) break;
            if (total > 0 && skip >= total) break;
        }

        return new ScanSelection(ids, seen, skippedChildren, skippedNotInDelivery);
    }
}
