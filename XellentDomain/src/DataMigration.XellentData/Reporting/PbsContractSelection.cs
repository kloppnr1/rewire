using DataMigration.Core.Reporting;

namespace DataMigration.XellentData.Reporting;

/// <summary>
/// One candidate contract-on-metering-point, as read from Xellent before any rule is applied.
/// </summary>
/// <param name="HasSubContracts">Whether another contract names this one as its main contract.
/// Cannot be read off the candidate's own row — <c>Referencetomaincontract</c> says whether it HAS
/// a parent, which is a different question — so the reader supplies it from a second query.</param>
public record PbsContractCandidate(
    string ContractNumber,
    string CustomerAccount,
    string CustomerName,
    int PbsAgreementNumber,
    string MeteringpointId,
    string? Referencetomaincontract,
    DateTime ContractStartDate,
    DateTime ContractEndDate,
    DateTime PbsTransdate,
    int PbsTransactionCode,
    bool HasSubContracts = false);

/// <summary>
/// A PBS agreement somewhere under a customer number, read without the metering-point join — so
/// contracts of any commodity are included, and so are contracts that own no metering point at all.
///
/// <para>This is the pool the fallback draws from when the contract that owns a metering point
/// carries no agreement of its own. <c>create</c> has read billing this way since
/// <c>4dc57f2</c>; the report had not.</para>
/// </summary>
public record PbsAccountAgreement(
    string CustomerAccount,
    string ContractNumber,
    string DeliveryCategory,
    int PbsAgreementNumber,
    int PbsTransactionCode,
    DateTime PbsTransdate,
    DateTime ContractEndDate);

/// <summary>
/// Decides which Xellent PBS contract owns a metering point at the cutover, and why the others
/// don't. Kept free of database access so the rules can be tested directly.
///
/// <para>The rule that matters: a contract only speaks for a metering point if it was still in
/// force at the cutover. Without that check a customer who moved out months earlier keeps winning
/// their old metering point — and because the match is by metering point alone, the report then
/// pairs their stale PBS agreement with whatever payment agreements the metering point's *current*
/// holder has in Settl. Verified against production 2026-07-30: metering point
/// 571313115200207046 produced two such rows, naming Tank Ejendomme ApS and Randers Kommune under
/// account 406242, whose contract there ended 2026-01-31.</para>
///
/// <para>The cutover, not today, is the measuring point. Migrating a customer ends their Xellent
/// contract the day before the change of supplier, so measuring at today would discard every
/// contract the handover report exists to describe.</para>
/// </summary>
public static class PbsContractSelection
{
    // AX sentinel for "no date set" — the contract never ends.
    private static readonly DateTime EndDateNull = new(1900, 1, 1);

    /// <summary>
    /// A contract still ran at the cutover when it never ends, or ends no earlier than the last day
    /// before the change of supplier. A contract ending exactly on that day was live right up to
    /// the handover; one ending the day before was not.
    /// </summary>
    public static bool RanAtCutover(DateTime contractEndDate, DateTime? changeOfSupplierDate)
    {
        // With no cutover date there is nothing to measure against, so no contract is ruled out.
        if (changeOfSupplierDate is null) return true;

        return contractEndDate == EndDateNull
            || contractEndDate >= changeOfSupplierDate.Value.AddDays(-1);
    }

    /// <summary>
    /// Whether a PBS transaction code counts as an agreement this run may use.
    /// Only active codes 1, 2 and 6 qualify by default; unknown codes never qualify.
    ///
    /// <para>Terminated (3/4/5) normally does not — but migrating a customer to Settl terminates their
    /// PBS agreement in Xellent, so for the customers a handover report is about, terminated is the
    /// ordinary state. <c>includeTerminated</c> takes them back in, and the transaction date decides
    /// which: terminated after the change of supply is the migration's own doing, terminated before
    /// it is a customer who left.</para>
    ///
    /// <para>One definition, used by the winner, by the exclusion list and by the account-level
    /// fallback alike. A fallback held to a stricter rule would silently drop the migrated
    /// population — exactly what it exists to recover.</para>
    /// </summary>
    public static bool CountsAsAgreement(
        int pbsTransactionCode, DateTime pbsTransdate, bool includeTerminated, DateTime? changeOfSupplierDate)
        => IsActive(pbsTransactionCode)
           || includeTerminated && IsTerminated(pbsTransactionCode)
               && IsTerminatedAfterCos(pbsTransdate, changeOfSupplierDate);

