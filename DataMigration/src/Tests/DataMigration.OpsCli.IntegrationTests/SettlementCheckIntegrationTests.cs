using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Read-only: the raw contract-part and billing-dates queries against AXDB50_TEST.</summary>
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

        var xellent = await SettlementCheck.ReadAsync(context, subjects, CancellationToken.None);
        var parts = xellent.Parts.ToLookup(p => p.MeteringPoint);
        var statements = xellent.Statements.ToLookup(p => p.MeteringPoint);
        var rows = subjects.Select(s => SettlementCheck.Classify(s, parts[s.MeteringPoint], statements[s.MeteringPoint])).ToList();

        Assert.All(xellent.Parts, p => Assert.Contains(p.MeteringPoint, cosByMp.Keys));
        Assert.All(xellent.Statements, p => Assert.Contains(p.MeteringPoint, cosByMp.Keys));
        Assert.All(rows, r => Assert.NotEqual("", r.ContractPartIds));
    }

    /// <summary>
    /// The point back office verified in production (contract 10461316, COS 2026-05-01, latest
    /// statement 2026-04-30). AXDB50_TEST is an older copy where that contract's latest statement
    /// is 2025-03-31, so here it must come out NotSettled with that date.
    /// </summary>
    [Fact]
    public async Task Verified_point_reads_the_max_statement_date_of_its_contract_and_agreement()
    {
        await using var context = Context();
        var subject = new SettlementCheck.Subject("000000", "000000", "571313115200045310", "10461316",
            new DateOnly(2026, 5, 1));

        var xellent = await SettlementCheck.ReadAsync(context, [subject], CancellationToken.None);
        var row = SettlementCheck.Classify(subject, xellent.Parts, xellent.Statements);

        Assert.Equal(new DateOnly(2025, 3, 31), row.LatestStatementDate);
        Assert.Equal(SettlementCheck.NotSettled, row.Status);
    }
}