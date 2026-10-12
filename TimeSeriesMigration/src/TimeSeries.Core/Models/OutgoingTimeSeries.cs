using System.Globalization;

namespace TimeSeries.Core.Models;

/// <summary>Prepares outgoing values without changing the extracted source segments.</summary>
public static class OutgoingTimeSeries
{
    public static bool RequiresConversion(TimeSeriesSegmentDto segment) =>
        segment.TimeResolution == "Hour" && segment.ReadingResolution == "Quarter";

    public static string Resolution(TimeSeriesSegmentDto segment) =>
        RequiresConversion(segment) ? "Quarter" : segment.TimeResolution;

    public static int PointCount(TimeSeriesSegmentDto segment) =>
        checked((segment.CountedPoints ?? segment.Points.Count) * (RequiresConversion(segment) ? 4 : 1));

    // Divisibility by 0.00004 also works in SQL without division/rounding precision loss.
    public static bool CanSplit(decimal value) => value % 0.00004m == 0m;

    public static string PrecisionError(DateTimeOffset timestamp, decimal value) =>
        $"hourly value {value.ToString(CultureInfo.InvariantCulture)} at {timestamp:O} cannot be split equally within five decimal places";

    public static string? Validate(TimeSeriesSegmentDto segment)
    {
        if (!RequiresConversion(segment)) return null;
        if (segment.ConversionError != null) return segment.ConversionError;
        var invalid = segment.Points.FirstOrDefault(p => !CanSplit(p.Value));
        return invalid == null ? null : PrecisionError(invalid.TimeOfValue, invalid.Value);
    }

    public static TimeSeriesSegmentDto Prepare(TimeSeriesSegmentDto source)
    {
        if (Validate(source) is { } error) throw new InvalidOperationException(error);
        if (!RequiresConversion(source)) return source;
        return new TimeSeriesSegmentDto
        {
            TimeResolution = "Quarter",
            ReadingResolution = source.ReadingResolution,
            CompanyId = source.CompanyId,
            PeriodStart = source.PeriodStart,
            PeriodEnd = source.PeriodEnd,
            CountedPoints = source.CountedPoints.HasValue ? PointCount(source) : null,
            FirstCountedPoint = source.FirstCountedPoint,
            Points = source.Points.SelectMany(p => Enumerable.Range(0, 4).Select(i => new TimeSeriesDto
            {
                TimeOfValue = p.TimeOfValue.AddMinutes(i * 15),
                Value = p.Value / 4m,
                TimeResolution = "Quarter",
                QuantityCodeValue = p.QuantityCodeValue,
                QualityOfValue = p.QualityOfValue
            })).ToList()
        };
    }

    /// <summary>Pairs original values with the same prepared values used for posting.</summary>
    public static TimeSeriesPreviewFileDto Preview(MeteringPointTimeSeriesDto source, DateTimeOffset? deliveryBoundary)
    {
        var preview = new TimeSeriesPreviewFileDto { MeteringPointId = source.MeteringPointId };
        var included = new List<(TimeSeriesSegmentDto Segment, List<TimeSeriesPreviewPointDto> Groups)>();
        foreach (var segment in source.Segments)
        {
            if (segment.CountedPoints.HasValue)
                throw new InvalidOperationException("A value preview requires extracted points, not counts.");
            var groups = segment.Points.Select(p => new TimeSeriesPreviewPointDto
            {
                Source = new() { Time = p.TimeOfValue, Quantity = p.Value, Quality = p.QualityOfValue, Resolution = segment.TimeResolution },
                Excluded = deliveryBoundary is { } boundary && p.TimeOfValue < boundary
            }).ToList();
            preview.Points.AddRange(groups);
            included.Add((new TimeSeriesSegmentDto
            {
                TimeResolution = segment.TimeResolution,
                ReadingResolution = segment.ReadingResolution,
                Points = segment.Points.Where(p => deliveryBoundary is null || p.TimeOfValue >= deliveryBoundary).ToList()
            }, groups.Where(g => !g.Excluded).ToList()));
        }

        preview.Points = preview.Points.OrderBy(p => p.Source.Time).ThenBy(p => p.Source.Resolution, StringComparer.Ordinal).ToList();
        preview.SourceCount = preview.Points.Count;
        preview.ExcludedCount = preview.Points.Count(p => p.Excluded);
        preview.Error = source.SeriesWithoutAgreement.Count > 0
            ? "Xellent series without an agreement period: " + string.Join(", ", source.SeriesWithoutAgreement)
            : included.Select(s => Validate(s.Segment)).FirstOrDefault(error => error != null);
        // Match posting's whole-meter validation: no partial converted preview on failure.
        if (preview.Error != null) return preview;

        foreach (var (segment, groups) in included)
        {
            var prepared = Prepare(segment);
            var pointsPerSource = RequiresConversion(segment) ? 4 : 1;
            for (var i = 0; i < groups.Count; i++)
                groups[i].Outgoing = prepared.Points.GetRange(i * pointsPerSource, pointsPerSource)
                    .Select(p => new TimeSeriesPreviewValueDto
                    {
                        Time = p.TimeOfValue, Quantity = p.Value, Quality = "Measured", Resolution = prepared.TimeResolution
                    }).ToList();
        }
        preview.OutgoingCount = preview.Points.Sum(p => p.Outgoing.Count);
        return preview;
    }
}
