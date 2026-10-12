namespace DataMigration.OpsCli.Services;

/// <summary>
/// Narrows the export's population to the customers migrated on one change-of-supplier date, and
/// keeps those customers <b>whole</b>.
///
/// <para><b>Why by customer and not by metering point.</b> The export gives each customer one
/// settlement schedule and one customer segment, decided from all her metering points together
/// (<see cref="SettlementScheduleGrouper"/> and <see cref="CustomerSegmentGrouper"/>). A customer can
/// hold metering points with different change-of-supplier dates. Keeping only the metering points
/// whose date matches would judge such a customer on part of her estate, and she could land in a
/// different schedule or segment than a full run gives her — a wrong answer with nothing to show for
/// it. So the date selects <i>customers</i>: any customer with at least one metering point on the
/// date is kept with every metering point in the supplied population she holds, whatever its own
/// delivery start. Historical callers supply only points delivering and owned on the selected date;
/// today's portfolio may be different.</para>
///
/// <para>A metering point Settl and Xellent could name no customer for is judged on its own date:
/// there is no customer to keep it whole with. It carries the date or it is dropped.</para>
///
/// <para>Pure — no Settl, no Xellent — so the semantics are unit-testable on their own.</para>
/// </summary>
public static class ChangeOfSupplierCohort
{
    /// <param name="KeptMeteringPointIdentifications">The population the rest of the export runs on.
    /// Both the metering points carrying the date and the ones that came along with their customer.</param>
    public sealed record Result(
        IReadOnlyList<string> KeptMeteringPointIdentifications,
        SettlementConfigChangeOfSupplierFilter Filter);

    /// <param name="date">The Settl change-of-supplier date to select on, as a Danish calendar date —
    /// the same date the caller already derived per metering point from the delivery period.</param>
    /// <param name="cosByMpId">Metering point identification → its change-of-supplier date. The whole
    /// population, before any narrowing.</param>
    /// <param name="customerByMpId">Metering point identification → the customer number the export
    /// would put on its row (Settl's answer first, the Xellent representative as fallback). A metering
    /// point missing from this map has no customer at all.</param>
    /// <param name="nameByCustomer">Customer number → name, for the reported rows only. Optional.</param>
    /// <param name="changeOfSupplierMeteringPoints">When supplied, only these verified supplier-change
    /// events can seed the cohort. A MoveIn starting on the date is not a supplier change.</param>
    public static Result Select(
        DateOnly date,
        IReadOnlyDictionary<string, DateOnly> cosByMpId,
        IReadOnlyDictionary<string, string> customerByMpId,
        IReadOnlyDictionary<string, string>? nameByCustomer = null,
        IReadOnlySet<string>? changeOfSupplierMeteringPoints = null)
    {
        // The customers the date names. One matching metering point is enough — that is what makes
        // her part of this population.
        var customers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mpId, cos) in cosByMpId)
        {
            if (cos != date || changeOfSupplierMeteringPoints is not null && !changeOfSupplierMeteringPoints.Contains(mpId)) continue;
            if (customerByMpId.TryGetValue(mpId, out var customer) && !string.IsNullOrWhiteSpace(customer))
                customers.Add(customer);
        }

        var onDateByCustomer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sharedByCustomer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var onDateWithoutCustomer = new List<string>();
        var kept = new List<string>();

        foreach (var (mpId, cos) in cosByMpId)
        {
            var hasCustomer = customerByMpId.TryGetValue(mpId, out var customer)
                && !string.IsNullOrWhiteSpace(customer);

            if (!hasCustomer)
            {
                // Nobody to be kept whole with, so its own date decides.
                if (cos != date || changeOfSupplierMeteringPoints is not null && !changeOfSupplierMeteringPoints.Contains(mpId)) continue;
                kept.Add(mpId);
                onDateWithoutCustomer.Add(mpId);
                continue;
            }

            if (!customers.Contains(customer!)) continue;
            kept.Add(mpId);

            var target = cos == date && (changeOfSupplierMeteringPoints is null || changeOfSupplierMeteringPoints.Contains(mpId))
                ? onDateByCustomer : sharedByCustomer;
            if (!target.TryGetValue(customer!, out var list))
                target[customer!] = list = [];
            list.Add(mpId);
        }

        var rows = customers
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => new SettlementConfigChangeOfSupplierFilterCustomer(
                c,
                nameByCustomer is not null && nameByCustomer.TryGetValue(c, out var name) ? name : null,
                Sorted(onDateByCustomer, c),
                Sorted(sharedByCustomer, c)))
            .ToList();

        var filter = new SettlementConfigChangeOfSupplierFilter(
            date,
            customers.Count,
            onDateByCustomer.Sum(kv => kv.Value.Count) + onDateWithoutCustomer.Count,
            sharedByCustomer.Sum(kv => kv.Value.Count),
            rows,
            [.. onDateWithoutCustomer.Order(StringComparer.Ordinal)]);

        return new Result([.. kept.Order(StringComparer.Ordinal)], filter);
    }

    private static IReadOnlyList<string> Sorted(Dictionary<string, List<string>> map, string key) =>
        map.TryGetValue(key, out var list) ? [.. list.Order(StringComparer.Ordinal)] : [];
}
