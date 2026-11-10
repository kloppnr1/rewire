using System.Collections.Concurrent;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

/// <summary>Historical delivery and payer ownership. Current MP relationships are never a fallback.</summary>
public static class HistoricalSettlementSource
{
    private static readonly TimeZoneInfo DanishZone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    public sealed record Result(
        Dictionary<string, DateOnly> DeliveryStartByMeteringPoint,
        Dictionary<string, Guid> CustomerByMeteringPoint,
        IReadOnlySet<string> ChangeOfSupplierMeteringPoints,
        IReadOnlySet<Guid> IncompleteCustomers,
        IReadOnlyList<SettlementConfigHistoricalIssue> Issues)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyDictionary<Guid,IReadOnlyList<SettlementConfigHistoricalIssue>> IncompleteCustomerCauses { get; init; }
            = new Dictionary<Guid,IReadOnlyList<SettlementConfigHistoricalIssue>>();
    }

    /// <summary>Strict-list migration origins, using the same first-delivery rule as state generation.</summary>
    public static async Task<Result> ReadFirstSupplierChangesAsync(
        IReadOnlyDictionary<string, Guid> meteringPoints,
        Func<Guid, CancellationToken, Task<(GetDeliveryStatusesResponse? History, string? Error)>> readHistory,
        Func<IReadOnlyList<Guid>, DateTime, CancellationToken, Task<(List<PayerForMeteringPointsDto>? Payers, string? Error)>> readPayers,
        Action<string>? log = null, CancellationToken cancellationToken = default,
        Action<SettlementConfigProgress>? progress = null)
    {
        var histories = new ConcurrentDictionary<Guid, GetDeliveryStatusesResponse>();
        var dates = new ConcurrentDictionary<string, DateOnly>(StringComparer.Ordinal);
        var issues = new ConcurrentDictionary<string, SettlementConfigUnresolvedReason>(StringComparer.Ordinal);
        var completed = 0;
        var progressSync = new object();
        progress?.Invoke(new("FirstDeliveryHistory", 0, meteringPoints.Count, "meteringPoints"));
        await Parallel.ForEachAsync(meteringPoints,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken }, async (mp, ct) =>
            {
                GetDeliveryStatusesResponse? history;
                string? error;
                try { (history,error) = await readHistory(mp.Value,ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { history = null; error = "Delivery history request failed"; }
                if (error is not null) issues[mp.Key] = SettlementConfigUnresolvedReason.DeliveryHistoryReadFailed;
                else if (history is null) issues[mp.Key] = SettlementConfigUnresolvedReason.DeliveryHistoryMissing;
                else
                {
                    var first = MoveInDeliveryFilter.FirstDelivery(history.DeliveryStatuses);
                    if (first is { Date: { } date, Disposition: "ChangeOfSupplier" })
                    {
                        dates[mp.Key] = date;
                        histories[mp.Value] = history;
                    }
                    else issues[mp.Key] = first.Disposition switch
                    {
                        "ConflictingDeliveryStartReasons" => SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous,
                        "MoveIn" => SettlementConfigUnresolvedReason.FirstDeliveryIsMoveIn,
                        "UnknownDeliveryStartReason" => SettlementConfigUnresolvedReason.FirstDeliveryStartReasonUnknown,
                        "OtherDeliveryStartReason" => SettlementConfigUnresolvedReason.FirstDeliveryNotChangeOfSupplier,
                        _ => SettlementConfigUnresolvedReason.DeliveryHistoryMissing
                    };
                }
                int count;
                lock (progressSync)
                {
                    count = ++completed;
                    progress?.Invoke(new("FirstDeliveryHistory", count, meteringPoints.Count, "meteringPoints"));
                }
                if (count % 500 == 0 || count == meteringPoints.Count)
                    log?.Invoke($"  First delivery lookup: {count}/{meteringPoints.Count}");
            });
        var starts = new Dictionary<string,DateOnly>(StringComparer.Ordinal);
        var customers = new Dictionary<string,Guid>(StringComparer.Ordinal);
        var seeds = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = new HashSet<Guid>();
        var incompleteCauses = new Dictionary<Guid,List<SettlementConfigHistoricalIssue>>();
        var payerDone = 0;
        progress?.Invoke(new("HistoricalPayers", 0, dates.Count, "meteringPoints"));
        foreach (var group in dates.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
        {
            var selected = group.ToDictionary(kv => kv.Key,kv => meteringPoints[kv.Key],StringComparer.Ordinal);
            var groupDone = 0;
            var dated = await ReadAsync(group.Key,selected,
                (id,_) => Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((histories[id],null)),
                readPayers,log,cancellationToken, p =>
                {
                    // These histories are cached. Keep one payer phase across all per-point dates.
                    if (p.Stage != "HistoricalPayers" || p.Done is not > 0) return;
                    groupDone = p.Done.Value;
                    progress?.Invoke(new("HistoricalPayers", payerDone + groupDone, dates.Count, "meteringPoints"));
                });
            payerDone += groupDone;
            foreach (var (mp,date) in dated.DeliveryStartByMeteringPoint) starts[mp] = date;
            foreach (var (mp,customer) in dated.CustomerByMeteringPoint) customers[mp] = customer;
            seeds.UnionWith(dated.ChangeOfSupplierMeteringPoints);
            incomplete.UnionWith(dated.IncompleteCustomers);
            foreach (var (customer,causes) in dated.IncompleteCustomerCauses)
            {
                if (!incompleteCauses.TryGetValue(customer,out var existing)) incompleteCauses[customer] = existing = [];
                existing.AddRange(causes);
            }
            foreach (var issue in dated.Issues) issues[issue.MeteringPointIdentification] = issue.Reason;
        }
        return new(starts,customers,seeds,incomplete,
            issues.OrderBy(kv => kv.Key,StringComparer.Ordinal).Select(kv => new SettlementConfigHistoricalIssue(kv.Key,kv.Value)).ToArray())
        { IncompleteCustomerCauses = incompleteCauses.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<SettlementConfigHistoricalIssue>)kv.Value.DistinctBy(i => i.MeteringPointIdentification).ToArray()) };
    }

    public static async Task<Result> ReadAsync(
        DateOnly date, IReadOnlyDictionary<string, Guid> meteringPoints,
        Func<Guid, CancellationToken, Task<(GetDeliveryStatusesResponse? History, string? Error)>> readHistory,
        Func<IReadOnlyList<Guid>, DateTime, CancellationToken, Task<(List<PayerForMeteringPointsDto>? Payers, string? Error)>> readPayers,
        Action<string>? log = null, CancellationToken cancellationToken = default,
        Action<SettlementConfigProgress>? progress = null)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), DanishZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DanishZone);
        var starts = new ConcurrentDictionary<string, DateOnly>(StringComparer.Ordinal);
        var seeds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var issues = new ConcurrentDictionary<string, SettlementConfigUnresolvedReason>(StringComparer.Ordinal);
        var completed = 0;
        var progressSync = new object();
        progress?.Invoke(new("HistoricalDeliveryHistory", 0, meteringPoints.Count, "meteringPoints"));
        await Parallel.ForEachAsync(meteringPoints, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken }, async (mp, ct) =>
        {
            GetDeliveryStatusesResponse? history;
            string? error;
            try { (history, error) = await readHistory(mp.Value, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { history = null; error = "Delivery history request failed"; }
            var selection = SelectDelivery(date, history, error);
            if (selection.Reason is { } reason) issues[mp.Key] = reason;
            if (selection.Start is { } deliveryStart)
            {
                starts[mp.Key] = deliveryStart;
                if (selection.IsChangeOfSupplier) seeds[mp.Key] = 0;
            }
            int count;
            lock (progressSync)
            {
                count = ++completed;
                progress?.Invoke(new("HistoricalDeliveryHistory", count, meteringPoints.Count, "meteringPoints"));
            }
            if (count % 500 == 0 || count == meteringPoints.Count)
                log?.Invoke($"  Historical delivery lookup: {count}/{meteringPoints.Count}");
        });

        var customers = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var incomplete = new HashSet<Guid>();
        var incompleteCauses = new Dictionary<Guid,List<SettlementConfigHistoricalIssue>>();
        // Ask also for MPs whose delivery read failed: a known customer's other missing MP must
        // prevent a proposal based on only the readable part of that customer's portfolio.
        var lookups = meteringPoints.Where(mp => starts.ContainsKey(mp.Key) || issues.ContainsKey(mp.Key)).ToArray();
        var payerDone = 0;
        progress?.Invoke(new("HistoricalPayers", 0, lookups.Length, "meteringPoints"));
        foreach (var batch in lookups.Chunk(200))
        {
            var ids = batch.Select(mp => mp.Value).Distinct().ToArray();
            var first = await ReadPayers(start);
            var last = await ReadPayers(end.AddTicks(-1));
            async Task<(List<PayerForMeteringPointsDto>? Payers, string? Error)> ReadPayers(DateTime instant)
            {
                try { return await readPayers(ids, instant, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { return (null, "Historical payer request failed"); }
            }
            foreach (var mp in batch)
            {
                var firstOwners = Owners(first.Payers, mp.Value, date);
                var lastOwners = Owners(last.Payers, mp.Value, date);
                var candidates = firstOwners.Concat(lastOwners).ToHashSet();
                SettlementConfigUnresolvedReason? problem = null;
                if (first.Error is not null || last.Error is not null || first.Payers is null || last.Payers is null)
                    problem = SettlementConfigUnresolvedReason.HistoricalCustomerReadFailed;
                else if (firstOwners.Count == 0 || lastOwners.Count == 0)
                    problem = SettlementConfigUnresolvedReason.HistoricalCustomerMissing;
                else if (firstOwners.Count != 1 || lastOwners.Count != 1 || !firstOwners.SetEquals(lastOwners))
                    problem = SettlementConfigUnresolvedReason.HistoricalCustomerAmbiguous;
                if (problem is { } reason) issues.TryAdd(mp.Key, reason);
                if (issues.TryGetValue(mp.Key,out var issueReason))
                {
                    incomplete.UnionWith(candidates);
                    foreach (var candidate in candidates)
                    {
                        if (!incompleteCauses.TryGetValue(candidate,out var causes)) incompleteCauses[candidate] = causes = [];
                        causes.Add(new(mp.Key,issueReason));
                    }
                }
                if (problem is null) customers[mp.Key] = firstOwners.Single();
            }
            payerDone += batch.Length;
            progress?.Invoke(new("HistoricalPayers", payerDone, lookups.Length, "meteringPoints"));
        }
        return new(starts.ToDictionary(), customers, seeds.Keys.ToHashSet(StringComparer.Ordinal), incomplete,
            issues.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new SettlementConfigHistoricalIssue(kv.Key, kv.Value)).ToArray())
        { IncompleteCustomerCauses = incompleteCauses.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<SettlementConfigHistoricalIssue>)kv.Value.ToArray()) };
    }

    public static (DateOnly? Start, bool IsChangeOfSupplier, SettlementConfigUnresolvedReason? Reason) SelectDelivery(
        DateOnly date, GetDeliveryStatusesResponse? history, string? error = null)
    {
        if (error is not null) return (null, false, SettlementConfigUnresolvedReason.DeliveryHistoryReadFailed);
        if (history?.DeliveryStatuses is not { Count: > 0 } statuses || statuses.Any(s => s.Period is null || s.Period.Start == default || s.Period.End <= s.Period.Start))
            return (null, false, SettlementConfigUnresolvedReason.DeliveryHistoryMissing);
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), DanishZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DanishZone);
        var overlapping = statuses.Where(s => s.Period!.Start < end && s.Period.End > start).ToArray();
        if (overlapping.Length == 0) return (null, false, SettlementConfigUnresolvedReason.DeliveryHistoryMissing);
        if (overlapping.Length != 1 || overlapping[0].Period!.Start > start
            || overlapping[0].Period!.End < end)
            return (null, false, SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous);
        var period = overlapping[0];
        if (!period.InDelivery) return (null, false, null);
        if (period.StartReason is DeliveryStartReason.Na or DeliveryStartReason.Unknown)
            return (null, false, SettlementConfigUnresolvedReason.DeliveryHistoryMissing);
        var deliveryStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.Period!.Start, DanishZone).DateTime);
        return (deliveryStart, deliveryStart == date && period.StartReason == DeliveryStartReason.ChangeOfSupplier, null);
    }

    private static HashSet<Guid> Owners(List<PayerForMeteringPointsDto>? rows, Guid mpId, DateOnly date)
    {
        var active = (rows ?? []).Where(row => row.Payer is { } payer && payer.CustomerId != Guid.Empty
            && (row.MeteringPoints ?? []).Any(mp => mp.MeteringPointId == mpId && mp.Start != default && mp.Start <= date
                && (mp.End is null || mp.End == DateOnly.MaxValue || mp.End > date)))
            .ToArray();
        return active.Select(row => row.Payer!.CustomerId).ToHashSet();
    }
}
