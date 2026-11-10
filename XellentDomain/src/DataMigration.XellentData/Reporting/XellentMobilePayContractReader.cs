using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Reporting;

/// <summary>
/// Where under the customer number the MobilePay agreement was found.
///
/// <para>Being a main contract is a property of having sub-contracts, not of lacking a parent. The
/// two were conflated while this was a two-valued enum, so every parentless contract reported
/// "Hovedkontrakt" — 4.288 of the 4.965 agreement-carrying contracts in AXDB50 (2026-07-31), which
/// are in fact standalone.</para>
/// </summary>
public enum MobilePaySourceKind
{
    /// <summary>Agreement sits on a contract that other contracts reference as their main.</summary>
    MainContract,

    /// <summary>Agreement sits on a sub-contract (<c>Referencetomaincontract</c> set).</summary>
    SubContract,

    /// <summary>Agreement sits on a contract with neither a parent nor any sub-contracts.</summary>
    Standalone
}

/// <summary>
/// One active MobilePay agreement, with the provenance needed to check it by hand: which contract
/// carried it, of which kind, when that contract ended, and how many agreements compete on the same
/// customer number.
/// </summary>
/// <param name="Account">Xellent customer number the agreement belongs to.</param>
/// <param name="MobilePayAgreementId">The agreement id stored on the contract.</param>
/// <param name="Referencetomaincontract">The contract's main contract, or null when it is one.</param>
/// <param name="SourceContractNumber">Contract the agreement id is stored on — the answer to
/// "where was this agreement found". Was duplicated as a second <c>ContractNumber</c> member
/// holding the identical value, which made the record read as if placement could be two different
/// contracts. It cannot.</param>
/// <param name="SourceKind">Main, sub- or standalone placement.</param>
// Delivery category was carried here too, at the cost of a per-contract query
// (ContractDeliveryCategoryReader). It never decided anything about a row — a MobilePay agreement
// sits on whichever contract the customer signed up on, and the commodity says nothing about
// whether the agreement is live. Dropped 2026-07-31 along with the query.
/// <param name="SourceContractEndDate">End date of the contract the agreement sits on, or null
/// where Xellent's <c>1900-01-01</c> sentinel says there is none. This is the date a reader needs
/// to judge the agreement: one on a contract that ended long before the customer's change of
/// supply is a leftover that was never cancelled.</param>
/// <param name="AgreementCountOnAccount">Distinct active agreements on the customer number.
/// Greater than 1 means competing agreements that need a manual decision. Counts uncancelled
/// agreements only — a cancelled one is not one of the account's current ways to pay, even when the
/// caller asked to see it.</param>
/// <param name="CancelDate">When the agreement was cancelled in Xellent, or null while it stands.
/// Only ever set when the caller asked for cancelled agreements; the default read returns none.
/// This is MobilePay's counterpart to PBS's <c>Pbstransdate</c>: held against the change of supply
/// it separates an agreement the migration itself closed from one the customer walked away
/// from.</param>
public record MobilePayContract(
    string Account,
    string MobilePayAgreementId,
    string? Referencetomaincontract,
    string SourceContractNumber,
    MobilePaySourceKind SourceKind,
    DateOnly? SourceContractEndDate,
    int AgreementCountOnAccount,
    DateOnly? CancelDate = null);

public static class XellentMobilePayContractReader
{
    // AX sentinel for "no date set" — agreement not cancelled.
    private static readonly DateTime EndDateNull = new(1900, 1, 1);

    // SQL Server caps parameters at 2100; keep each IN-batch well under that. Same rule and same
    // number as MeteringPointAccountResolver — an account list can easily run into thousands.
    private const int AccountBatchSize = 1500;

