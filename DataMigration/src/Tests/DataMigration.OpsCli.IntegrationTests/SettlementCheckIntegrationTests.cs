using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// The raw contract-part and billing-dates SQL in <see cref="SettlementCheck"/>, run against an
/// in-memory Xellent. <c>EXU_LATESTBILLINGDATES</c> is not in the EF model, so the table is created
/// here with the columns the query reads.
/// </summary>
public sealed class SettlementCheckIntegrationTests : IDisposable
{
    private readonly FakeXellent _xellent = new();

    public SettlementCheckIntegrationTests()
    {
        using var context = _xellent.NewContext();
        context.Database.ExecuteSqlRaw("""
            CREATE TABLE EXU_LATESTBILLINGDATES (
                DATAAREAID TEXT NOT NULL, COMPANYID TEXT NOT NULL, CONTRACTNUM TEXT NOT NULL,
                ATTACHMENTNUM TEXT NOT NULL, LATESTSTATEMENTDATE TEXT NOT NULL)
            """);

        // Points whose contracts ended in 2025-2026 (the COS-1 window the first test samples).
        Seed(new("410001", "C410001", "571313100000000201", new(2020, 1, 1), new(2025, 8, 31)), new(2025, 8, 31));
        Seed(new("410002", "C410002", "571313100000000202", new(2020, 1, 1), new(2026, 3, 31)), new(2026, 2, 28));
        Seed(new("410003", "C410003", "571313100000000203", new(2020, 1, 1), new(2026, 5, 31), Company: ""), new(2026, 5, 31));

        // The point back office verified (contract 10461316, COS 2026-05-01). In the older test copy
        // its latest statement is 2025-03-31, so it must come out NotSettled with that date.
        var verified = new XellentRows.Delivery("410009", "10461316", "571313115200045310", new(2019, 1, 1), new(2026, 4, 30));
        Seed(verified, new(2025, 3, 31));
        Statement(verified, new(2024, 12, 31));
    }

    public void Dispose() => _xellent.Dispose();

    private void Seed(XellentRows.Delivery delivery, DateTime latestStatement)
    {
        _xellent.Add(XellentRows.For(delivery));
        Statement(delivery, latestStatement);
    }

    private void Statement(XellentRows.Delivery d, DateTime date)
    {
        using var context = _xellent.NewContext();
        context.Database.ExecuteSqlRaw(
            "INSERT INTO EXU_LATESTBILLINGDATES VALUES ({0}, {1}, {2}, {3}, {4})",
            MigrationConstants.DataAreaId, d.Company, d.Contract, d.Agreement, date.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private XellentDbContext Context() => _xellent.NewContext();

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
        var resolution = await MeteringPointAccountResolver.ResolveAsync(_xellent.ConnectionString,
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
        // A statement dated COS-1 is settled; one a month earlier is not.
        Assert.Equal(SettlementCheck.Settled, rows.Single(r => r.Subject.MeteringPoint == "571313100000000201").Status);
        Assert.Equal(SettlementCheck.NotSettled, rows.Single(r => r.Subject.MeteringPoint == "571313100000000202").Status);
    }

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
