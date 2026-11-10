using DataMigration.XellentData.Reporting;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

public interface IPaymentMatchMpReader
{
    Task<List<XellentDeliveryOnDate.Delivery>> OwnersAsync(IReadOnlyList<string> ids, DateTime on,
        Action<int, int> progress, CancellationToken ct);
    Task<PaymentMpSnapshot> PaymentsAsync(IReadOnlySet<string> accounts, DateTime on, CancellationToken ct);
}

public sealed class PaymentMatchMpReader(string connectionString) : IPaymentMatchMpReader
{
    public Task<List<XellentDeliveryOnDate.Delivery>> OwnersAsync(IReadOnlyList<string> ids,
        DateTime on, Action<int, int> progress, CancellationToken ct) =>
        XellentDeliveryOnDate.ReadCandidatesAsync(connectionString, ids, on,
            PaymentMatchMpXellent.DataAreaId, PaymentMatchMpXellent.DeliveryCategory, progress, ct);

    public async Task<PaymentMpSnapshot> PaymentsAsync(IReadOnlySet<string> accounts, DateTime on, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); }).Options;
        await using var db = new XellentDbContext(options);
        return await ReadSnapshotAsync(db, accounts, on, ct);
    }

    internal static async Task<PaymentMpSnapshot> ReadSnapshotAsync(XellentDbContext db,
        IReadOnlySet<string> accounts, DateTime on, CancellationToken ct)
    {
        var customers = new List<PaymentMpCustomer>();
        var contracts = new List<PaymentMpSource>();
        var pbsCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Keep SQL parameters below 2100 even when called independently of engine batching.
        foreach (var batch in accounts.Chunk(1500))
        {
            // Restore the original PBS report count: distinct own agreement numbers on
            // consumption-linked candidate contracts that have not ended before cutover-1.
            // This is report evidence only and never participates in agreement selection.
            var ownPbs = await (
                from c in db.ExuContractTables.AsNoTracking()
                where c.Dataareaid == PaymentMatchMpXellent.DataAreaId && batch.Contains(c.Custaccount)
                    && (c.Contractenddate == new DateTime(1900, 1, 1) || c.Contractenddate >= @on)
                join cp in db.ExuContractPartTables on new { c.Contractnum, c.Dataareaid } equals new { cp.Contractnum, cp.Dataareaid }
                join ag in db.ExuAgreementTables on new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid }
                    equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                join dp in db.ExuDelpoints on new { Attachmentnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                    equals new { dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
                join mt in db.EgsMeterpointTypecodeperiods on new { Delpointnum = dp.Num, dp.Attachmentnum, dp.Dataareaid, dp.Companyid, dp.Deliverycategory }
                    equals new { mt.Delpointnum, mt.Attachmentnum, mt.Dataareaid, mt.Companyid, mt.Deliverycategory }
                where mt.Meterpointtypecode == "E17/Forb" || mt.Meterpointtypecode == "FORB"
                select new { c.Custaccount, c.Pbsagreementnum, c.Pbstransactioncode, c.Pbstransdate }).Distinct().ToListAsync(ct);
            foreach (var group in ownPbs.Where(c => PbsContractSelection.CountsAsAgreement(c.Pbstransactioncode,
                c.Pbstransdate, false, null)).GroupBy(c => c.Custaccount.Trim(), StringComparer.Ordinal))
                pbsCounts[group.Key] = group.Select(c => c.Pbsagreementnum).Distinct().Count();
            var people = await db.CustTables.AsNoTracking()
                .Where(c => c.Dataareaid == PaymentMatchMpXellent.DataAreaId && batch.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, c.Name, Cvr = c.ExuCvrDisponent1 }).ToListAsync(ct);
            customers.AddRange(people.Select(c => new PaymentMpCustomer(c.Accountnum.Trim(), c.Name?.Trim() ?? "",
                PaymentMpCustomer.IsBusinessCvr(c.Cvr))));
            var rows = await db.ExuContractTables.AsNoTracking()
                .Where(c => c.Dataareaid == PaymentMatchMpXellent.DataAreaId && batch.Contains(c.Custaccount))
                .Select(c => new PaymentMpSource(c.Custaccount, c.Contractnum, c.Deliverycategory,
                    c.Referencetomaincontract, c.Contractstartdate, c.Contractenddate,
                    c.Pbsagreementnum, c.Pbstransactioncode, c.Pbstransdate,
                    c.Mobilepaysubscriptionagre20135, c.Mobilepayagreementcanceldate)).ToListAsync(ct);
            var partCategories = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var numbers in rows.Select(c => c.Contract).Distinct().Chunk(1500))
            {
                var parts = await db.ExuContractPartTables.AsNoTracking()
                    .Where(c => c.Dataareaid == PaymentMatchMpXellent.DataAreaId && numbers.Contains(c.Contractnum)
                        && c.Startdate <= on && (c.Enddate == new DateTime(1900, 1, 1) || c.Enddate >= on))
                    .Select(c => new { c.Contractnum, c.Deliverycategory }).Distinct().ToListAsync(ct);
                foreach (var group in parts.GroupBy(c => c.Contractnum.Trim()))
                    partCategories[group.Key] = string.Join("/", group.Select(c => c.Deliverycategory?.Trim())
                        .Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c, StringComparer.Ordinal));
            }
            contracts.AddRange(rows.Select(c => c with { Account = c.Account.Trim(), Contract = c.Contract.Trim(),
                Commodity = partCategories.GetValueOrDefault(c.Contract.Trim(), c.Commodity?.Trim() ?? ""), MobileId = c.MobileId?.Trim(),
                MainContract = string.IsNullOrWhiteSpace(c.MainContract) ? null : c.MainContract.Trim() }));
        }
        return new(customers, contracts) { PbsOwnAgreementCounts = pbsCounts };
    }
}
