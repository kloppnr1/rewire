using DataMigration.OpsCli.Services.Reconcile;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Explicitly selected, read-only production SQL source check. No Settl API calls or personal fields.</summary>
public sealed class ProductionSettlementTypeSourceIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Known_production_E18_points_resolve_from_their_dated_owner_contracts()
    {
        var settings = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../DataMigration.Migration/appsettings.json"));
        var config = new ConfigurationBuilder().AddJsonFile(settings, optional: false)
            .AddUserSecrets<ProductionSettlementTypeSourceIntegrationTests>(optional: true).Build();
        var connection = config["SettlApi:Environments:production:XellentDatabase"]
            ?? config.GetConnectionString("XellentDatabase")
            ?? throw new InvalidOperationException("Production Xellent connection is not configured");
        var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection);
        output.WriteLine($"Xellent target: {target.DataSource}/{target.InitialCatalog}");
        Assert.Equal("AXDB50", target.InitialCatalog, ignoreCase: true);
        string[] points = ["570715000001565587", "570715000001598783", "570715000002531314"];
        var result = await MeteringPointAccountResolver.ResolveAsync(connection,
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
