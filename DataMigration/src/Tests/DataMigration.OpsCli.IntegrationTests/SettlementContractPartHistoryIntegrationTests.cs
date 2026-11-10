using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.Core;
using DataMigration.OpsCli.Services;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>The live/log contract-part projections, run as real queries against an in-memory Xellent.</summary>
public sealed class SettlementContractPartHistoryIntegrationTests : IDisposable
{
    private readonly FakeXellent _xellent = new();

    public SettlementContractPartHistoryIntegrationTests()
    {
        // Two electricity points whose contracts ended, plus one point nobody asks about.
        _xellent.Add(XellentRows.For(new("400001", "C400001", "571313100000000101", new(2020, 1, 1), new(2025, 8, 31))));
        _xellent.Add(XellentRows.For(new("400002", "C400002", "571313100000000102", new(2021, 3, 1), new(2026, 4, 30))));
        _xellent.Add(XellentRows.For(new("400003", "C400003", "571313100000000103", new(2021, 3, 1), new(2026, 4, 30))));
        // A part deleted after the reference day survives only in the log.
        _xellent.Add(new ExuContractPartLog
        {
            Recid = 9_100_001, Dataareaid = "hol", Contractnum = "C400002", Instagreenum = "AC400002", Companyid = "for",
            Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Productnum = "V Kvartal",
            Contractpartstartdate = new(2021, 3, 1), Contractpartenddate = XellentRows.Open, Logdatetime = new(2026, 5, 2),
        });
        // Charging history for two accounts.
        _xellent.Add(XellentRows.ChargeSubscription("339097", "C339097A", "OAboEjeMEL", new(2025, 1, 1), new(2026, 7, 31)));
        _xellent.Add(XellentRows.ChargeSubscription("445400", "C445400A", "O HCLejML", new(2025, 1, 1), XellentRows.Open));
    }

    public void Dispose() => _xellent.Dispose();

    private XellentDbContext Context() => _xellent.NewContext();

    [Fact]
    public async Task Electricity_history_queries_use_only_requested_points_and_dated_type_evidence()
    {
        await using var context = Context();
        var sample = await (
            from c in context.ExuContractTables
            join p in context.ExuContractPartTables on new { c.Contractnum,c.Companyid,c.Dataareaid }
                equals new { p.Contractnum,p.Companyid,p.Dataareaid }
            join d in context.ExuDelpoints on new { Attachmentnum = p.Instagreenum,p.Companyid,p.Dataareaid,p.Deliverycategory }
                equals new { d.Attachmentnum,d.Companyid,d.Dataareaid,d.Deliverycategory }
            where c.Dataareaid == "hol" && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                && c.Contractenddate > new DateTime(2020,1,1) && c.Contractenddate < new DateTime(2100,1,1)
            orderby d.Meteringpoint
            select new { d.Meteringpoint,c.Contractenddate }).Distinct().Take(2).ToListAsync();
        Assert.NotEmpty(sample);
        var dates = sample.GroupBy(r => r.Meteringpoint).ToDictionary(g => g.Key,
            g => DateOnly.FromDateTime(g.First().Contractenddate).AddDays(1));
        var result = await SettlementContractPartResolver.ResolveAsync(context,dates,"hol");
        Assert.NotEmpty(result.Evidence);
        Assert.All(result.Resolution.Pairs,pair => Assert.Contains(pair.MpId,dates.Keys));
        Assert.All(result.Evidence,row =>
        {
            Assert.Contains(row.MeteringPointIdentification,dates.Keys);
            Assert.Equal(dates[row.MeteringPointIdentification].AddDays(-1),row.ReferenceDate);
            Assert.True(row.Source is "Live" or "ContractPartLog");
            Assert.NotNull(row.Company);
            Assert.NotNull(row.Agreement);
        });
        Assert.DoesNotContain(result.Evidence, row => row.MeteringPointIdentification == "571313100000000103");
        Assert.All(result.Resolution.MeterPointTypesByMpId?.Values ?? [],type => Assert.NotEqual("ReadFailed",type.ReadStatus));
    }

    [Fact]
    public async Task Charging_history_queries_preserve_the_requested_account_and_reference_date()
    {
        await using var context = Context();
        (string Account,DateOnly Date)[] requests = [("339097",new(2026,8,31)),("339097",new(2026,7,31)),("445400",new(2026,8,31))];
        var result = await SettlementChargingHistoryReader.ReadAsync(context,requests,"hol");
        Assert.NotEmpty(result.Evidence);
        Assert.All(result.Products.Keys,key => Assert.Contains(key,requests));
        Assert.All(result.Evidence,row =>
        {
            Assert.Contains((row.Account,row.ReferenceDate),requests);
            Assert.True(row.Source is "Live" or "ContractPartLog");
        });
        Assert.All(result.Products.Values,products => Assert.Equal(products.Count,products.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }
}
