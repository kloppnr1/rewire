using System.Text.Json;

namespace DataMigration.OpsCli.Services;

/// <summary>Reports proposal problems and current assignment read status.</summary>
public static class SettlementConfigReview
{
    public static SettlementConfigCurrentSegment ReadCurrentSegment(string json, IReadOnlyDictionary<Guid, string?>? names)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var customer = document.RootElement;
            if (Property(customer, "customer", out var nested)) customer = nested;
            if (!Property(customer, "customerSegmentId", out var value)) return new(null, "NotRead");
            if (value.ValueKind == JsonValueKind.Null || value.GetString() is null or "") return new(null, "NotAssigned");
            if (!Guid.TryParse(value.GetString(), out var id)) return new(null, "Unmapped");
            if (id == Guid.Empty) return new(null, "NotAssigned");
            if (names is null) return new(null, "ReadFailed", id);
            return names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? new(name, "Read", id) : new(null, "Unmapped", id);
        }
        catch (JsonException) { return new(null, "ReadFailed"); }
        catch (InvalidOperationException) { return new(null, "ReadFailed"); }
    }

    private static bool Property(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default;
        return false;
    }

    public static IReadOnlyList<SettlementConfigReviewRow> Build(SettlementConfigDocument document,
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? groups,
        IReadOnlyDictionary<string, string>? groupSchedules,
        IReadOnlyDictionary<string, SettlementConfigCurrentSegment> currentSegments,
        bool contractsRead, bool assignmentsSkipped = false)
    {
        var proposedSegments = (document.Segments ?? []).Where(g => g.Mapped)
            .SelectMany(g => g.Customers.Select(c => (c.CustomerNumber, g.Segment))).ToDictionary(x => x.CustomerNumber, x => x.Segment);
        var proposedSchedules = document.Schedules.Where(g => g.Mapped)
            .SelectMany(g => g.Customers.Select(c => (c.CustomerNumber, g.Schedule))).ToDictionary(x => x.CustomerNumber, x => x.Schedule);
        var byCustomer = entries.GroupBy(e => e.CustomerNumber).ToDictionary(g => g.Key, g => g.ToList());
        var unresolvedByCustomer = document.Unresolved.Where(u => u.CustomerNumber is not null)
            .GroupBy(u => u.CustomerNumber!).ToDictionary(g => g.Key, g => g.ToList());
        var segmentUnresolvedCustomers = (document.SegmentUnresolved ?? []).Select(u => u.CustomerNumber).ToHashSet(StringComparer.Ordinal);
        var scheduleConflicts = (document.ScheduleConflicts ?? []).ToDictionary(c => c.CustomerNumber, StringComparer.Ordinal);
        var segmentConflicts = (document.SegmentConflicts ?? []).ToDictionary(c => c.CustomerNumber, StringComparer.Ordinal);
        return byCustomer.Keys.Concat(unresolvedByCustomer.Keys).Distinct().Order(StringComparer.Ordinal).Select(customer =>
        {
            var rows = byCustomer.GetValueOrDefault(customer) ?? [];
            var reasons = new List<string>();
            if (unresolvedByCustomer.TryGetValue(customer, out var unresolved))
                reasons.AddRange(unresolved.Select(u => $"{u.MeteringPointIdentification}: {u.Reason}"));
            if (ChargingCause(rows,"ReadFailed") is { } failedCharge)
                reasons.Add("Xellent Charge_Abo read failed for metering points: " + failedCharge);
            if (ChargingCause(rows,"Ambiguous") is { } ambiguousCharge)
                reasons.Add("Customer segment is blocked by ambiguous historical charging contract parts for metering points: " + ambiguousCharge);
            if (segmentUnresolvedCustomers.Contains(customer))
                reasons.Add("One or more Xellent metering points have no usable segment basis");
            if (rows.Any(e => !e.CustomerInXellent)) reasons.Add("No Xellent account for this customer");
            var scheduleConflict = scheduleConflicts.GetValueOrDefault(customer);
            if (scheduleConflict is not null) reasons.Add("Conflicting schedule proposals: " + string.Join(", ", scheduleConflict.Candidates.Select(c => c.Schedule)));
            var segmentConflict = segmentConflicts.GetValueOrDefault(customer);
            if (segmentConflict is not null) reasons.Add("Conflicting or ambiguous segment proposals: " + string.Join(", ", segmentConflict.Candidates.SelectMany(c => c.Segments).Distinct()));
            var unmappedPlans = rows.Select(e => e.BillingPlanNum).Intersect(document.UnmappedBillingPlanNums, StringComparer.OrdinalIgnoreCase).ToList();
            if (unmappedPlans.Count > 0) reasons.Add("Unknown billing plan mapping: " + string.Join(", ", unmappedPlans));
            var unmappedTemplates = rows.Select(e => e.ContractTemplate).OfType<string>().Intersect(document.UnmappedContractTemplates ?? [], StringComparer.OrdinalIgnoreCase).ToList();
            if (unmappedTemplates.Count > 0) reasons.Add("Unknown contract template mapping: " + string.Join(", ", unmappedTemplates));
            var proposedSegment = proposedSegments.GetValueOrDefault(customer);
            var proposedSchedule = proposedSchedules.GetValueOrDefault(customer);
            if (proposedSegment is null) reasons.Add("No mapped Xellent segment proposal; see segment conflicts, unresolved rows and unmapped templates");
            if (proposedSchedule is null) reasons.Add("No mapped Xellent schedule proposal; see schedule conflicts, unresolved rows and unmapped plans");
            var current = assignmentsSkipped ? new SettlementConfigCurrentSegment(null,"Skipped")
                : currentSegments.GetValueOrDefault(customer) ?? new(null, "NotRead");
            var memberships = groups is null ? null : groups.GetValueOrDefault(customer) ?? [];
            var schedule = SettlementGroupScheduleResolver.Resolve(memberships, groupSchedules);
            var scheduleStatus = assignmentsSkipped ? "Skipped" : memberships is null ? "ReadFailed" : memberships.Count == 0 ? "NotAssigned" : schedule.Schedule is null ? "Unmapped" : "Read";
            if (AssignmentProblem("segment",current.Status) is { } segmentProblem) reasons.Add(segmentProblem);
            if (AssignmentProblem("schedule",scheduleStatus) is { } scheduleProblem) reasons.Add(scheduleProblem);
            return new SettlementConfigReviewRow(customer, proposedSegment, proposedSchedule, current.Segment,
                schedule.Schedule, current.Status, scheduleStatus, null, null, null, null,
                rows.Select(e => new SettlementConfigXellentBasis(e.MeteringPointIdentification, e.XellentAccount,
                    e.ReferenceDate, e.BillingPlanNum, e.ContractTemplate, e.ChargeProducts, e.ChargeReadStatus, e.ProductNums, e.XellentContractNumber, e.MeterPointTypeCode, e.MeterPointTypeReadStatus))
                    .Concat((unresolved ?? []).Where(u => rows.All(e => e.MeteringPointIdentification != u.MeteringPointIdentification))
                        .Select(u => new SettlementConfigXellentBasis(u.MeteringPointIdentification,null,null,"",null,null,"NotRead"))).ToList(), reasons, current.SegmentId);
        }).ToList();
    }

    private static string? AssignmentProblem(string assignment,string status) => status switch
    {
        "Read" or "Skipped" => null,
        "NotAssigned" => $"No {assignment} assigned in Settl",
        "NotRead" => $"Settl {assignment} was not read",
        "Unmapped" => $"Could not match the Settl {assignment} to a known name",
        _ => $"Could not read the Settl {assignment}"
    };

    private static string? ChargingCause(IEnumerable<SettlementConfigEntry> rows,string status)
    {
        var points = rows.Where(e => e.ChargeReadStatus == status).Select(e => e.MeteringPointIdentification)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return points.Length == 0 ? null : string.Join(", ",points.Take(10))
            + (points.Length > 10 ? $" (+{points.Length - 10} more; see JSON charging evidence)" : "");
    }
}
