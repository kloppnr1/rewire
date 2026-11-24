using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// One Xellent contract row as seen by the MobilePay scan: the contract itself plus the main
/// contract it points at. Both are inspected — the agreement may sit on either.
/// </summary>
internal sealed record MobilePayScanRow(
    string Account,
    string OwnContract,
    string? OwnReference,
    string? OwnAgreement,
    DateTime? OwnCancelDate,
    string? MainContract,
    string? MainAgreement,
    DateTime? MainCancelDate);

/// <summary>
/// Picks each account's MobilePay agreement from all of its contracts. Pure — the database
/// query lives in <see cref="CustomerMapper"/>; everything that decides which agreement wins
/// is here so it can be tested against the real-world shapes found in Xellent.
///
/// <para>There is no rule for where a MobilePay agreement sits. Customers signed up on whichever
/// contract they happened to be looking at, so an agreement can sit on a water/heat contract, an
/// electricity contract or an EV-charging contract, as a main contract or a sub-contract.</para>
/// </summary>
internal static class MobilePayResolver
{
    private static readonly DateTime EndDate = MigrationConstants.EndDateNull;

    /// <summary>An agreement is active when the id is set and it was never cancelled.</summary>
    public static bool IsActive(string? agreementId, DateTime? cancelDate) =>
        !string.IsNullOrWhiteSpace(agreementId) && cancelDate == EndDate;

    /// <summary>
    /// Returns the winning agreement per account together with its provenance. Main contracts
    /// win over sub-contracts, then the lowest contract number — deterministic, and it leaves
    /// accounts whose agreement already sat on the main contract untouched.
    /// </summary>
    public static IReadOnlyDictionary<string, MobilePayPick> Resolve(IEnumerable<MobilePayScanRow> rows)
    {
        // Deduplicate by contract: the same contract is reached once per sub-contract hanging
        // under it, and a main contract carrying the agreement must not be counted repeatedly.
        var byAccount = new Dictionary<string, Dictionary<string, MobilePayCandidate>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var account = row.Account?.Trim();
            if (string.IsNullOrEmpty(account)) continue;

            if (IsActive(row.OwnAgreement, row.OwnCancelDate))
                AddCandidate(byAccount, account, row.OwnContract, row.OwnAgreement,
                    string.IsNullOrWhiteSpace(row.OwnReference)
                        ? MobilePayOrigin.MainContract
                        : MobilePayOrigin.SubContract);

            // The main contract may have ended while an active sub-contract still hangs under it.
            // Its agreement was migrated before the cross-contract scan existed and stays valid.
            if (!string.IsNullOrWhiteSpace(row.MainContract)
                && !string.Equals(row.MainContract?.Trim(), row.OwnContract?.Trim(), StringComparison.Ordinal)
                && IsActive(row.MainAgreement, row.MainCancelDate))
                AddCandidate(byAccount, account, row.MainContract, row.MainAgreement, MobilePayOrigin.MainContract);
        }

        return byAccount.ToDictionary(
            kv => kv.Key,
            kv => new MobilePayPick(
                Winner: kv.Value.Values
                    .OrderBy(c => c.Origin == MobilePayOrigin.MainContract ? 0 : 1)
                    .ThenBy(c => c.ContractNumber, StringComparer.Ordinal)
                    .First(),
                // Counted by distinct agreement id: one agreement stored on two contracts is
                // one agreement, not a conflict that needs a human decision.
                CandidateCount: kv.Value.Values
                    .Select(c => c.AgreementId)
                    .Distinct(StringComparer.Ordinal)
                    .Count()),
            StringComparer.Ordinal);
    }

    private static void AddCandidate(
        Dictionary<string, Dictionary<string, MobilePayCandidate>> byAccount,
        string account,
        string? contractNumber,
        string? agreementId,
        MobilePayOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(contractNumber)) return;

        if (!byAccount.TryGetValue(account, out var bucket))
            byAccount[account] = bucket = new Dictionary<string, MobilePayCandidate>(StringComparer.Ordinal);

        bucket[contractNumber.Trim()] = new MobilePayCandidate(
            agreementId!.Trim(), contractNumber.Trim(), origin);
    }
}

/// <summary>One contract carrying an active MobilePay agreement, before the per-account pick.</summary>
internal sealed record MobilePayCandidate(
    string AgreementId,
    string ContractNumber,
    MobilePayOrigin Origin);

/// <summary>The agreement chosen for an account, plus how many competing agreements it had.</summary>
internal sealed record MobilePayPick(MobilePayCandidate Winner, int CandidateCount);
