using DataMigration.Core;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>One Settl metering point with the change-of-supplier date it was migrated on.</summary>
public sealed record SettlMpWithCos(string Identification, Guid Id, DateOnly Cos, MeteringPointInfoDto Info);

/// <summary>
/// Loads every metering point in a Settl environment together with its change-of-supplier date.
///
/// <para>This is the entry point for anything that has to go from Settl back to Xellent: the COS
/// is what lets <see cref="MeteringPointAccountResolver"/> pin a metering point to the one account
/// that owned it at migration, rather than every account it ever touched. Shared by
/// <c>reconcile</c> and <c>payment-match</c> so there is a single definition of "which metering
/// points exist in this environment, and when did they change supplier".</para>
/// </summary>
public sealed class SettlMeteringPointCosLoader
{
    private static readonly TimeZoneInfo DkTz = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    private readonly ISettlMeteringPointClient _mpClient;
    private readonly Action<string> _log;

    public SettlMeteringPointCosLoader(ISettlMeteringPointClient mpClient, Action<string>? log = null)
    {
        _mpClient = mpClient;
        _log = log ?? (_ => { });
    }

    /// <summary>Reports (scanned, total) as pagination progresses.</summary>
    public Action<int, int>? OnProgress { get; set; }

    /// <param name="noDeliveryPeriodCos">
    /// When supplied, the COS given to <b>every</b> metering point that is not in delivery — its
    /// delivery-period fields are not consulted at all. Only meaningful together with
    /// <paramref name="includeNotInDelivery"/>. Leave it null to keep the behaviour <c>reconcile</c>
    /// and <c>payment-match</c> depend on: most recently completed period start, else today.
    /// <c>generate-state-from-settl</c> passes the planned change-of-supplier date, so the invoice
    /// window is placed on the date supply actually starts rather than on the day the command ran.
    /// </param>
    public async Task<Dictionary<string, SettlMpWithCos>> LoadAsync(
        CosPeriod cosPeriod,
        CancellationToken ct,
        bool includeNotInDelivery = false,
        CommodityFilter commodity = CommodityFilter.Electricity,
        DateOnly? noDeliveryPeriodCos = null)
    {
        var byMpId = new Dictionary<string, SettlMpWithCos>(StringComparer.Ordinal);

        // Electricity MPs — market-v3 pagination.
        if (commodity != CommodityFilter.Water)
            await LoadElectricityMpsAsync(byMpId, cosPeriod, ct, includeNotInDelivery, noDeliveryPeriodCos);

        // Water/heat MPs — commodity API.
        if (commodity != CommodityFilter.Electricity)
            await LoadWaterMpsAsync(byMpId, ct);

        return byMpId;
    }

