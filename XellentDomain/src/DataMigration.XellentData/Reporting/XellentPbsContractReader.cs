using DataMigration.Core.Reporting;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Reporting;

public record ExcludedPbsContract(
    string CustomerAccount,
    string ContractNumber,
    string CustomerName,
    int Pbstransactioncode,
    DateTime Pbstransdate);

public record PbsContractQueryResult(
    IReadOnlyList<XellentPbsContract> Contracts,
    IReadOnlyDictionary<string, string> ContractStatusMap,
    IReadOnlyDictionary<string, ExcludedPbsContract> ExcludedByMp,
    IReadOnlyDictionary<string, int> ContractCodes,
    /// <summary>Metering points dropped because no contract of these accounts still ran at the
    /// cutover. They belong to another customer now, so they are not reported as anything.</summary>
    int ExpiredBeforeCutoverMeteringPoints = 0);

/// <summary>
/// Reads the PBS contracts behind a set of accounts, one per metering point. The rules for which
/// contract wins a metering point live in <see cref="PbsContractSelection"/>; this type only
/// fetches the candidates.
/// </summary>
public static class XellentPbsContractReader
{
    private static readonly string[] ConsumptionTypes = ["E17/Forb", "FORB"];

    // SQL Server caps parameters at 2100; keep each IN-batch well under that. Same rule and number
    // as XellentMobilePayContractReader — a profile's contract list runs to thousands.
    private const int ContractBatchSize = 1500;

