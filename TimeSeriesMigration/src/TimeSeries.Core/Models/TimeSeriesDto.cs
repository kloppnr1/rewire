namespace TimeSeries.Core.Models;

public class TimeSeriesDto
{
    public DateTimeOffset TimeOfValue { get; set; }
    public decimal Value { get; set; }
    public string QuantityCodeValue { get; set; } = string.Empty;
    public string TimeResolution { get; set; } = string.Empty;
    public string QualityOfValue { get; set; } = string.Empty;
}