    /// <summary>
    /// Returns one row per active MobilePay agreement. Pass <paramref name="accounts"/> to limit the
    /// scan, or leave it null to cover the whole data area — the caller should not have to query for
    /// the account list only to hand it straight back.
    ///
    /// <para>There is no rule for where a MobilePay agreement sits: it follows whichever contract
    /// the customer happened to sign up on (verified against Xellent 2026-07 — agreements sit on
    /// water/heat, electricity and EV-charging contracts, main and sub). No end-date filter is
    /// applied: migrating a customer to Settl ends their Xellent contract, so filtering on open
    /// contracts would drop exactly the customers a handover is about.</para>
    /// </summary>
    /// <param name="includeTerminated">Also return cancelled agreements, with
    /// <see cref="MobilePayContract.CancelDate"/> set. Off by default: the sheet is a list of
    /// agreements to create in Settl, and a cancelled one must not be created by accident. On, it
    /// answers the question the default read cannot even ask — which customers held MobilePay and
    /// lost it. Whether a cancelled agreement is then reported is the caller's call, on the same
    /// terms PBS uses: cancelled after the change of supply is the migration's own doing, cancelled
    /// before it is the customer's.</param>
    public static async Task<IReadOnlyList<MobilePayContract>> FetchAsync(
        string connectionString,
        string dataAreaId,
        IReadOnlySet<string>? accounts = null,
        CancellationToken ct = default,
        bool includeTerminated = false)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseXellent(connectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); })
            .Options;

        await using var db = new XellentDbContext(dbOptions);

        var agreementRows = accounts is null
            ? await QueryAgreementsAsync(db, dataAreaId, null, includeTerminated, ct)
            : await QueryAgreementsBatchedAsync(db, dataAreaId, accounts, includeTerminated, ct);

        if (agreementRows.Count == 0) return [];

        var agreements = agreementRows
            .Select(r =>
            {
                var mainRef = string.IsNullOrWhiteSpace(r.Referencetomaincontract)
                    ? null
                    : r.Referencetomaincontract.Trim();
                return new
                {
                    Account = r.Custaccount.Trim(),
                    SourceContract = r.Contractnum.Trim(),
                    AgreementId = r.AgreementId!.Trim(),
                    MainRef = mainRef,
                    EndDate = r.Contractenddate == EndDateNull
                        ? (DateOnly?)null
                        : DateOnly.FromDateTime(r.Contractenddate),
                    // Baade NULL og AX-sentinellen 1900-01-01 betyder "ikke opsagt".
                    CancelDate = r.Mobilepayagreementcanceldate is not { } cancel || cancel == EndDateNull
                        ? (DateOnly?)null
                        : DateOnly.FromDateTime(cancel)
                };
            })
            .ToList();

        var sourceContracts = agreements.Select(a => a.SourceContract).Distinct(StringComparer.Ordinal).ToList();

        // Which of the source contracts are actually referenced as a main contract. Answering this
        // needs a second query — the agreement row itself only knows whether the contract HAS a
        // parent, and that says nothing about whether it IS one.
        var contractsWithChildren = await QueryContractsWithChildrenAsync(db, dataAreaId, sourceContracts, ct);

        // Uncancelled only: a cancelled agreement is not one of the account competing live ways to
        // pay, even when the caller asked to see it.
        var agreementCountByAccount = agreements
            .Where(a => a.CancelDate is null)
            .GroupBy(a => a.Account, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.AgreementId).Distinct(StringComparer.Ordinal).Count(),
                StringComparer.Ordinal);

        // One row per agreement. An agreement id is stored on exactly one contract — verified over
        // AXDB50 2026-07-31: no customer number has the same active id on two contract numbers. An
        // earlier dedup step here guarded a case the data does not contain, and made the reader look
        // as though the placement of an agreement were ambiguous. It is not; competing agreements on
        // one customer are distinct ids, and AgreementCountOnAccount below is what reports them.
        return agreements
            .Select(a => new MobilePayContract(
                Account: a.Account,
                MobilePayAgreementId: a.AgreementId,
                Referencetomaincontract: a.MainRef,
                SourceContractNumber: a.SourceContract,
                SourceKind: a.MainRef is not null
                    ? MobilePaySourceKind.SubContract
                    : contractsWithChildren.Contains(a.SourceContract)
                        ? MobilePaySourceKind.MainContract
                        : MobilePaySourceKind.Standalone,
                SourceContractEndDate: a.EndDate,
                AgreementCountOnAccount: agreementCountByAccount.GetValueOrDefault(a.Account),
                CancelDate: a.CancelDate))
            .OrderBy(r => r.Account, StringComparer.Ordinal)
            .ThenBy(r => r.SourceContractNumber, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record AgreementRow(
        string Custaccount, string Contractnum, string? Referencetomaincontract, string? AgreementId,
        DateTime Contractenddate, DateTime? Mobilepayagreementcanceldate);

    /// <summary>
    /// Of <paramref name="contracts"/>, those that at least one other contract names as its main
    /// contract. Batched on the same 2100-parameter ceiling as the agreement query — the agreement
    /// set runs to several thousand contracts.
    /// </summary>
    private static async Task<HashSet<string>> QueryContractsWithChildrenAsync(
        XellentDbContext db, string dataAreaId, IReadOnlyList<string> contracts, CancellationToken ct)
    {
        var withChildren = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < contracts.Count; offset += AccountBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = contracts.Skip(offset).Take(AccountBatchSize).ToList();
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

    private static async Task<List<AgreementRow>> QueryAgreementsAsync(
        XellentDbContext db, string dataAreaId, IReadOnlyCollection<string>? batch,
        bool includeTerminated, CancellationToken ct)
        => await db.ExuContractTables
            .Where(c => c.Dataareaid == dataAreaId
                && c.Mobilepaysubscriptionagre20135 != null
                && c.Mobilepaysubscriptionagre20135 != ""
                && (includeTerminated || c.Mobilepayagreementcanceldate == EndDateNull)
                && (batch == null || batch.Contains(c.Custaccount)))
            .Select(c => new AgreementRow(
                c.Custaccount, c.Contractnum, c.Referencetomaincontract, c.Mobilepaysubscriptionagre20135,
                c.Contractenddate, c.Mobilepayagreementcanceldate))
            .ToListAsync(ct);

    private static async Task<List<AgreementRow>> QueryAgreementsBatchedAsync(
        XellentDbContext db, string dataAreaId, IReadOnlySet<string> accounts,
        bool includeTerminated, CancellationToken ct)
    {
        var ids = accounts.ToList();
        var all = new List<AgreementRow>();
        for (var offset = 0; offset < ids.Count; offset += AccountBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.GetRange(offset, Math.Min(AccountBatchSize, ids.Count - offset));
            all.AddRange(await QueryAgreementsAsync(db, dataAreaId, batch, includeTerminated, ct));
        }
        return all;
    }

}