    /// <param name="accountAgreements">Every PBS agreement under the accounts, read without the
    /// metering-point join. Supplies the fallback when a metering point's own contract carries no
    /// agreement. Null or empty disables the fallback entirely — the pre-<c>4dc57f2</c> behaviour.</param>
    public static PbsContractQueryResult Select(
        IReadOnlyList<PbsContractCandidate> candidates,
        bool includeTerminated,
        DateTime? changeOfSupplierDate,
        IReadOnlyList<PbsAccountAgreement>? accountAgreements = null)
    {
        // Rule out expired contracts before picking a winner: otherwise a stale contract with a
        // later start date shadows the in-force one that should own the metering point.
        var live = candidates
            .Where(x => RanAtCutover(x.ContractEndDate, changeOfSupplierDate))
            .ToList();

        // Newest contract per metering point, across all customers and PBS codes.
        var winners = live
            .GroupBy(x => x.MeteringpointId, StringComparer.Ordinal)
            .Select(g => g.MaxBy(x => x.ContractStartDate)!)
            .ToList();

        // Metering points left without any in-force contract are simply not this profile's. Their
        // Settl agreements are reported as unmatched and nothing more: annotating them with the
        // account whose contract expired would hang one customer's number on another's agreement,
        // which is the conflation this filter exists to remove. Only the count is kept, so the
        // filter does not work in silence.
        var wonMps = winners.Select(x => x.MeteringpointId).ToHashSet(StringComparer.Ordinal);
        var expiredMps = candidates
            .Select(x => x.MeteringpointId)
            .Distinct(StringComparer.Ordinal)
            .Count(mp => !wonMps.Contains(mp));

        // Where the winner carries no agreement of its own, the customer may still pay through one
        // sitting on another of their contracts — any commodity, and possibly a contract that owns
        // no metering point at all. Keyed by metering point, since winners are one per point.
        var fallbackByMp = ResolveFallbacks(winners, accountAgreements, includeTerminated, changeOfSupplierDate);

        // The agreement each winner actually reports, its own or the fallback's.
        PbsAccountAgreement? FallbackFor(PbsContractCandidate x) =>
            fallbackByMp.GetValueOrDefault(x.MeteringpointId);

        bool HasAgreement(PbsContractCandidate x) =>
            CountsAsAgreement(x.PbsTransactionCode, x.PbsTransdate, includeTerminated, changeOfSupplierDate)
            || FallbackFor(x) is not null;

        // Codes 3 (cancelled by PI), 4 and 5 are terminated agreements.
        var excludedByMp = winners
            .Where(x => !HasAgreement(x))
            .ToDictionary(
                x => x.MeteringpointId,
                x => new ExcludedPbsContract(x.CustomerAccount, x.ContractNumber, x.CustomerName,
                                             x.PbsTransactionCode, x.PbsTransdate),
                StringComparer.Ordinal);

        // ContractStatusMap covers excluded contracts too, so annotated rows still get a label. The
        // label describes the agreement being reported, so a fallback row shows the source
        // contract's status — its own would read "Ingen PBS" beside a PBS number.
        var contractStatusMap = winners
            .GroupBy(x => x.ContractNumber, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var x = g.First();
                    return PbsStatusLabel(FallbackFor(x)?.PbsTransactionCode ?? x.PbsTransactionCode);
                },
                StringComparer.Ordinal);

        var matchable = winners.Where(HasAgreement).ToList();

        // Provenance for the winning rows. Counted over the candidate set rather than the winners,
        // because the point of the numbers is what the selection had to discard to arrive at one
        // contract per metering point.
        var candidatesPerMp = candidates
            .GroupBy(x => x.MeteringpointId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var expiredPerMp = candidates
            .Where(x => !RanAtCutover(x.ContractEndDate, changeOfSupplierDate))
            .GroupBy(x => x.MeteringpointId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Agreements in force on the customer number, counted over the in-force candidates only. A
        // moved-out customer's old agreement is not one of the account's competing ways to pay, and
        // counting it would contradict the filter the rest of this method applies.
        //
        // An agreement number sits on exactly one contract, and a contract that carries one names
        // exactly one consumption metering point (AXDB50 2026-08-07: 0 of 158.548 agreement numbers
        // on two contracts, 0 of 97.546 agreement-bearing contracts on two metering points). The
        // Distinct() therefore removes nothing today; it stays because the schema enforces neither,
        // and a duplicate would inflate the count in silence.
        //
        // That is NOT the same as one agreement covering one metering point. A main contract's
        // agreement also pays for its sub-contracts' metering points, which carry none of their
        // own: 4.100 accounts have more metering points than agreements, one of them 411 points on
        // a single agreement. So this counts metering points paid for SEPARATELY — the ones with an
        // agreement of their own — and above 1 says the customer does not pay for everything the
        // same way. It is not the account's metering-point count, and not its agreement coverage.
        var agreementsPerAccount = live
            .Where(x => IsActive(x.PbsTransactionCode))
            .GroupBy(x => x.CustomerAccount, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.PbsAgreementNumber).Distinct().Count(),
                StringComparer.Ordinal);

        var contracts = matchable
            .Select(x => new XellentPbsContract
            {
                ContractNumber = x.ContractNumber,
                CustomerAccount = x.CustomerAccount,
                PbsAgreementNumber = FallbackFor(x)?.PbsAgreementNumber ?? x.PbsAgreementNumber,
                PbsAgreementSourceContract = FallbackFor(x)?.ContractNumber,
                PbsAgreementSourceDeliveryCategory = FallbackFor(x)?.DeliveryCategory,
                MeteringpointId = x.MeteringpointId,
                Referencetomaincontract = x.Referencetomaincontract,
                SourceKind = x.Referencetomaincontract is not null
                    ? PbsSourceKind.SubContract
                    : x.HasSubContracts
                        ? PbsSourceKind.MainContract
                        : PbsSourceKind.Standalone,
                ContractStartDate = ToDateOrNull(x.ContractStartDate),
                ContractEndDate = ToDateOrNull(x.ContractEndDate),
                AgreementCountOnAccount = agreementsPerAccount.GetValueOrDefault(x.CustomerAccount),
                CandidatesOnMeteringPoint = candidatesPerMp.GetValueOrDefault(x.MeteringpointId),
                ExpiredCandidatesOnMeteringPoint = expiredPerMp.GetValueOrDefault(x.MeteringpointId)
            })
            .ToList();

        var contractCodes = matchable
            .GroupBy(x => x.ContractNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().PbsTransactionCode, StringComparer.Ordinal);

        return new PbsContractQueryResult(contracts, contractStatusMap, excludedByMp, contractCodes, expiredMps);
    }

