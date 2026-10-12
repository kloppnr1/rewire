using System.Collections.Concurrent;
using System.Text;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal sealed record DeliveryStartAudit(string MeteringPoint, DateOnly? SelectedCos, string Disposition);

/// <summary>Selects the first actual delivery start. Later occupants must not replace migration ownership.</summary>
internal static class MoveInDeliveryFilter
{
    private static readonly TimeZoneInfo DkTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    internal static async Task<(IReadOnlyList<SettlMpWithCos> Kept,
        IReadOnlyList<DeliveryStartAudit> Audit, int Unresolved, int IncludedUnknown)> FilterAsync(
        IReadOnlyList<SettlMpWithCos> mps,
        Func<Guid, CancellationToken, Task<(GetDeliveryStatusesResponse? Statuses, string? Error)>> getStatuses,
        CancellationToken ct,
        Action<int, int>? progress = null,
        bool includeUnknownFirstStarts = false)
    {
        var audit = new ConcurrentBag<DeliveryStartAudit>();
        var kept = new ConcurrentBag<SettlMpWithCos>();
        var inspect = mps.ToArray();
        var completed = 0;
        var includedUnknown = 0;
        await Parallel.ForEachAsync(inspect,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (mp, token) =>
            {
                string disposition;
                DateOnly? firstDate = null;
                try
                {
                    var (statuses, error) = await getStatuses(mp.Id, token);
                    if (error is not null || statuses is null)
                        disposition = "DeliveryStatusLookupFailed";
                    else
                        (firstDate, disposition) = FirstDelivery(statuses.DeliveryStatuses);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { disposition = "DeliveryStatusLookupFailed"; }
                if (disposition == "ChangeOfSupplier"
                    || (includeUnknownFirstStarts && disposition == "UnknownDeliveryStartReason"
                        && firstDate.HasValue))
                {
                    kept.Add(mp with { Cos = firstDate!.Value });
                    if (disposition == "UnknownDeliveryStartReason")
                    {
                        Interlocked.Increment(ref includedUnknown);
                        audit.Add(new DeliveryStartAudit(mp.Identification, firstDate,
                            "IncludedUnknownDeliveryStartReason"));
                    }
                }
                else audit.Add(new DeliveryStartAudit(mp.Identification, firstDate, disposition));
                var count = Interlocked.Increment(ref completed);
                if (count % 500 == 0) progress?.Invoke(count, inspect.Length);
            });

        // Report the final total even for a run with zero status calls.
        progress?.Invoke(inspect.Length, inspect.Length);

        var rows = audit.OrderBy(a => a.MeteringPoint, StringComparer.Ordinal).ToArray();
        return (kept.OrderBy(m => m.Identification, StringComparer.Ordinal).ToArray(), rows,
            rows.Count(a => a.Disposition is not ("MoveIn" or "IncludedUnknownDeliveryStartReason")),
            includedUnknown);
    }

    internal static (DateOnly? Date, string Disposition) FirstDelivery(
        IReadOnlyList<DeliveryStatusDto>? statuses)
    {
        var starts = (statuses ?? [])
            .Where(s => s.InDelivery && s.Period is { } p && p.Start != default)
            .Select(s => (Date: ToDkDate(s.Period!.Start), s.StartReason))
            .OrderBy(s => s.Date).ToArray();
        if (starts.Length == 0) return (null, "NoFirstDeliveryStart");
        var first = starts[0].Date;
        var reasons = starts.Where(s => s.Date == first).Select(s => s.StartReason).Distinct().ToArray();
        if (reasons.Length != 1) return (first, "ConflictingDeliveryStartReasons");
        return (first, reasons[0] switch
        {
            DeliveryStartReason.ChangeOfSupplier => "ChangeOfSupplier",
            DeliveryStartReason.MoveIn => "MoveIn",
            DeliveryStartReason.Unknown => "UnknownDeliveryStartReason",
            _ => "OtherDeliveryStartReason"
        });
    }

    internal static DateOnly ToDkDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(value.UtcDateTime, DkTimeZone));

    internal static string BuildCsv(IReadOnlyList<DeliveryStartAudit> rows)
    {
        var csv = new StringBuilder("MeteringPoint;SelectedCos;Disposition\n");
        foreach (var row in rows)
            csv.Append(row.MeteringPoint).Append(';')
                .Append(row.SelectedCos?.ToString("yyyy-MM-dd")).Append(';')
                .Append(row.Disposition).Append('\n');
        return csv.ToString();
    }

    internal static string ReportPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath) ?? "",
            $"{Path.GetFileNameWithoutExtension(outputPath)}-delivery-start-audit.csv");
}
