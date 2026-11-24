using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal sealed record RepairDeliveryEvent(string Kind, DateOnly Date, DateOnly LastSupplyDay);

internal static class RepairDeliveryHistory
{
    internal const string Missing = "Delivery history missing or failed.";
    internal const string Unclear = "Delivery history does not establish the old customer's end date.";

    internal static CustomerPayerRepairDecision Apply(CustomerPayerRepairDecision decision, RepairSettlEvidence settl)
    {
        var checkedDecision = decision with { DeliveryHistoryChecked = true };
        if (decision.Status != "Ready") return checkedDecision;
        var histories = settl.Histories.Where(h => h.MeteringPoint == decision.MeteringPoint).ToArray();
        if (histories.Length != 1 || histories[0].Error is not null
            || histories[0].Statuses?.DeliveryStatuses is not { Count: > 0 } statuses)
            return checkedDecision with { Status = Missing };
        if (decision.Start is not { } linkStart) return checkedDecision with { Status = Unclear };

        var events = new List<RepairDeliveryEvent>();
        var uncertain = new List<DateOnly>();
        var periods = new List<(DeliveryStatusDto Status, DateOnly Start, DateOnly? End)>();
        foreach (var status in statuses)
        {
            if (status.Period is not { } period || period.Start == default || period.End == default)
                return checkedDecision with { Status = Unclear };
            var start = MoveInDeliveryFilter.ToDkDate(period.Start);
            DateOnly? end = period.End.Year >= 9999 ? null : MoveInDeliveryFilter.ToDkDate(period.End);
            if (end <= start) return checkedDecision with { Status = Unclear };
            periods.Add((status, start, end));
        }
        periods = periods.Distinct().OrderBy(p => p.Start).ToList();
        var supplied = periods.Where(p => p.Status.InDelivery).ToArray();
        if (supplied.Length == 0) return checkedDecision with { Status = Unclear };
        var firstSupply = supplied[0].Start;
        if (supplied.GroupBy(p => p.Start).Any(g => g.Select(p => p.Status.StartReason).Distinct().Count() > 1))
            return checkedDecision with { Status = Unclear };
        foreach (var p in periods)
        {
            if (p.Status.InDelivery)
            {
                if (p.Status.StartReason == DeliveryStartReason.MoveIn && p.Start >= linkStart)
                    Add("MoveIn", p.Start);
                else if (p.Status.StartReason != DeliveryStartReason.ChangeOfSupplier)
                    uncertain.Add(p.Start);
                else if (p.Start > firstSupply)
                    uncertain.Add(p.Start); // A later return cannot extend the migrated owner's old period.

                if (p.End is { } end && end >= linkStart)
                {
                    var following = periods.Where(next => next.Start == end).ToArray();
                    if (p.Status.StopReason is DeliveryStopReason.MoveOut or DeliveryStopReason.ChangeOfSupplier)
                        Add(p.Status.StopReason == DeliveryStopReason.MoveOut ? "MoveOut" : "SupplierDeparture", end);
                    else if (!following.Any(next => !next.Status.InDelivery
                        && next.Status.StopReason is DeliveryStopReason.MoveOut or DeliveryStopReason.ChangeOfSupplier)
                        && !following.Any(next => next.Status.InDelivery && next.Status.StartReason == DeliveryStartReason.MoveIn))
                        uncertain.Add(end);
                }
                else if (p.End is null && p.Status.StopReason != DeliveryStopReason.Na)
                    uncertain.Add(p.Start); // An open period does not provide a departure date.
            }
            else if (p.Start >= linkStart && p.Start >= firstSupply)
            {
                if (p.Status.StopReason is DeliveryStopReason.MoveOut or DeliveryStopReason.ChangeOfSupplier
                    && supplied.Any(prev => prev.End == p.Start))
                    Add(p.Status.StopReason == DeliveryStopReason.MoveOut ? "MoveOut" : "SupplierDeparture", p.Start);
                else uncertain.Add(p.Start);
            }
        }
        var boundary = events.OrderBy(e => e.LastSupplyDay).ThenBy(e => e.Kind, StringComparer.Ordinal).FirstOrDefault();
        if (uncertain.Any(date => boundary is null || date <= boundary.LastSupplyDay))
            return checkedDecision with { Status = Unclear, Warnings = decision.Warnings.Append(
                "Uklar leveringshistorik ved " + string.Join(" | ", uncertain.Where(date => boundary is null || date <= boundary.LastSupplyDay)
                    .Distinct().Order().Select(date => date.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture)))
                + ". " + (periods.Any(p => p.End is null && p.Status.StopReason != DeliveryStopReason.Na)
                    ? "Ophør er registreret på en åben periode uden slutdato." : "Leveringsskiftet eller årsagen skal afklares.")).ToArray() };
        if (boundary is null) return checkedDecision;
        if (boundary.LastSupplyDay < linkStart)
            return checkedDecision with { Status = Unclear, DeliveryEvent = boundary };
        var original = decision.Contracts.ToArray();
        return checkedDecision with
        {
            End = boundary.LastSupplyDay,
            DeliveryEvent = boundary,
            OriginalContracts = original,
            Contracts = original.Where(c => c.Start <= boundary.LastSupplyDay)
                .Select(c => c with { End = c.End > boundary.LastSupplyDay ? boundary.LastSupplyDay : c.End }).ToList()
        };

        void Add(string kind, DateOnly exclusiveBoundary)
        {
            if (exclusiveBoundary > DateOnly.MinValue)
                events.Add(new(kind, exclusiveBoundary, exclusiveBoundary.AddDays(-1)));
            else uncertain.Add(exclusiveBoundary);
        }
    }

    internal static string Explanation(CustomerPayerRepairDecision d, RepairSettlEvidence settl, DateTimeOffset? sourceCapturedAt = null)
    {
        var text = d.DeliveryHistoryChecked ? "Leveringshistorik: til." : "Leveringshistorik: fra.";
        text += $" Settl-kopi: {settl.CapturedAt.UtcDateTime:dd-MM-yyyy HH:mm} UTC.";
        if (sourceCapturedAt is { } captured) text += $" Xellent-kopi: {captured.UtcDateTime:dd-MM-yyyy HH:mm} UTC.";
        if (d.DeliveryEvent is not { } e) return text;
        var label = e.Kind switch { "MoveIn" => "Indflytning", "MoveOut" => "Fraflytning", _ => "Leverandørskifte væk fra Verdo" };
        text += $" {label} {e.Date:dd-MM-yyyy}; gamle links slutter {e.LastSupplyDay:dd-MM-yyyy}. Link: {d.Start:dd-MM-yyyy} – Åben → {d.Start:dd-MM-yyyy} – {e.LastSupplyDay:dd-MM-yyyy}.";
        foreach (var c in d.OriginalContracts)
        {
            var result = c.Start > e.LastSupplyDay ? "Udeladt" : $"{c.Start:dd-MM-yyyy} – {End(c.End < e.LastSupplyDay ? c.End : e.LastSupplyDay)}";
            if (c.End > e.LastSupplyDay || c.Start > e.LastSupplyDay)
                text += $" {c.ContractName}: {c.Start:dd-MM-yyyy} – {End(c.End)} → {result}.";
        }
        return text;
    }

    private static string End(DateOnly date) => date == DateOnly.MaxValue ? "Åben" : date.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