    /// <summary>
    /// For each winner that carries no agreement of its own, the agreement to report instead — the
    /// one on its <b>main contract</b>.
    ///
    /// <para>The main contract and nothing else. <c>create</c> reads billing that way
    /// (<c>CustomerMapper.FetchBillingChannelInfoAsync</c> joins through
    /// <c>Referencetomaincontract</c>), and the point of this fallback is that the report and the
    /// migration agree about who pays by PBS. Reaching for any agreement under the customer number
    /// instead would report PBS for customers <c>create</c> migrated without it — trading one
    /// disagreement for a new one pointing the other way. Measured against AXDB50_TEST 2026-08-06:
    /// 8.830 accounts inherit from their own main contract, while 3.395 have an agreement only on
    /// some sibling contract, and those are exactly the ones that must stay unreported.</para>
    ///
    /// <para>Only <c>PbsTransactionCode == 0</c> ("Ingen PBS") falls back. A terminated agreement is
    /// a decision about this metering point's contract, not an absence, and reaching past it would
    /// re-admit precisely what a run without <c>--include-terminated</c> leaves out.</para>
    ///
    /// <para>The main contract must itself have run at the cutover, like every other contract this
    /// class keeps: an agreement on a contract the customer left years ago is a leftover, not the
    /// way they pay now.</para>
    /// </summary>
    private static Dictionary<string, PbsAccountAgreement> ResolveFallbacks(
        IReadOnlyList<PbsContractCandidate> winners,
        IReadOnlyList<PbsAccountAgreement>? accountAgreements,
        bool includeTerminated,
        DateTime? changeOfSupplierDate)
    {
        var result = new Dictionary<string, PbsAccountAgreement>(StringComparer.Ordinal);
        if (accountAgreements is null || accountAgreements.Count == 0) return result;

        // Keyed by (account, contract): the main contract is named by number, and the number is
        // only unique within the data area the caller read.
        var usable = accountAgreements
            .Where(a => a.PbsAgreementNumber != 0
                     && RanAtCutover(a.ContractEndDate, changeOfSupplierDate)
                     && CountsAsAgreement(a.PbsTransactionCode, a.PbsTransdate, includeTerminated, changeOfSupplierDate))
            .GroupBy(a => (a.CustomerAccount, a.ContractNumber))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var winner in winners)
        {
            if (winner.PbsTransactionCode != 0) continue;

            // A contract with no parent has nothing to inherit from. Its own code 0 is the answer.
            if (string.IsNullOrEmpty(winner.Referencetomaincontract)) continue;

            if (usable.TryGetValue((winner.CustomerAccount, winner.Referencetomaincontract), out var pick)
                && !string.Equals(pick.ContractNumber, winner.ContractNumber, StringComparison.Ordinal))
            {
                result[winner.MeteringpointId] = pick;
            }
        }

        return result;
    }

    public static string PbsStatusLabel(int code) => code switch
    {
        0 => "Ingen PBS",
        1 => "Aktiv",
        2 => "Aktiv",
        3 => "Aftale afmeldt af PI",
        4 => "Afmeldt",
        5 => "Afmeldt",
        6 => "Aktiv",
        _ => code.ToString()
    };

    private static bool IsActive(int code) => code is 1 or 2 or 6;

    private static bool IsTerminated(int code) => code is 3 or 4 or 5;

    /// <summary>The AX sentinel is not a date a reader should be shown; it becomes an empty cell.</summary>
    private static DateOnly? ToDateOrNull(DateTime value) =>
        value == EndDateNull ? null : DateOnly.FromDateTime(value);

    private static bool IsTerminatedAfterCos(DateTime pbsTransdate, DateTime? cos) =>
        !cos.HasValue || pbsTransdate > cos.Value;
}