    /// <summary>Resolve only explicitly selected electricity points, preserving the global loader's
    /// COS rules. A selected point cannot be silently dropped from a repair snapshot.</summary>
    internal async Task<Dictionary<string, SettlMpWithCos>> LoadScopedAsync(IEnumerable<string> identifications,
        CancellationToken ct, bool includeNotInDelivery = false, DateOnly? noDeliveryPeriodCos = null)
    {
        var targets = identifications.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, SettlMpWithCos>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        var activity = new InventoryActivity("scoped metering point");
        using var timer = new CaptureStage("Scoped Settl metering-point lookups", _log, () => activity.Summary);
        _log($"  Scoped Settl metering points: selectedMPs={targets.Length}; direct identification lookups, no global inventory.");
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var (point, error) = await activity.CallAsync("identificationLookup", Guid.Empty,
                () => _mpClient.SearchByIdentificationAsync(target, ct));
            if (error is not null || point is null || point.Id == Guid.Empty
                || !string.Equals(point.MeteringPointIdentification, target, StringComparison.Ordinal))
                throw new InvalidOperationException($"Settl scoped metering-point lookup failed or mismatched (target={target}, completed={result.Count}/{targets.Length}); capture remains incomplete.");
            if (!ids.Add(point.Id)) throw new InvalidOperationException($"Settl scoped metering points resolved to duplicate GUID {point.Id}; capture remains incomplete.");
            if (point.ParentMeteringPoint?.Id is { } parent && parent != Guid.Empty)
                throw new InvalidOperationException($"Selected Settl metering point is a child (target={target}, entityId={point.Id}); capture requires a parent point.");
            var (cos, source) = ResolveCos(point, includeNotInDelivery, noDeliveryPeriodCos);
            if (cos is null)
                throw new InvalidOperationException($"Selected Settl metering point has no usable delivery date (target={target}, entityId={point.Id}, reason={source}); capture remains incomplete.");
            result.Add(target, new(target, point.Id, cos.Value, point));
            activity.Finished();
            OnProgress?.Invoke(result.Count, targets.Length);
            _log($"  Scoped Settl metering points: resolved={result.Count}/{targets.Length}, elapsedSeconds={timer.Elapsed.TotalSeconds:F2}.");
        }
        timer.Complete($"selectedMPs={targets.Length}, identificationLookupCalls={targets.Length}. {activity.Summary}");
        return result;
    }

    /// <summary>Where a metering point's change-of-supplier date came from.</summary>
    private enum CosSource
    {
        /// <summary>Start of the current or next delivery period (the metering point is in delivery).</summary>
        CurrentOrNext,
        /// <summary>Start of the most recently completed delivery period.</summary>
        Completed,
        /// <summary>Not in delivery — the fallback date was used.</summary>
        NotInDeliveryFallback,
        /// <summary>In delivery, but Settl named no period start. Nothing to derive a COS from.</summary>
        Unresolved,
        /// <summary>Not in delivery and <c>includeNotInDelivery</c> is off — out of scope.</summary>
        OutOfScope,
    }

    /// <summary>
    /// COS for one metering point — the whole rule, in one place.
    ///
    /// <para>In delivery: the start of the current or next delivery period. Nothing else; if Settl
    /// names no period start there, that is missing data, not a not-in-delivery metering point, and it
    /// is reported as unresolved.</para>
    ///
    /// <para>Not in delivery, with a caller-supplied <paramref name="notInDeliveryCos"/>: that date,
    /// full stop. <c>MostRecentCompletedDeliveryPeriod</c> is deliberately not consulted — a
    /// production scan showed Settl fills it in for metering points whose change of supplier was
    /// scheduled and then cancelled, so it does not distinguish "delivered once" from "never
    /// delivered". That scan counted 61,513 electricity metering points in production — 11,708 in
    /// delivery, 49,805 not — of which only 625 not-in-delivery ones (1.25%) carry the field at all;
    /// telling those 625 apart would cost a detail call per metering point across all 61,513. All
    /// not-in-delivery metering points therefore share the supplied date.</para>
    ///
    /// <para>Not in delivery, with no supplied date (reconcile, payment-match): unchanged from before
    /// — the most recently completed period start if there is one, else today.</para>
    /// </summary>
    private static (DateOnly? Cos, CosSource Source) ResolveCos(
        MeteringPointInfoDto mp, bool includeNotInDelivery, DateOnly? notInDeliveryCos)
    {
        if (mp.InDelivery == true)
            return mp.CurrentOrNextDeliveryPeriod is { } p && p.Start != default
                ? (ToDkDate(p.Start), CosSource.CurrentOrNext)
                : (null, CosSource.Unresolved);

        if (!includeNotInDelivery) return (null, CosSource.OutOfScope);

        if (notInDeliveryCos is { } supplied) return (supplied, CosSource.NotInDeliveryFallback);

        return mp.MostRecentCompletedDeliveryPeriod is { } completed && completed.Start != default
            ? (ToDkDate(completed.Start), CosSource.Completed)
            : (DateOnly.FromDateTime(DateTime.Today), CosSource.NotInDeliveryFallback);
    }

    private static DateOnly ToDkDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(value.UtcDateTime, DkTz));

    private async Task LoadElectricityMpsAsync(
        Dictionary<string, SettlMpWithCos> byMpId,
        CosPeriod cosPeriod,
        CancellationToken ct,
        bool includeNotInDelivery,
        DateOnly? noDeliveryPeriodCos = null)
    {
        const int pageSize = 500;
        var skip = 0;
        var total = 0;
        var seen = 0;
        var skippedChildren = 0;
        var skippedOutOfWindow = 0;
        var skippedNoDeliveryPeriod = 0;
        var fromDeliveryPeriod = 0;
        var fromFallback = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (items, count, err) = await _mpClient.FindMeteringPointsAsync(pageSize, skip, doCount: skip == 0, ct);
            if (err != null)
                throw new InvalidOperationException($"Settl FindMeteringPoints failed: {err}");
            if (skip == 0 && count > 0) total = count;
            if (items.Count == 0) break;

            foreach (var mp in items)
            {
                seen++;
                // Skip child MPs regardless of delivery status.
                if (mp.ParentMeteringPoint?.Id is { } parentId && parentId != Guid.Empty)
                {
                    skippedChildren++;
                    continue;
                }
                var id = mp.MeteringPointIdentification;
                if (string.IsNullOrEmpty(id)) continue;

                var (cos, source) = ResolveCos(mp, includeNotInDelivery, noDeliveryPeriodCos);

                if (source == CosSource.OutOfScope) continue;      // not in delivery, flag not set
                if (source == CosSource.Unresolved) { skippedNoDeliveryPeriod++; continue; }

                if (source == CosSource.NotInDeliveryFallback)
                {
                    // Bypass the window filter — the window selects historical change-of-supply events;
                    // a metering point that is not in delivery has no such event and is always in scope
                    // when includeNotInDelivery is set.
                    byMpId[id] = new SettlMpWithCos(id, mp.Id, cos!.Value, mp);
                    fromFallback++;
                    continue;
                }

                // COS-window scoping happens here, at selection — the single point that decides which
                // MPs the run touches. Everything downstream (account COS, refDate, diff) is unchanged.
                if (!cosPeriod.Contains(cos!.Value)) { skippedOutOfWindow++; continue; }
                byMpId[id] = new SettlMpWithCos(id, mp.Id, cos.Value, mp);
                fromDeliveryPeriod++;
            }

            var denom = Math.Max(total, seen);
            OnProgress?.Invoke(Math.Min(seen, denom), denom);
            var keptLabel = includeNotInDelivery ? "parents kept" : "InDelivery parents kept";
            _log($"  ...scanned {seen}{(total > 0 ? $"/{total}" : "")} MPs ({keptLabel}: {byMpId.Count})");

            skip += pageSize;
            if (items.Count < pageSize) break;
            if (total > 0 && skip >= total) break;
        }
        if (includeNotInDelivery)
            _log($"  COS source split: {fromDeliveryPeriod} from a delivery period, " +
                 $"{fromFallback} not in delivery" +
                 (noDeliveryPeriodCos is { } f ? $" (fallback COS {f:yyyy-MM-dd})" : " (fallback COS today)"));
        if (skippedChildren > 0)
            _log($"  skipped {skippedChildren} child-MP(s) (Settl relations, not drift)");
        if (skippedNoDeliveryPeriod > 0)
            _log($"  skipped {skippedNoDeliveryPeriod} MP(s) with no resolvable COS (no delivery period)");
        if (skippedOutOfWindow > 0)
            _log($"  skipped {skippedOutOfWindow} MP(s) with COS outside the selection window");
    }

    private async Task LoadWaterMpsAsync(Dictionary<string, SettlMpWithCos> byMpId, CancellationToken ct)
    {
        var (items, err) = await _mpClient.FindWaterMeteringPointsAsync(ct);
        if (err != null)
            throw new InvalidOperationException($"Settl FindWaterMeteringPoints failed: {err}");

        // Water/heat MPs in the commodity API do NOT carry InDelivery or CurrentOrNextDeliveryPeriod —
        // those are electricity-market concepts. All MPs returned by FindWaterMeteringPointsAsync are
        // included; the COS is resolved from Xellent (ExuContractPart.Startdate) by the caller after
        // account resolution. Use today as a placeholder so the map is populated.
        var placeholder = DateOnly.FromDateTime(DateTime.Today);
        foreach (var mp in items)
        {
            var id = mp.MeteringPointIdentification;
            if (string.IsNullOrEmpty(id)) continue;
            byMpId[id] = new SettlMpWithCos(id, mp.Id, placeholder, mp);
        }

        _log($"  [water] loaded {items.Count} vandmålepunkter fra Settl, {items.Count} i scope");
    }
}
