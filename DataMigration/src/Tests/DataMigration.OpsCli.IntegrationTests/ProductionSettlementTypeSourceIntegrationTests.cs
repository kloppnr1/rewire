using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData.Entities;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// The settlement-type source on the shape the production check was written for: E18 (production)
/// points whose owner contract ended the day before change of supplier. Runs the resolver's real
/// queries against an in-memory Xellent. No Settl calls, no personal fields.
/// </summary>
public sealed class ProductionSettlementTypeSourceIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Known_production_E18_points_resolve_from_their_dated_owner_contracts()
    {
        using var xellent = new FakeXellent();
        string[] points = ["570715000001565587", "570715000001598783", "570715000002531314"];
        for (var i = 0; i < points.Length; i++)
        {
            var rows = XellentRows.For(new($"50000{i}", $"C50000{i}", points[i], new(2020, 1, 1), new(2026, 8, 31), TypeCode: "PROD"));
            // The point was consumption before it became production: the dated type must win.
            var type = rows.OfType<EgsMeterpointTypecodeperiod>().Single();
            type.Startdate = new DateTime(2024, 1, 1);
            xellent.Add(rows);
            xellent.Add(new EgsMeterpointTypecodeperiod
            {
                Dataareaid = type.Dataareaid, Deliverycategory = type.Deliverycategory, Companyid = type.Companyid,
                Attachmentnum = type.Attachmentnum, Delpointnum = type.Delpointnum,
                Startdate = new DateTime(2015, 1, 1), Enddate = new DateTime(2023, 12, 31), Meterpointtypecode = "FORB",
                Recid = 9_200_000 + i,
            });
        }

        var result = await MeteringPointAccountResolver.ResolveAsync(xellent.ConnectionString,
            points.ToDictionary(p => p, _ => new DateOnly(2026, 9, 1)), "hol", includeMeterPointTypes: true);
        foreach (var point in points)
        {
            var evidence = result.MeterPointTypesByMpId?.GetValueOrDefault(point);
            output.WriteLine($"{point}: type={evidence?.Code ?? "unknown"}; status={evidence?.ReadStatus ?? "NotResolved"}");
            Assert.Contains(result.Pairs, pair => pair.MpId == point);
            Assert.NotNull(evidence);
            Assert.Equal("Read", evidence.ReadStatus);
            Assert.Equal("PROD", evidence.Code);
            Assert.Equal(new DateOnly(2026, 8, 31), result.OwnerContractByMpId[point].EndDate);
        }
    }
}
