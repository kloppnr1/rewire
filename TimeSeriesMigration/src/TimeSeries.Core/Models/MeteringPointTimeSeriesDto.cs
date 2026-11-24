namespace TimeSeries.Core.Models;

public class MeteringPointTimeSeriesDto
{
    public string MeteringPointId { get; set; } = string.Empty;
    public string Unit { get; set; } = "kWh";
    public string? ParentMeteringPointId { get; set; } = null;
    public List<TimeSeriesSegmentDto> Segments { get; set; } = new();

    /// <summary>
    /// Xellent series (as "RecId/CompanyId") that had points in the window but no agreement
    /// period to place them in. Their points are not in <see cref="Segments"/>; the post
    /// command fails the metering point rather than migrate it with a silent gap.
    /// </summary>
    public List<string> SeriesWithoutAgreement { get; set; } = new();

    /// <summary>
    /// Set when the segments were only counted (see <see cref="TimeSeriesSegmentDto.CountedPoints"/>):
    /// the points in the segments' periods that fall before the delivery boundary.
    /// </summary>
    public int? CountedPointsBeforeBoundary { get; set; }

    /// <summary>
    /// The resolutions Xellent says the metering point was read at inside the window ("Hour", "Quarter").
    /// More than one is a resolution change, whatever the series hold.
    /// </summary>
    public List<string> ReadingResolutionsInWindow { get; set; } = new();

    /// <summary>
    /// Known Xellent issue: values stored in a series of one resolution for a stretch Xellent says was
    /// read at another, with no series of that resolution (e.g. hourly values where it reads quarter-
    /// hourly). One description per stretch. This preserves source evidence even when outgoing
    /// hourly-to-quarter conversion resolves the mismatch.
    /// </summary>
    public List<string> ValuesWithWrongResolution { get; set; } = new();
}
