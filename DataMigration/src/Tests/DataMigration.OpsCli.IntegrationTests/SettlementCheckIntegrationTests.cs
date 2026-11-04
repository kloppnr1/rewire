using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Read-only: the raw TAKEORPAYPREVDATE query finds the COS-1 resolver's contract parts.</summary>
public sealed class SettlementCheckIntegrationTests(SettlFeaturetestFixture fixture)
    : IClassFixture<SettlFeaturetestFixture>
{
    private XellentDbContext Context() => new(new DbContextOptionsBuilder<XellentDbContext>()
        .UseSqlServer(fixture.XellentConnectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(60); }).Options);

    [Fact]
    public async Task Every_point_pinned_by_the_cos_resolver_has_a_contract_part_at_cos_minus_one()
    {
        await using var context = Context();
        var sample = await (
            from c in context.ExuContractTables
            join p in context.ExuContractPartTables on new { c.Contractnum, c.Companyid, c.Dataareaid }
                equals new { p.Contractnum, p.Companyid, p.Dataareaid }
            join d in context.ExuDelpoints on new { Attachmentnum = p.Instagreenum, p.Companyid, p.Dataareaid }
                equals new { d.Attachmentnum, d.Companyid, d.Dataareaid }
            where c.Dataareaid == MigrationConstants.DataAreaId && c.Companyid == ForInvoiceScope.CompanyId
                && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                && c.Contractenddate > new DateTime(2025, 1, 1) && c.Contractenddate < new DateTime(2027, 1, 1)
                && p.Startdate <= c.Contractenddate
                && (p.Enddate == new DateTime(1900, 1, 1) || p.Enddate >= c.Contractenddate)
            select new { d.Meteringpoint, c.Custaccount, c.Contractenddate }).Distinct().Take(20).ToListAsync();
        Assert.NotEmpty(sample);
        var cosByMp = sample.GroupBy(r => r.Meteringpoint).ToDictionary(g => g.Key,
            g => DateOnly.FromDateTime(g.Max(r => r.Contractenddate)).AddDays(1));
        var resolution = await MeteringPointAccountResolver.ResolveAsync(fixture.XellentConnectionString,
            cosByMp, MigrationConstants.DataAreaId, companyId: ForInvoiceScope.CompanyId, includeBlankCompany: true);
        var subjects = resolution.OwnerContractByMpId
            .Select(kv => new SettlementCheck.Subject("000000",
                resolution.Pairs.First(p => p.MpId == kv.Key).Account, kv.Key, kv.Value.ContractNumber, cosByMp[kv.Key]))
            .ToList();
        Assert.NotEmpty(subjects);

        var parts = await SettlementCheck.ReadPartsAsync(context, subjects, CancellationToken.None);
        var byMp = parts.ToLookup(p => p.MeteringPoint);
        var rows = subjects.Select(s => SettlementCheck.Classify(s, byMp[s.MeteringPoint])).ToList();

        Assert.All(parts, p => Assert.Contains(p.MeteringPoint, cosByMp.Keys));
        Assert.All(rows, r => Assert.NotEqual(SettlementCheck.Missing, r.Status));
        Assert.All(rows, r => Assert.All(r.ContractPartIds.Split('|'), id => Assert.True(long.Parse(id) > 0)));
    }
}
