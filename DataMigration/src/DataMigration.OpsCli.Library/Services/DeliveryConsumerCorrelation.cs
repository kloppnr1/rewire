namespace DataMigration.OpsCli.Services;

internal sealed record DeliveryConsumerCorrelation(ConsumerVersion? Latest, int? Count, bool? AnyDuplicate, string Warning)
{
    internal static DeliveryConsumerCorrelation For(DeliveryPeriod period, ConsumerVersionEvidence evidence)
    {
        if (period.Start is null || period.Status == "Delivery period dates missing or invalid.")
            return new(null, null, null, "Delivery dates are unavailable or invalid; Consumer correlation cannot be determined.");
        var warnings = evidence.Errors.Concat(evidence.Versions.Where(v => v.Status != "As returned").Select(v => v.Status))
            .Distinct().ToArray();
        var end = period.End ?? DateTimeOffset.MaxValue;
        var versions = evidence.Versions.Where(v => v.Start is { } start && start != default
            && ConsumerDeliveryEvidence.Utc(start) < end
            && (v.End is { } stop ? ConsumerDeliveryEvidence.Utc(stop) : DateTimeOffset.MaxValue) > period.Start.Value)
            .OrderBy(v => ConsumerDeliveryEvidence.Utc(v.Start!.Value)).ToArray();
        var anyDuplicate = versions.Any(v => v.Status == "As returned" && v.Duplicate);
        if (warnings.Length > 0)
            return new(null, null, anyDuplicate ? true : null, "Consumer correlation incomplete: " + string.Join(" | ", warnings));
        return versions.Length == 0
            ? new(null, 0, false, "No Consumer version effective during this delivery period.")
            : new(versions[^1], versions.Length, anyDuplicate, "");
    }
}
