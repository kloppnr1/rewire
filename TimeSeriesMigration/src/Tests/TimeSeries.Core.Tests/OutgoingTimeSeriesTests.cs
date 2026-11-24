using TimeSeries.Core.Models;

namespace TimeSeries.Core.Tests;

public class OutgoingTimeSeriesTests
{
    [Theory]
    [InlineData("0.001")]
    [InlineData("0")]
    [InlineData("-1.234")]
    [InlineData("0.00004")]
    public void Split_PreservesTotalMetadataAndSource(string text)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var source = Segment(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, value);
        var outgoing = OutgoingTimeSeries.Prepare(source);
        Assert.Equal("Quarter", outgoing.TimeResolution);
        Assert.Equal(value, outgoing.Points.Sum(p => p.Value));
        Assert.Equal(4, outgoing.Points.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(source.Points[0].TimeOfValue.AddMinutes(i * 15), outgoing.Points[i].TimeOfValue);
            Assert.Equal(value / 4m, outgoing.Points[i].Value);
            Assert.Equal("Measured", outgoing.Points[i].QualityOfValue);
            Assert.Equal("Active energy", outgoing.Points[i].QuantityCodeValue);
        }
        Assert.Single(source.Points);
        Assert.Equal("Hour", source.TimeResolution);
        Assert.Equal(value, source.Points[0].Value);
    }

    [Theory]
    [InlineData("0.0001")]
    [InlineData("-0.00001")]
    public void InexactSplit_IsRejected(string text)
    {
        var source = Segment(DateTimeOffset.UtcNow, 1, decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Throws<InvalidOperationException>(() => OutgoingTimeSeries.Prepare(source));
    }

    [Theory]
    [InlineData(3, 30, 23, 23)]
    [InlineData(10, 26, 22, 25)]
    public void DanishDstDays_RemainContinuousInUtc(int month, int day, int hour, int count)
    {
        var start = new DateTimeOffset(2024, month, day, hour, 0, 0, TimeSpan.Zero);
        var source = Segment(start, count, 1m);
        var outgoing = OutgoingTimeSeries.Prepare(source);
        Assert.Equal(count * 4, outgoing.Points.Count);
        Assert.Equal(count, outgoing.Points.Sum(p => p.Value));
        Assert.Equal(Enumerable.Range(0, count * 4).Select(i => start.AddMinutes(i * 15)), outgoing.Points.Select(p => p.TimeOfValue));
    }

    [Fact]
    public void NativeQuarterAndMissingReadingPeriod_AreUnchanged()
    {
        var source = Segment(DateTimeOffset.UtcNow, 1, 0.0001m);
        source.ReadingResolution = null;
        Assert.Same(source, OutgoingTimeSeries.Prepare(source));
        source.TimeResolution = "Quarter";
        source.ReadingResolution = "Quarter";
        Assert.Same(source, OutgoingTimeSeries.Prepare(source));
    }

    private static TimeSeriesSegmentDto Segment(DateTimeOffset start, int count, decimal value) => new()
    {
        TimeResolution = "Hour", ReadingResolution = "Quarter",
        Points = Enumerable.Range(0, count).Select(i => new TimeSeriesDto
        {
            TimeOfValue = start.AddHours(i), Value = value, TimeResolution = "Hour",
            QualityOfValue = "Measured", QuantityCodeValue = "Active energy"
        }).ToList()
    };

    [Fact]
    public void Preview_PairsActualSourceWithPreparedValues_AndKeepsUnconvertedSegments()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var hourly = Segment(start, 1, 2m);
        hourly.ReadingResolution = "Hour";
        var converted = Segment(start.AddHours(1), 1, 1.234m);
        var native = Segment(start.AddHours(2), 1, 0.12345m);
        native.TimeResolution = "Quarter";
        var source = new MeteringPointTimeSeriesDto { MeteringPointId = "fixture", Segments = [native, converted, hourly] };

        var preview = OutgoingTimeSeries.Preview(source, null);

        Assert.Null(preview.Error);
        Assert.Equal(3, preview.SourceCount);
        Assert.Equal(6, preview.OutgoingCount);
        Assert.Equal(start, preview.Points[0].Source.Time);
        Assert.Equal(2m, Assert.Single(preview.Points[0].Outgoing).Quantity);
        Assert.Equal("Hour", preview.Points[0].Outgoing[0].Resolution);
        var split = preview.Points[1];
        Assert.Equal(1.234m, split.Source.Quantity);
        Assert.Equal("Hour", split.Source.Resolution);
        Assert.Equal(Enumerable.Range(0, 4).Select(i => start.AddHours(1).AddMinutes(i * 15)), split.Outgoing.Select(p => p.Time));
        Assert.All(split.Outgoing, p => { Assert.Equal(0.3085m, p.Quantity); Assert.Equal("Quarter", p.Resolution); });
        Assert.Equal(split.Source.Quantity, split.Outgoing.Sum(p => p.Quantity));
        Assert.Equal(0.12345m, Assert.Single(preview.Points[2].Outgoing).Quantity);
        Assert.Equal(1.234m, Assert.Single(converted.Points).Value);
    }

    [Fact]
    public void Preview_BlocksWholeMeterOnPrecisionError_ButExcludedValuesDoNotBlock()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var segment = Segment(start, 2, 1m);
        segment.Points[0].Value = 0.0001m;
        var source = new MeteringPointTimeSeriesDto { Segments = [segment] };
        var blocked = OutgoingTimeSeries.Preview(source, null);
        Assert.Contains("five decimal", blocked.Error);
        Assert.Equal(2, blocked.SourceCount);
        Assert.Equal(0, blocked.OutgoingCount);
        Assert.All(blocked.Points, p => Assert.Empty(p.Outgoing));

        var included = OutgoingTimeSeries.Preview(source, start.AddHours(1));
        Assert.Null(included.Error);
        Assert.Equal(1, included.ExcludedCount);
        Assert.Equal(4, included.OutgoingCount);
        Assert.True(included.Points[0].Excluded);
        Assert.Equal(0.0001m, included.Points[0].Source.Quantity);
        Assert.Empty(included.Points[0].Outgoing);
        Assert.False(included.Points[1].Excluded);
        Assert.All(included.Points[1].Outgoing, p => Assert.Equal(0.25m, p.Quantity));
    }
}
