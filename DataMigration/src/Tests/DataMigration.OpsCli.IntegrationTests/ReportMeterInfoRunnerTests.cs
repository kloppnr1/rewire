using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Tests;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;
using DataMigration.XellentData.Entities;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// report-meter-info end to end: the real runner, the real Xellent loader against an in-memory
/// Xellent, and the real Settl clients against a fake tenant. The golden is the one recorded from
/// the live run; the fake data is built to the same facts (types, sub-types, occurrence, annual
/// volumes and meters per metering point), so a change in how the runner compares or prints them
/// shows up as a diff.
/// </summary>
public sealed class ReportMeterInfoRunnerTests : IAsyncLifetime
{
    private static readonly DateOnly Start = new(2026, 5, 1);

    // (account, metering point, Settl sub-type, Settl EAV, Settl meter, Settl factor,
    //  Xellent sub-type code, Xellent EAV, Xellent meter, Xellent factor)
    private static readonly (string Account, string Mp, string SettlSub, double SettlEav, string? SettlMeter, double SettlFactor,
        int XellentSub, decimal XellentEav, string XellentMeter, decimal XellentFactor)[] Points =
    [
        (FixTestAccounts.HappyPath, "571313114900385351", "Virtual", 77777, null, 1, 2, 2906, "", 0),
        (FixTestAccounts.AnnualConsumption, "571313113161269318", "Physical", 6491, "4186795", 1, 1, 8959, "4186795", 1),
        (FixTestAccounts.AnnualConsumption, "571313115242231955", "Physical", 2516, "30426225", 1, 1, 2368, "30426225", 1),
    ];

    private readonly FakeSettl _settl = new();
    private readonly FakeXellent _xellent = new();

    public async Task InitializeAsync()
    {
        var mp = _settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);
        foreach (var account in Points.Select(p => p.Account).Distinct())
        {
            var customerId = Guid.NewGuid();
            await _settl.CustomerClient.CreateAsync(new CreateCustomersRequest
            {
                Customers = [new CreateCustomerDto { Id = customerId, CustomerNumber = account }],
            });
            var links = new List<CustomerMeteringPointDto>();
            foreach (var p in Points.Where(p => p.Account == account))
            {
                var id = Guid.NewGuid();
                await mp.CreateV4Async(new CreateMeteringPointsV4Request
                {
                    MeteringPointCreateObjectDtos = [new MeteringPointCreateObjectV4Dto
                    {
                        Id = id,
                        Identification = new MeteringPointIdentifiersDto { Start = Start, MeteringPointIdentification = p.Mp },
                        MeterCharacteristics = new MeteringPointMeterCharacteristicsDto
                        {
                            Start = Start, MeteringPointType = "Consumption", MeteringPointSubType = p.SettlSub,
                            EstimatedAnnualVolume = new QuantityDto { Unit = "kWh", Kind = "Na", Value = p.SettlEav },
                            Meters = [new MeteringPointMeterDto
                                { MeterIdentifier = p.SettlMeter, MultiplicationFactor = p.SettlFactor, MeterReadingOccurrence = "Hourly" }],
                        },
                        DeliveryStatus = new MeteringPointDeliveryStatusDto { Start = Start, InDelivery = true },
                    }],
                });
                links.Add(new CustomerMeteringPointDto { MeteringPointId = id, ExternalIdentifier = p.Mp, Start = Start });

                var rows = XellentRows.For(new(p.Account, "C" + p.Mp[^6..], p.Mp, new(2020, 1, 1), XellentRows.Open),
                    withCustomer: false);
                var delpoint = rows.OfType<ExuDelpoint>().Single();
                _xellent.Add(rows);
                _xellent.Add(
                    new ExuMeterTableTrade
                    {
                        Dataareaid = "hol", Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Companyid = delpoint.Companyid,
                        Agreementnum = delpoint.Attachmentnum, Delpointnum = delpoint.Num, Startdate = new(2020, 1, 1),
                        Enddate = XellentRows.Open, Meternum = p.XellentMeter, Counterconstant = p.XellentFactor,
                        MeteringPointDhSubType = p.XellentSub,
                    },
                    new ExuTimereadingsPeriod
                    {
                        Dataareaid = "hol", Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Companyid = delpoint.Companyid,
                        Attachmentnum = delpoint.Attachmentnum, Delpointnum = delpoint.Num, Startdate = new(2020, 1, 1),
                        Enddate = XellentRows.Open, Seriesresolution = 0,
                    },
                    new ExuReadingTableExt
                    {
                        Recid = Random.Shared.NextInt64(1_000_000, 9_000_000), Dataareaid = "hol",
                        Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Companyid = delpoint.Companyid,
                        Agreementnum = delpoint.Attachmentnum, Delpointnum = delpoint.Num, Readingdate = new(2025, 5, 31),
                        Expectedconsumptionperyear = p.XellentEav,
                    });
            }
            await _settl.CustomerClient.AddMeteringPointsAsync(customerId, new AddCustomerMeteringPointsRequest { CustomerMeteringPoints = links });
            _xellent.Add(new CustTable { Accountnum = account, Dataareaid = "hol" });
        }
    }

    public Task DisposeAsync()
    {
        _settl.Dispose();
        _xellent.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FixtureAccounts_MatchesGolden()
    {
        var loader = new XellentDomainLoader(_xellent.ConnectionString);
        var runner = new ReportMeterInfoRunner(
            _settl.CustomerClient,
            _settl.MeteringPointClient,
            loader);

        var output = new StringWriter();
        var exitCode = await runner.RunAsync(new ReportMeterInfoRunOptions
        {
            ProfileName = "golden-test",
            Accounts = [FixTestAccounts.HappyPath, FixTestAccounts.AnnualConsumption],
            DataAreaId = "hol",
            ReferenceDate = new DateTime(2026, 5, 1),
            ShowAll = true,
        }, output);

        Assert.Equal(0, exitCode);
        CsvGoldenAsserter.AssertMatchesGolden(
            output.ToString(),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Goldens", "report-meter-info", "fixture-accounts.csv")));
    }
}
