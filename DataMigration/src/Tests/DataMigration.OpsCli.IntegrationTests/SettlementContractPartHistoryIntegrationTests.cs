using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.Core;
using DataMigration.OpsCli.Services;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Read-only checks of the new live/log SQL projections; requires corporate connectivity.</summary>
public sealed class SettlementContractPartHistoryIntegrationTests(SettlFeaturetestFixture fixture)
    : IClassFixture<SettlFeaturetestFixture>
{
    private XellentDbContext Context() => new(new DbContextOptionsBuilder<XellentDbContext>()
        .UseSqlServer(fixture.XellentConnectionString,sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(30); }).Options);

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
            select new { d.Meteringpoint,c.Contractenddate }).Distinct().Take(2).ToListAsync();
        Assert.NotEmpty(sample);
        var dates = sample.GroupBy(r => r.Meteringpoint).ToDictionary(g => g.Key,
            g => DateOnly.FromDateTime(g.First().Contractenddate).AddDays(1));
        var result = await SettlementContractPartResolver.ResolveAsync(context,dates,"hol");
        Assert.All(result.Resolution.Pairs,pair => Assert.Contains(pair.MpId,dates.Keys));
        Assert.All(result.Evidence,row =>
        {
            Assert.Contains(row.MeteringPointIdentification,dates.Keys);
            Assert.Equal(dates[row.MeteringPointIdentification].AddDays(-1),row.ReferenceDate);
            Assert.True(row.Source is "Live" or "ContractPartLog");
            Assert.NotNull(row.Company);
            Assert.NotNull(row.Agreement);
        });
        Assert.All(result.Resolution.MeterPointTypesByMpId?.Values ?? [],type => Assert.NotEqual("ReadFailed",type.ReadStatus));
    }

    [Fact]
    public async Task Charging_history_queries_preserve_the_requested_account_and_reference_date()
    {
        await using var context = Context();
        (string Account,DateOnly Date)[] requests = [("339097",new(2026,8,31)),("339097",new(2026,7,31)),("445400",new(2026,8,31))];
        var result = await SettlementChargingHistoryReader.ReadAsync(context,requests,"hol");
        Assert.All(result.Products.Keys,key => Assert.Contains(key,requests));
        Assert.All(result.Evidence,row =>
        {
            Assert.Contains((row.Account,row.ReferenceDate),requests);
            Assert.True(row.Source is "Live" or "ContractPartLog");
        });
        Assert.All(result.Products.Values,products => Assert.Equal(products.Count,products.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }
}
