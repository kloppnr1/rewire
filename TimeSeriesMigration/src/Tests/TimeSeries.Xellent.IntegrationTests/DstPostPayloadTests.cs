using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.SettlApi.Models;
using TimeSeries.Cli.Services;
using TimeSeries.Core.Models;

namespace TimeSeries.Xellent.IntegrationTests;

/// <summary>
/// End-to-end DST regression: extract a known MP from AXDB50_TEST across a DST-transition day,
/// build the exact <c>SettlTimeSeriesRequestDto</c> that would be POST'ed to Settl, and
/// snapshot the wire JSON against a committed golden.
/// <para>Proves that the full pipeline (Xellent → normalisation → wire payload) produces
/// the correct UTC timestamps and carry-over values for spring-forward and fall-back days.</para>
/// </summary>
public class DstPostPayloadTests : IntegrationTestBase
{
    // Fixed test parameters — MP 571313115242231955 (kunde 405013 i HOL), El-ekstern.
    private const string MeteringPoint = "571313115242231955";
    private const string DataAreaId = "hol";
    private const string DeliveryCategory = "El-ekstern";

    // Deterministic stand-ins for runtime-random payload fields (TransactionId/Revision/RegistrationTime
    // would otherwise drift between runs). Asserter masks TransactionId, but Revision/RegistrationTime
    // aren't in its mask list — fix them here so the golden stays stable.
    private static readonly Guid FixedTransactionId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FixedRegistrationTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedRevision = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [Fact]
    public Task SpringForward_PostPayloadMatchesGolden()
        => RunDstDayAsync(new DateTime(2024, 3, 31), "spring-forward");

    [Fact]
    public Task FallBack_PostPayloadMatchesGolden()
        => RunDstDayAsync(new DateTime(2024, 10, 27), "fall-back");

    private async Task RunDstDayAsync(DateTime localDay, string scenario)
    {
        var fromLocal = DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified);
        var toLocal = fromLocal.AddDays(1).AddTicks(-1);

        var service = new TimeSeriesExtractionService(Context, DeliveryCategory, new[] { MeteringPoint });
        var extracted = await service.ExtractTimeSeriesAsync(Array.Empty<string>(), fromLocal, toLocal);
        var mpTs = extracted.Single();

        var fileDto = new TimeSeriesFileDto
        {
            TransactionId = FixedTransactionId,
            RegistrationTime = FixedRegistrationTime,
            Revision = FixedRevision,
            AccountForQuantityQuality = true,
            Points = mpTs.Segments.SelectMany(seg => seg.Points).Select(ts => new SettlTimeSeriesPointDto
            {
                Time = ts.TimeOfValue.UtcDateTime,
                Quantity = ts.Value,
                Quality = "Measured"
            }).ToList()
        };
        var request = fileDto.ToApiRequest();

        var wireJson = JsonSerializer.Serialize(request, WireOptions);
        var node = JsonNode.Parse(wireJson);

        JsonGoldenAsserter.AssertMatchesGolden(node!, GoldenPath(scenario, "settl-post-payload.json"));
    }

    private static string GoldenPath(string scenario, string file)
    {
        var baseDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Goldens", "dst", scenario));
        return Path.Combine(baseDir, file);
    }
}
