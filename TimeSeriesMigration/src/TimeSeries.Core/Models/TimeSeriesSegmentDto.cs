namespace TimeSeries.Core.Models;

/// <summary>
/// Et sammenhængende interval af time-series-points med ÉN resolution. Et metering
/// point kan have flere segmenter når Xellent har skiftet COMPANYID/resolution
/// undervejs (typisk Hour → Quarter ved et company-skift). Hvert segment mapper
/// til én Settl-TimeSeries (eksisterende eller nyoprettet).
/// </summary>
public class TimeSeriesSegmentDto
{
    public string TimeResolution { get; set; } = string.Empty;

    /// <summary>Dated Xellent reading resolution; null when no reading period is recorded.</summary>
    public string? ReadingResolution { get; set; }

    /// <summary>First invalid included value found by count-only conversion validation.</summary>
    public string? ConversionError { get; set; }

    /// <summary>
    /// Xellent COMPANYID som segmentet kommer fra. Bruges til logging/diagnostik
    /// når Settl-TS-matching slår fejl. Sendes ikke til Settl.
    /// </summary>
    public string? CompanyId { get; set; }

    /// <summary>
    /// First Danish calendar day covered by the agreement this series belongs to
    /// (EXU_AGREEMENTTABLE.STARTDATE via EXU_DELPOINT on the same COMPANYID).
    /// Null when the segment was not built from an agreement.
    /// </summary>
    public DateOnly? PeriodStart { get; set; }

    /// <summary>
    /// Day after the agreement's last day (exclusive end). Null for an open-ended agreement.
    /// </summary>
    public DateOnly? PeriodEnd { get; set; }

    public List<TimeSeriesDto> Points { get; set; } = new();

    /// <summary>
    /// Set when the segment was only counted, not read (a batch dry run): the number of points on or
    /// after the delivery boundary. <see cref="Points"/> is then empty.
    /// </summary>
    public int? CountedPoints { get; set; }

    /// <summary>With <see cref="CountedPoints"/>: the first of those points; null when there are none.</summary>
    public DateTimeOffset? FirstCountedPoint { get; set; }
}
