// Metering point type and sub type on the create payload.
//
// The type comes from the grid connection (EGS_METERPOINTTYPECODEPERIOD.METERPOINTTYPECODE),
// the sub type from the meter trade row (EXU_METERTABLETRADE.METERINGPOINTDHSUBTYPE). Every
// seed group captured from live Xellent is a consumption point, so production had no coverage
// at all until seed group 990001 was added — it is hand-written for exactly this case and is
// the only group carrying METERINGPOINTDHSUBTYPE.
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services;
using DataMigration.Migration.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Tests.Extraction;

public class ProductionMeteringPointTests
{
    private static readonly DateTime ReferenceDate = new(2026, 4, 16);

    [Fact]
    public async Task ProductionMeteringPoint_IsSentAsProduction_WithSubTypeFromMeterTrade()
    {
        using var seeder = new XellentDbSeeder("990001");

        var mps = await ExtractMeteringPointsAsync(seeder, "990001");

        var mp = Assert.Single(mps);
        Assert.Equal("571313199000000017", mp.Identification?.MeteringPointIdentification);
        Assert.NotNull(mp.MeterCharacteristics);
        Assert.Equal("Production", mp.MeterCharacteristics!.MeteringPointType);
        Assert.Equal("Physical", mp.MeterCharacteristics.MeteringPointSubType);
    }

    [Fact]
    public async Task ConsumptionMeteringPoint_IsSentAsConsumption_AndHasNoSubTypeWithoutADhCode()
    {
        // 512812 is captured from live Xellent and carries no METERINGPOINTDHSUBTYPE, which is
        // what every captured group looks like — the sub type stays out of the payload.
        using var seeder = new XellentDbSeeder("512812");

        var mps = await ExtractMeteringPointsAsync(seeder, "512812");

        var mp = Assert.Single(mps);
        Assert.NotNull(mp.MeterCharacteristics);
        Assert.Equal("Consumption", mp.MeterCharacteristics!.MeteringPointType);
        Assert.Null(mp.MeterCharacteristics.MeteringPointSubType);
    }

    private static async Task<List<MeteringPointDto>> ExtractMeteringPointsAsync(
        XellentDbSeeder seeder, string account)
    {
        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(ReferenceDate),
            Commodity: Commodity.Electricity,
            IncludeEndedCutoff: new DateOnly(2000, 1, 1));
        var mapper = new XellentDomainMapper(seeder.Context, warnings, mapperOptions, MigrationConstants.DataAreaId);
        var loadResult = await mapper.LoadAsync([account]);

        var service = new SettlMappingService(loadResult, warnings, ReferenceDate,
            deliveryCategory: MigrationConstants.DeliveryCategoryElectricity);
        return await service.ExtractMeteringPointsAsync([account]);
    }
}
