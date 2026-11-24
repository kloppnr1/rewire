using System.Text.Json;
using DataMigration.SettlApi.Models;
using TimeSeries.Core.Models;

namespace TimeSeries.Cli.Services;

public class JsonExportService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public async Task<int> ExportTimeSeriesAsync(
        List<MeteringPointTimeSeriesDto> data,
        string outputPath,
        Action<string>? log = null)
    {
        var fileCount = 0;
        var dateStamp = DateTime.Now.ToString("yyyy-MM-dd");

        foreach (var meteringPoint in data)
        {
            // Én fil per segment — Settl modellerer resolution per TS, så et MP med
            // flere segmenter (typisk Hour → Quarter ved company-skift) producerer flere filer.
            for (var index = 0; index < meteringPoint.Segments.Count; index++)
            {
                var segment = meteringPoint.Segments[index];
                var exportDto = new TimeSeriesFileDto
                {
                    TransactionId = Guid.NewGuid(),
                    RegistrationTime = DateTimeOffset.UtcNow,
                    Revision = DateTimeOffset.UtcNow,
                    AccountForQuantityQuality = true,
                    TimeResolution = MapTimeResolution(segment.TimeResolution),
                    Points = segment.Points.Select(ts => new SettlTimeSeriesPointDto
                    {
                        Time = ts.TimeOfValue.UtcDateTime,
                        Quantity = ts.Value,
                        Quality = "Measured"
                    }).ToList()
                };

                var resolutionSlug = segment.TimeResolution.Length > 0 ? segment.TimeResolution : "Unknown";
                // Dated reading periods can split one stored resolution into several source segments.
                // Keep all original values instead of overwriting an earlier segment's export.
                if (meteringPoint.Segments.Count(s => s.TimeResolution == segment.TimeResolution) > 1)
                    resolutionSlug += $"_{index + 1}";
                var fileName = meteringPoint.Segments.Count > 1
                    ? $"TimeSeries_{meteringPoint.MeteringPointId}_{resolutionSlug}_{dateStamp}.json"
                    : $"TimeSeries_{meteringPoint.MeteringPointId}_{dateStamp}.json";
                var filePath = Path.Combine(outputPath, fileName);

                var json = JsonSerializer.Serialize(exportDto, _jsonOptions);
                await File.WriteAllTextAsync(filePath, json);

                log?.Invoke($"  {meteringPoint.MeteringPointId} [{resolutionSlug}]: {segment.Points.Count} records");

                fileCount++;
            }
        }

        return fileCount;
    }

    private static TimeResolutionDto? MapTimeResolution(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution))
            return null;

        return resolution switch
        {
            "Month" => new TimeResolutionDto { Length = 1, Unit = "Month" },
            "Hour" => new TimeResolutionDto { Length = 1, Unit = "Hour" },
            "Quarter" => new TimeResolutionDto { Length = 15, Unit = "Minutes" },
            "Day" => new TimeResolutionDto { Length = 1, Unit = "Day" },
            "Year" => new TimeResolutionDto { Length = 1, Unit = "Year" },
            "Minute" => new TimeResolutionDto { Length = 1, Unit = "Minutes" },
            _ => null
        };
    }

    public async Task ExportOutgoingPreviewsAsync(List<MeteringPointTimeSeriesDto> data, string outputPath,
        DateTimeOffset? deliveryBoundary)
    {
        foreach (var meteringPoint in data)
        {
            var preview = OutgoingTimeSeries.Preview(meteringPoint, deliveryBoundary);
            await File.WriteAllTextAsync(Path.Combine(outputPath, $"OutgoingPreview_{meteringPoint.MeteringPointId}.json"),
                JsonSerializer.Serialize(preview, _jsonOptions));
        }
    }
}
