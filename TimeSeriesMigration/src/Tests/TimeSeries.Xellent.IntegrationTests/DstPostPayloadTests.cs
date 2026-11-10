using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.SettlApi.Models;
using TimeSeries.Cli.Services;
using TimeSeries.Core.Models;

namespace TimeSeries.Xellent.IntegrationTests;

/// <summary>
/// End-to-end DST regression: extract an MP across a DST-transition day from a seeded Xellent
/// copy, build the exact <c>SettlTimeSeriesRequestDto</c> that would be POST'ed to Settl, and
/// snapshot the wire JSON against a committed golden. The seed is the golden's own points (recorded
/// from AXDB50_TEST) plus one point just outside the day on each side.
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

    // The UTC hours just outside each Danish day (00:00 local = 23:00Z in winter, 22:00Z in summer).
    // Seeded with a value no golden point has, so a window that takes one in shows up as drift.
    [Fact]
    public Task SpringForward_PostPayloadMatchesGolden()
        => RunDstDayAsync(new DateTime(2024, 3, 31), "spring-forward",
            new DateTime(2024, 3, 30, 22, 0, 0), new DateTime(2024, 3, 31, 22, 0, 0));

    [Fact]
    public Task FallBack_PostPayloadMatchesGolden()
        => RunDstDayAsync(new DateTime(2024, 10, 27), "fall-back",
            new DateTime(2024, 10, 26, 21, 0, 0), new DateTime(2024, 10, 27, 23, 0, 0));

    private async Task RunDstDayAsync(DateTime localDay, string scenario, DateTime beforeUtc, DateTime afterUtc)
    {
        var points = GoldenPoints(GoldenPath(scenario, "settl-post-payload.json"));
        Assert.Equal(scenario == "spring-forward" ? 23 : 25, points.Count);
        Seeder.SeedTimeSeriesPoints(MeteringPoint, DataMigration.Domain.TimeSeries.TimeResolution.Hour,
            [(beforeUtc, 9.99m), .. points, (afterUtc, 9.99m)]);

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
