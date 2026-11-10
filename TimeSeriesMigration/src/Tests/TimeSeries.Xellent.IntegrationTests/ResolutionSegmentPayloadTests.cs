using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using DataMigration.SettlApi.Models;
using TimeSeries.Cli.Services;
using TimeSeries.Core.Models;

namespace TimeSeries.Xellent.IntegrationTests;

/// <summary>
/// End-to-end multi-resolution regression, on a seeded Xellent copy built from the goldens (recorded
/// from AXDB50_TEST): extract an MP across the
/// COMPANYID/resolution switch at the 2023→2024 year boundary, where Xellent holds the same MP
/// as TWO EMS_TIMESERIES rows — an Hour-resolution row (ends 2023-12-31 22:00 UTC) and a
/// Quarter-resolution row (starts 2023-12-31 23:00 UTC = 2024-01-01 00:00 CET).
/// <para>Proves that extraction splits the data into one segment per resolution, and that each
/// segment maps to its own wire payload. The two payloads are snapshotted against committed
/// goldens so any drift in segmentation, ordering, or UTC timestamps is caught.</para>
/// <para>Companion to <see cref="DstPostPayloadTests"/>, which locks the single-resolution
/// (Hour-only) DST behaviour. This test is the only coverage that exercises BOTH Hour and
/// Quarter resolution from real Xellent data.</para>
/// </summary>
public class ResolutionSegmentPayloadTests : IntegrationTestBase
{
    // MP 571313103103321037 (HOL, El-ekstern) — Hour rows 2021-12-31..2023-12-31, then Quarter
    // from 2023-12-31 23:00 UTC onward. Verified against AXDB50_TEST 2026-05-27.
    private const string MeteringPoint = "571313103103321037";
    private const string DeliveryCategory = "El-ekstern";

    // Local window straddling the year boundary: 31 Dec 2023 (last full Hour day) + 1 Jan 2024
    // (first full Quarter day). Small + deterministic: 24 hourly + 96 quarterly points.
    private static readonly DateTime FromLocal = new(2023, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime ToLocal = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(-1);

    // Deterministic stand-ins for runtime-random payload fields — same approach as DstPostPayloadTests.
    private static readonly Guid FixedTransactionId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FixedRegistrationTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedRevision = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // The switch as Xellent holds it: the Hour series under one company, whose agreement ends on
    // 31 Dec 2023, and the Quarter series under another, from 1 Jan 2024. Plus one point just before
    // the window (Hour) and one just after it (Quarter), which must not be extracted.
    private void SeedSwitch()
    {
        var hour = GoldenPoints(GoldenPath("hour", "settl-post-payload.json"));
        var quarter = GoldenPoints(GoldenPath("quarter", "settl-post-payload.json"));
        Seeder.SeedTimeSeriesPoints(MeteringPoint, DataMigration.Domain.TimeSeries.TimeResolution.Hour,
            [(new DateTime(2023, 12, 30, 22, 0, 0), 9.99m), .. hour], companyId: "old",
            agreementEnd: new DateTime(2023, 12, 31));
        Seeder.SeedTimeSeriesPoints(MeteringPoint, DataMigration.Domain.TimeSeries.TimeResolution.Quarter,
            [.. quarter, (new DateTime(2024, 1, 1, 23, 0, 0), 9.99m)], companyId: "new",
            agreementStart: new DateTime(2024, 1, 1));
    }

    [Fact]
    public async Task CountedSegments_MatchExtractedOutgoingCounts_AndSqlPrecisionCheck()
    {
        SeedSwitch();
        var service = new TimeSeriesExtractionService(Context, DeliveryCategory, [MeteringPoint]);
        var extracted = (await service.ExtractTimeSeriesAsync([], FromLocal, ToLocal)).Single();
        var counted = await service.CountTimeSeriesAsync(MeteringPoint, FromLocal, ToLocal, null);
        Assert.Equal(extracted.Segments.Sum(OutgoingTimeSeries.PointCount), counted.Segments.Sum(OutgoingTimeSeries.PointCount));
        Assert.Equal(extracted.Segments.Select(OutgoingTimeSeries.Resolution), counted.Segments.Select(OutgoingTimeSeries.Resolution));

        // Exercise the exact decimal remainder predicate with SQL Server's real column precision.
        var ids = Context.EmsTimeseries.Where(t => t.Dataareaid == "hol" && t.Meteringpoint == MeteringPoint).Select(t => t.Recid);
        var values = Context.EmsTimeseriesValues.Where(v => v.Dataareaid == "hol" && ids.Contains(v.Timeseriesrefrecid)
            && v.Timeofvalue >= new DateTime(2023, 12, 30) && v.Timeofvalue < new DateTime(2024, 1, 2));
        var source = await values.Select(v => new { v.Timeofvalue, v.Value }).ToListAsync();
        Assert.NotEmpty(source);
        Assert.Equal(source.Count(v => !OutgoingTimeSeries.CanSplit(v.Value)),
            await values.CountAsync(v => v.Value % 0.00004m != 0m));
    }

    [Fact]
    public async Task TwoResolutionSegments_EachPayloadMatchesGolden()
    {
        SeedSwitch();
        var service = new TimeSeriesExtractionService(Context, DeliveryCategory, new[] { MeteringPoint });
        var extracted = await service.ExtractTimeSeriesAsync(Array.Empty<string>(), FromLocal, ToLocal);
        var mpTs = extracted.Single();

        // The MP must surface exactly two segments, one Hour and one Quarter.
        Assert.Equal(2, mpTs.Segments.Count);
        var hourSegment = mpTs.Segments.Single(s => s.TimeResolution == "Hour");
        var quarterSegment = mpTs.Segments.Single(s => s.TimeResolution == "Quarter");

        Assert.NotEmpty(hourSegment.Points);
        Assert.NotEmpty(quarterSegment.Points);

        // The Hour segment's points must all precede the Quarter segment's — the boundary is
        // contiguous (Hour ends one hour before Quarter starts), never interleaved.
        Assert.True(hourSegment.Points.Max(p => p.TimeOfValue) < quarterSegment.Points.Min(p => p.TimeOfValue));

        AssertSegmentMatchesGolden(hourSegment, "hour");
        AssertSegmentMatchesGolden(quarterSegment, "quarter");
    }

    private static void AssertSegmentMatchesGolden(TimeSeriesSegmentDto segment, string resolutionSlug)
    {
        var fileDto = new TimeSeriesFileDto
        {
            TransactionId = FixedTransactionId,
            RegistrationTime = FixedRegistrationTime,
            Revision = FixedRevision,
            AccountForQuantityQuality = true,
            Points = segment.Points.Select(ts => new SettlTimeSeriesPointDto
            {
                Time = ts.TimeOfValue.UtcDateTime,
                Quantity = ts.Value,
                Quality = "Measured"
            }).ToList()
        };
        var request = fileDto.ToApiRequest();

        var wireJson = JsonSerializer.Serialize(request, WireOptions);
        var node = JsonNode.Parse(wireJson);

        JsonGoldenAsserter.AssertMatchesGolden(node!, GoldenPath(resolutionSlug, "settl-post-payload.json"));
    }

    private static string GoldenPath(string resolutionSlug, string file)
    {
        var baseDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Goldens", "resolution", MeteringPoint, resolutionSlug));
        return Path.Combine(baseDir, file);
    }
}