    public static async Task<PbsContractQueryResult> FetchAsync(
        string connectionString,
        string dataAreaId,
        IReadOnlySet<string> accounts,
        bool includeTerminated,
        DateTime? changeOfSupplierDate = null,
        CancellationToken ct = default)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); })
            .Options;

        await using var db = new XellentDbContext(dbOptions);

        var xellentRows = await (
            from c in db.ExuContractTables
            where c.Dataareaid == dataAreaId && accounts.Contains(c.Custaccount)
            join cust in db.CustTables
                on new { Accountnum = c.Custaccount, c.Dataareaid } equals new { cust.Accountnum, cust.Dataareaid }
            join cp in db.ExuContractPartTables
                on new { c.Contractnum, c.Dataareaid } equals new { cp.Contractnum, cp.Dataareaid }
            join ag in db.ExuAgreementTables
                on new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid } equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
            join dp in db.ExuDelpoints
                on new { Attachmentnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid } equals new { dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
            join mt in db.EgsMeterpointTypecodeperiods
                on new { Delpointnum = dp.Num, Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid, dp.Deliverycategory }
                equals new { mt.Delpointnum, mt.Attachmentnum, mt.Dataareaid, mt.Companyid, mt.Deliverycategory }
            where ConsumptionTypes.Contains(mt.Meterpointtypecode)
            select new
            {
                c.Contractnum,
                c.Custaccount,
                CustomerName = cust.Name,
                c.Pbsagreementnum,
                c.Referencetomaincontract,
                c.Contractstartdate,
                c.Contractenddate,
                c.Pbstransdate,
                c.Pbstransactioncode,
                dp.Meteringpoint
            }
        ).ToListAsync(ct);

        // Which of the contracts read above are themselves named as a main contract. Needs its own
        // query: the contract row knows whether it HAS a parent, not whether it IS one.
        var contractNumbers = xellentRows
            .Select(x => x.Contractnum.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var contractsWithChildren = await QueryContractsWithChildrenAsync(db, dataAreaId, contractNumbers, ct);

        var candidates = xellentRows
            .GroupBy(x => new { x.Contractnum, x.Meteringpoint })
            .Select(g =>
            {
                var first = g.First();
                return new PbsContractCandidate(
                    ContractNumber: first.Contractnum.Trim(),
                    CustomerAccount: first.Custaccount.Trim(),
                    CustomerName: first.CustomerName?.Trim() ?? "",
                    PbsAgreementNumber: first.Pbsagreementnum,
                    MeteringpointId: first.Meteringpoint.Trim(),
                    Referencetomaincontract: string.IsNullOrWhiteSpace(first.Referencetomaincontract)
                        ? null : first.Referencetomaincontract.Trim(),
                    ContractStartDate: first.Contractstartdate,
                    ContractEndDate: first.Contractenddate,
                    PbsTransdate: first.Pbstransdate,
                    PbsTransactionCode: first.Pbstransactioncode,
                    HasSubContracts: contractsWithChildren.Contains(first.Contractnum.Trim()));
            })
            .ToList();

        // Read separately from the candidates above, and deliberately without the metering-point
        // join: an account's PBS agreement can sit on a contract of any commodity, and on one that
        // owns no metering point at all. The join that produces the candidates cannot see those.
        var accountAgreements = await QueryAccountAgreementsAsync(db, dataAreaId, accounts, ct);

        return PbsContractSelection.Select(candidates, includeTerminated, changeOfSupplierDate, accountAgreements);
    }

    /// <summary>
    /// Every PBS agreement under the given accounts, whatever contract it sits on. No
    /// delivery-category filter — that is the whole point, and the same choice
    /// <c>CustomerMapper.FetchBillingChannelInfoAsync</c> made for <c>create</c> in <c>4dc57f2</c>:
    /// a PBS agreement on a Vand/Varme contract is invisible to an electricity profile otherwise.
    ///
    /// <para>No end-date or transaction-code filter either. Both rules belong to
    /// <see cref="PbsContractSelection"/>, which owns them for the candidates too and can be tested
    /// without a database.</para>
    /// </summary>
    private static async Task<List<PbsAccountAgreement>> QueryAccountAgreementsAsync(
        XellentDbContext db, string dataAreaId, IReadOnlySet<string> accounts, CancellationToken ct)
    {
        var ids = accounts.ToList();
        var all = new List<PbsAccountAgreement>();
        for (var offset = 0; offset < ids.Count; offset += ContractBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.GetRange(offset, Math.Min(ContractBatchSize, ids.Count - offset));
            var rows = await db.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId
                    && batch.Contains(c.Custaccount)
                    && c.Pbsagreementnum != 0)
                .Select(c => new
                {
                    c.Custaccount,
                    c.Contractnum,
                    c.Deliverycategory,
                    c.Pbsagreementnum,
                    c.Pbstransactioncode,
                    c.Pbstransdate,
                    c.Contractenddate
                })
                .ToListAsync(ct);

            all.AddRange(rows.Select(r => new PbsAccountAgreement(
                CustomerAccount: r.Custaccount.Trim(),
                ContractNumber: r.Contractnum.Trim(),
                DeliveryCategory: r.Deliverycategory?.Trim() ?? "",
                PbsAgreementNumber: r.Pbsagreementnum,
                PbsTransactionCode: r.Pbstransactioncode,
                PbsTransdate: r.Pbstransdate,
                ContractEndDate: r.Contractenddate)));
        }
        return all;
    }

    /// <summary>
    /// Of <paramref name="contracts"/>, those that at least one other contract names as its main
    /// contract. Batched on the 2100-parameter ceiling, as the agreement query in
    /// <see cref="XellentMobilePayContractReader"/> is.
    /// </summary>
    private static async Task<HashSet<string>> QueryContractsWithChildrenAsync(
        XellentDbContext db, string dataAreaId, IReadOnlyList<string> contracts, CancellationToken ct)
    {
        var withChildren = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < contracts.Count; offset += ContractBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = contracts.Skip(offset).Take(ContractBatchSize).ToList();
            var parents = await db.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId
                    && c.Referencetomaincontract != null
                    && batch.Contains(c.Referencetomaincontract))
                .Select(c => c.Referencetomaincontract!)
                .Distinct()
                .ToListAsync(ct);
            foreach (var p in parents) withChildren.Add(p.Trim());
        }
        return withChildren;
    }

    public static string PbsStatusLabel(int code) => PbsContractSelection.PbsStatusLabel(code);
}
