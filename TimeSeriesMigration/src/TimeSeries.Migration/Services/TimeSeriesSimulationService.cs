using TimeSeries.Core.Models;

namespace TimeSeries.Cli.Services;

public class TimeSeriesSimulationService
{
    private const string SimulatedQuality = "Estimated";

    public MeteringPointTimeSeriesDto FillGaps(
        MeteringPointTimeSeriesDto data,
        DateTime startDate,
        DateTime endDate)
    {
        // Hvis MP'et ingen segmenter har, fabriker ét fuldt Hour-segment for hele perioden.
        if (data.Segments.Count == 0)
        {
            return GenerateFullSeries(data.MeteringPointId, data.ParentMeteringPointId, startDate, endDate);
        }

        // Gap-fill stays inden for hvert segment — krydser ikke segment-grænser (resolution kan skifte).
        var filledSegments = data.Segments
            .Select(seg => FillSegmentGaps(seg))
            .ToList();

        return new MeteringPointTimeSeriesDto
        {
            MeteringPointId = data.MeteringPointId,
            ParentMeteringPointId = data.ParentMeteringPointId,
            Unit = data.Unit,
            Segments = filledSegments
        };
    }

    private TimeSeriesSegmentDto FillSegmentGaps(TimeSeriesSegmentDto segment)
    {
        var existingPoints = segment.Points.OrderBy(p => p.TimeOfValue).ToList();
        if (existingPoints.Count < 2) return segment;

        var resolution = DetectResolution(existingPoints);
        var first = existingPoints.First().TimeOfValue;
        var last = existingPoints.Last().TimeOfValue;
        var expectedTimestamps = GenerateExpectedTimestamps(first.UtcDateTime, last.UtcDateTime.AddTicks(1), resolution);
        var existingTimestamps = new HashSet<DateTimeOffset>(existingPoints.Select(p => p.TimeOfValue));
        var filledPoints = new List<TimeSeriesDto>(existingPoints);

        foreach (var timestamp in expectedTimestamps)
        {
            if (!existingTimestamps.Contains(timestamp))
            {
                var simulatedValue = GetSimulatedValue(timestamp, existingPoints, resolution);
                filledPoints.Add(new TimeSeriesDto
                {
                    TimeOfValue = timestamp,
                    Value = simulatedValue,
                    QualityOfValue = SimulatedQuality,
                    TimeResolution = segment.TimeResolution,
                    QuantityCodeValue = "kWh"
                });
            }
        }

        return new TimeSeriesSegmentDto
        {
            TimeResolution = segment.TimeResolution,
            ReadingResolution = segment.ReadingResolution,
            CompanyId = segment.CompanyId,
            PeriodStart = segment.PeriodStart,
            PeriodEnd = segment.PeriodEnd,
            Points = filledPoints.OrderBy(p => p.TimeOfValue).ToList()
        };
    }

    public MeteringPointTimeSeriesDto GenerateFullSeries(
        string meteringPointId,
        string? parentMeteringPointId,
        DateTime startDate,
        DateTime endDate)
    {
        var resolution = TimeSpan.FromHours(1);
        var timestamps = GenerateExpectedTimestamps(startDate, endDate, resolution);

        var points = timestamps.Select(timestamp => new TimeSeriesDto
        {
            TimeOfValue = timestamp,
            Value = GenerateRealisticConsumption(timestamp),
            QualityOfValue = SimulatedQuality,
            TimeResolution = "Hour",
            QuantityCodeValue = "kWh"
        }).ToList();

        return new MeteringPointTimeSeriesDto
        {
            MeteringPointId = meteringPointId,
            ParentMeteringPointId = parentMeteringPointId,
            Unit = "kWh",
            Segments =
            [
                new TimeSeriesSegmentDto
                {
                    TimeResolution = "Hour",
                    Points = points
                }
            ]
        };
    }

    public TimeSpan DetectResolution(List<TimeSeriesDto> points)
    {
        if (points.Count < 2)
            return TimeSpan.FromHours(1);

        var gaps = new List<TimeSpan>();
        var sortedPoints = points.OrderBy(p => p.TimeOfValue).ToList();

        for (int i = 1; i < Math.Min(sortedPoints.Count, 100); i++)
        {
            var gap = sortedPoints[i].TimeOfValue - sortedPoints[i - 1].TimeOfValue;
            if (gap > TimeSpan.Zero && gap <= TimeSpan.FromDays(1))
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
            return TimeSpan.FromHours(1);

        return gaps
            .GroupBy(g => g)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
    }

    private List<DateTimeOffset> GenerateExpectedTimestamps(DateTime startDate, DateTime endDate, TimeSpan resolution)
    {
        var timestamps = new List<DateTimeOffset>();
        var current = new DateTimeOffset(startDate, TimeSpan.Zero);
        var end = new DateTimeOffset(endDate, TimeSpan.Zero);

        while (current < end)
        {
            timestamps.Add(current);
            current = current.Add(resolution);
        }

        return timestamps;
    }

    private decimal GetSimulatedValue(DateTimeOffset timestamp, List<TimeSeriesDto> existingPoints, TimeSpan resolution)
    {
        var candidates = new List<TimeSeriesDto>();

        for (int weekOffset = -4; weekOffset <= 4; weekOffset++)
        {
            if (weekOffset == 0) continue;

            var targetTime = timestamp.AddDays(weekOffset * 7);
            var match = existingPoints.FirstOrDefault(p =>
                Math.Abs((p.TimeOfValue - targetTime).TotalMinutes) < resolution.TotalMinutes / 2);

            if (match != null)
            {
                candidates.Add(match);
            }
        }

        if (candidates.Count > 0)
        {
            return Math.Round(candidates.Average(c => c.Value), 3);
        }

        var sameHourPoints = existingPoints
            .Where(p => p.TimeOfValue.Hour == timestamp.Hour)
            .OrderBy(p => Math.Abs((p.TimeOfValue - timestamp).TotalDays))
            .Take(7)
            .ToList();

        if (sameHourPoints.Count > 0)
        {
            return Math.Round(sameHourPoints.Average(p => p.Value), 3);
        }

        if (existingPoints.Count > 0)
        {
            return Math.Round(existingPoints.Average(p => p.Value), 3);
        }

        return GenerateRealisticConsumption(timestamp);
    }

    private decimal GenerateRealisticConsumption(DateTimeOffset timestamp)
    {
        decimal baseLoad = 75m;

        var hour = timestamp.Hour;
        decimal timeOfDayFactor = hour switch
        {
            >= 0 and < 6 => 0.6m,
            >= 6 and < 9 => 1.2m,
            >= 9 and < 17 => 0.9m,
            >= 17 and < 21 => 1.3m,
            _ => 0.8m
        };

        decimal dayOfWeekFactor = timestamp.DayOfWeek switch
        {
            DayOfWeek.Saturday => 0.85m,
            DayOfWeek.Sunday => 0.8m,
            _ => 1.0m
        };

        var variation = 1.0m + (((timestamp.DayOfYear * 24 + hour) % 10) - 5) * 0.02m;

        var value = baseLoad * timeOfDayFactor * dayOfWeekFactor * variation;
        return Math.Round(value, 3);
    }
}
