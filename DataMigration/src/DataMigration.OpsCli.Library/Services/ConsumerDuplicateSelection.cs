namespace DataMigration.OpsCli.Services;

/// <summary>The report's per-period decision, also used by cleanup without reading a CSV.
/// <see cref="FictitiousCpr2"/> follows the same period rules: false on the first period.</summary>
internal sealed record ConsumerDuplicateSelection(DeliveryPeriod Period, DeliveryConsumerCorrelation Correlation, bool? Duplicate,
    bool? FictitiousCpr2 = null)
{
    internal static IReadOnlyList<ConsumerDuplicateSelection> For(ConsumerDeliveryEvidence delivery, ConsumerVersionEvidence versions)
    {
        var periods = delivery.Periods;
        var first = periods.Count > 0 && periods.All(p => p.Start is not null)
            && periods.Count(p => p.Start == periods[0].Start) == 1 ? periods[0] : null;
        return periods.Select(period =>
        {
            var correlation = DeliveryConsumerCorrelation.For(period, versions);
            bool? duplicate = first is null ? null : ReferenceEquals(period, first) ? false
                : correlation.Latest?.Point?.Consumer is null ? null : correlation.Latest.Duplicate;
            bool? fictitious = first is null ? null : ReferenceEquals(period, first) ? false
                : correlation.Latest?.Point?.Consumer is null ? null : correlation.Latest.FictitiousSecondCpr == true;
            return new ConsumerDuplicateSelection(period, correlation, duplicate, fictitious);
        }).ToArray();
    }

    /// <summary>The version in effect today, when it overlaps a delivery period other than the first. It is the only
    /// version the fix may write, and it is judged itself, not the newest version of its period (a future version
    /// must not hide it). Null when the first period cannot be identified.</summary>
    internal static ConsumerVersion? CurrentTarget(ConsumerDeliveryEvidence delivery, ConsumerVersionEvidence versions)
    {
        var periods = delivery.Periods;
        if (periods.Count == 0 || periods.Any(p => p.Start is null) || periods.Count(p => p.Start == periods[0].Start) != 1) return null;
        var current = versions.Versions.FirstOrDefault(v => v.IsCurrent && v.Status == "As returned" && v.Start is not null);
        if (current is null) return null;
        var start = ConsumerDeliveryEvidence.Utc(current.Start!.Value);
        var end = current.End is { } stop ? ConsumerDeliveryEvidence.Utc(stop) : DateTimeOffset.MaxValue;
        return periods.Skip(1).Any(p => p.Start < end && (p.End ?? DateTimeOffset.MaxValue) > start) ? current : null;
    }
}
