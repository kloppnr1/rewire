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
}
