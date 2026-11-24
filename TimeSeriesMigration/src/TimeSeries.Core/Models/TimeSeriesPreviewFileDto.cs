namespace TimeSeries.Core.Models;

/// <summary>Read-only source-to-outgoing calculation; not evidence of values saved in Settl.</summary>
public class TimeSeriesPreviewFileDto
{
    public string MeteringPointId { get; set; } = "";
    public string? Error { get; set; }
    public int SourceCount { get; set; }
    public int ExcludedCount { get; set; }
    public int OutgoingCount { get; set; }
    public List<TimeSeriesPreviewPointDto> Points { get; set; } = [];
}

public class TimeSeriesPreviewPointDto
{
    public TimeSeriesPreviewValueDto Source { get; set; } = new();
    public List<TimeSeriesPreviewValueDto> Outgoing { get; set; } = [];
    public bool Excluded { get; set; }
}

public class TimeSeriesPreviewValueDto
{
    public DateTimeOffset Time { get; set; }
    public decimal Quantity { get; set; }
    public string Quality { get; set; } = "";
    public string Resolution { get; set; } = "";
}
