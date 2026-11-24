using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Turns the three inputs of the MobilePay match into report rows: Xellent's active agreements,
/// the Settl metering points that pinned to each account, and Settl's payment agreements.
///
/// <para>Pure by design. Fetching the inputs needs a Settl environment and a Xellent database, and
/// for a long while that meant the rules themselves could only be exercised against whatever those
/// two happened to contain — in Settl featuretest no migrated customer holds a MobilePay agreement
/// at all, so every row-level assertion passed by describing an empty list. Separating the rules
/// from the fetching is what <c>reconcile</c> does, and it is what lets the cases below be stated
/// as facts instead of hoped for.</para>
/// </summary>
public static class MobilePayRowBuilder
{
    /// <param name="agreements">Active, uncancelled MobilePay agreements from Xellent.</param>
    /// <param name="settlMpsByAccount">Settl metering points per Xellent account, pinned via COS-1.
    /// An account absent here has nothing in Settl and is therefore not migrated.</param>
    /// <param name="cosByMpId">Change-of-supplier date per Settl metering point.</param>
    /// <param name="ownerContractByMpId">The contract that decided each metering point's account.
    /// Carried onto the row so the pinning can be checked rather than trusted: the end date should
    /// be the COS minus one day, and the row says so when it is not.</param>
    /// <param name="settlRecords">Settl's payment agreements.</param>
    public static List<MobilePayMatchReportRow> Build(
        IReadOnlyList<MobilePayContract> agreements,
        IReadOnlyDictionary<string, List<string>> settlMpsByAccount,
        IReadOnlyDictionary<string, DateOnly> cosByMpId,
        IReadOnlyDictionary<string, MeteringPointAccountResolver.OwnerContract> ownerContractByMpId,
        IReadOnlyList<SettlCsvRecord> settlRecords)
    {
        // Settl's payment agreements, keyed the two ways they can be reached. Deliberately NOT
        // filtered by payment method: what the sheet needs from Settl is the customer's E-number, and
        // that identifies the customer regardless of how they pay. Requiring a MobilePay-method row
        // would empty the report — Settl production held 5 of those out of 11.500 (2026-07-30) —
        // because creating the missing MobilePay agreements is precisely what the handover does.
        var byMp = settlRecords
            .Where(r => !string.IsNullOrEmpty(r.MeteringpointId))
            .GroupBy(r => r.MeteringpointId!.Trim())
            .ToDictionary(g => g.Key, g => g.First());

        var byJointInvoice = settlRecords
            .Where(r => string.IsNullOrEmpty(r.MeteringpointId) && !string.IsNullOrEmpty(r.JointInvoiceNumber))
            .GroupBy(r => r.JointInvoiceNumber!.Trim())
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<MobilePayMatchReportRow>(agreements.Count);
        foreach (var agreement in agreements)
        {
            settlMpsByAccount.TryGetValue(agreement.Account, out var accountMps);

            // The metering point whose migrated contract IS the contract this agreement sits on.
            // A customer can hold one contract per metering point, each with its own agreement, and
            // those agreements do not compete — but the account's metering point list cannot tell
            // them apart on its own. Without this, every row on the account was reported against
            // whichever metering point came first (account 366645, production: agr_VaQXh8t sits on
            // contract 10355841, yet its row showed 10355842's metering point and was flagged as
            // NOT sitting on the migrated contract — both contracts were migrated, one per point).
            string? ownMp = null;
            if (accountMps != null)
                foreach (var mp in accountMps)
                    if (ownerContractByMpId.TryGetValue(mp, out var candidate)
                        && string.Equals(candidate.ContractNumber, agreement.SourceContractNumber, StringComparison.Ordinal))
                    {
                        ownMp = mp;
                        break;
                    }

            // Try every Settl metering point on the account, not one picked in advance — but start
            // with the agreement's own, so a match is credited to the point that belongs to it.
            var lookupOrder = accountMps is null
                ? []
                : ownMp is null
                    ? accountMps
                    : (IReadOnlyList<string>)[ownMp, .. accountMps.Where(mp => !string.Equals(mp, ownMp, StringComparison.Ordinal))];

            string? matchedMp = null;
            SettlCsvRecord? record = null;
            foreach (var mp in lookupOrder)
                if (byMp.TryGetValue(mp, out var hit)) { matchedMp = mp; record = hit; break; }

            // Joint-invoice fallback: some payment agreements carry no metering point.
            var isJoint = false;
            string? matchedJointKey = null;
            if (record is null)
            {
                var jointKey = byJointInvoice.ContainsKey(agreement.SourceContractNumber)
                    ? agreement.SourceContractNumber
                    : agreement.Referencetomaincontract != null
                      && byJointInvoice.ContainsKey(agreement.Referencetomaincontract)
                        ? agreement.Referencetomaincontract
                        : null;
                if (jointKey != null)
                {
                    record = byJointInvoice[jointKey];
                    isJoint = true;
                    matchedJointKey = jointKey;
                }
            }

            var status = record != null
                ? MobilePayMatchStatus.Matched
                : MobilePayMatchStatus.NoSettlCustomerNumber;

            // The metering point the row is reported against. The match decides it whenever the
            // matched point belongs to the agreement's own contract — a contract can own several
            // points, and then the one that produced the E-number is the most informative of them.
            // Only where the match landed on ANOTHER contract's point does the agreement's own take
            // over: provenance answers "which delivery point does this agreement belong to", and
            // the E-number identifies the customer, not the point. Failing both, the account's
            // first, so the pinning is still visible. The key that reached Settl is reported apart
            // from this, because a joint match reaches Settl without a metering point at all.
            var matchedIsOwn = matchedMp != null
                && ownerContractByMpId.TryGetValue(matchedMp, out var matchedOwner)
                && string.Equals(matchedOwner.ContractNumber, agreement.SourceContractNumber, StringComparison.Ordinal);

            var provenanceMp = matchedIsOwn
                ? matchedMp
                : ownMp ?? matchedMp ?? accountMps?.FirstOrDefault();
            MeteringPointAccountResolver.OwnerContract? owner = null;
            DateOnly? cos = null;
            if (provenanceMp != null)
            {
                if (ownerContractByMpId.TryGetValue(provenanceMp, out var o)) owner = o;
                if (cosByMpId.TryGetValue(provenanceMp, out var c)) cos = c;
            }

            // A cancelled agreement is only worth reporting when the migration itself closed it —
            // cancelled after the change of supply. Cancelled before it, the customer stopped
            // paying that way while still in Xellent, and the handover has nothing to say about it.
            // Same rule PBS applies to Pbstransdate under --include-terminated.
            if (agreement.CancelDate is { } cancelled && cos is { } cosDate && cancelled <= cosDate)
                continue;

            // A COS-1 match means the owning contract ended the day before delivery started. Any
            // other end date means the account did not come from a change-of-supplier event, and
            // the row is then an attribution the reader has to check by hand.
            var pinningOff = owner != null && cos != null && owner.EndDate != cos.Value.AddDays(-1);

            // Does this agreement sit on the contract that owned the metering point at cutover? On
            // an account with competing agreements that is the whole question, and the answer is
            // already in hand — it just had to be stated instead of left for the reader to derive
            // by holding two columns up against each other.
            bool? isOnMigrated = owner is null
                ? null
                : string.Equals(owner.ContractNumber, agreement.SourceContractNumber, StringComparison.Ordinal);
            var remarks = MobilePayRowClassifier.BuildRemarks(
                agreement, status, pinningOff, isOnMigrated, accountMps?.Count ?? 0);

            rows.Add(new MobilePayMatchReportRow
            {
                Account = agreement.Account,
                SourceContractNumber = agreement.SourceContractNumber,
                SourceKind = agreement.SourceKind,
                AgreementCountOnAccount = agreement.AgreementCountOnAccount,
                Status = status,
                MeteringPointId = provenanceMp ?? "",
                MatchKey = matchedMp ?? matchedJointKey ?? "",
                SettlMeteringPointCount = accountMps?.Count ?? 0,
                SourceContractEndDate = agreement.SourceContractEndDate,
                MigratedContractNumber = owner?.ContractNumber ?? "",
                Cos = cos,
                IsOnMigratedContract = isOnMigrated,
                EnummerSettl = record?.ExternalCustomerNumber,
                EnummerSourcePaymentMethod = record?.PaymentMethod,
                MobilePayAgreementId = agreement.MobilePayAgreementId,
                CancelDate = agreement.CancelDate,
                IsJoint = isJoint,
                Remarks = remarks,
                Severity = MobilePayRowClassifier.Classify(agreement, status, remarks, isOnMigrated)
            });
        }

        return rows;
    }
}
