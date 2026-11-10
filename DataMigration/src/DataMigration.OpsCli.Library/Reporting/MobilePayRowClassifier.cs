using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Turns a matched agreement into remarks and a severity. Pure, and kept apart from
/// <see cref="MobilePaySettlMatchService"/> — the service needs Settl and Xellent to run, these
/// rules decide what the operator sees and must be testable on their own.
/// </summary>
public static class MobilePayRowClassifier
{
    /// <summary>
    /// No E-number could be found, so nothing can key the MobilePay agreement on the Settl side. Says
    /// "E-nummer" rather than "betalingsaftale": the old wording read as if the customer's MobilePay
    /// agreement was missing from Settl, which is true of nearly every row and therefore not the
    /// finding — Settl production held 5 MobilePay payment agreements out of 11.500 on 2026-07-30.
    ///
    /// <para>The consequence ("cannot be handed over") is not spelled out: it follows from the
    /// remark, and the row is already red. A remark column has to be scannable.</para>
    /// </summary>
    public const string MissingAgreementRemark = "INTET E-NUMMER i Settl";

    /// <summary>
    /// Spells out the lookup that failed. The status alone was unreadable: it says an E-number was
    /// not found without saying what was searched for, and a reader had no way to tell a customer
    /// Settl genuinely holds no payment agreement for from an export that simply does not cover
    /// them. There is only ever one cause — every account in the report resolved from Settl, so it
    /// always has metering points — and naming its inputs is what makes it checkable: take the
    /// metering point or the contract number, search the export, find nothing.
    /// </summary>
    public static string MissingAgreement(int meteringPointsTried, string contractNumber)
    {
        var mps = meteringPointsTried == 1
            ? "kontoens målepunkt"
            : $"kontoens {meteringPointsTried} målepunkter";
        return $"{MissingAgreementRemark} — hverken {mps} eller kontraktnr. {contractNumber} "
             + "findes i betalingsaftale-eksporten";
    }

    public const string SubContractRemark = "Aftale sidder på underkontrakt";

    /// <summary>
    /// The owning contract does not end the day before the metering point's change of supplier, so
    /// the account was not established by a change-of-supplier event. Since the scan only reads
    /// metering points in delivery, every row should pin cleanly — this remark marks a row the
    /// pinning could not account for, not an expected state.
    /// </summary>
    public const string PinningOffRemark =
        "Ejerkontraktens slutdato er ikke COS-1 — kontoen er ikke fastlagt ved leverandørskifte";

    /// <summary>
    /// What a human should look at: a missing E-number, a pinning that does not add up, and the
    /// placement questions.
    /// </summary>
    /// <param name="isOnMigratedContract">Whether the agreement sits on the contract that owned the
    /// metering point at cutover. Only sharpens the competing-agreements remark — on its own it is
    /// a column, not a finding.</param>
    /// <param name="meteringPointsTried">Settl metering points on the account, all of which were
    /// looked up. Reported in the missing-E-number remark so the failed search can be repeated.</param>
    public static List<string> BuildRemarks(
        MobilePayContract agreement, MobilePayMatchStatus status, bool pinningOff = false,
        bool? isOnMigratedContract = null, int meteringPointsTried = 0)
    {
        var remarks = new List<string>();

        if (status == MobilePayMatchStatus.NoSettlCustomerNumber)
            remarks.Add(MissingAgreement(meteringPointsTried, agreement.SourceContractNumber));

        if (pinningOff)
            remarks.Add(PinningOffRemark);

        // Competing agreements. Where the pinning is known it answers which one is live, so say so
        // rather than leaving a bare count that reads as "the report does not know" — it does.
        if (agreement.AgreementCountOnAccount > 1)
            remarks.Add(isOnMigratedContract switch
            {
                true => $"FLERE AFTALER på kundenr. ({agreement.AgreementCountOnAccount}) — denne sidder på den migrerede kontrakt",
                false => $"FLERE AFTALER på kundenr. ({agreement.AgreementCountOnAccount}) — denne sidder IKKE på den migrerede kontrakt",
                null => $"FLERE AFTALER på kundenr. ({agreement.AgreementCountOnAccount})"
            });

        if (agreement.SourceKind == MobilePaySourceKind.SubContract)
            remarks.Add(SubContractRemark);

        return remarks;
    }

    /// <summary>
    /// Blocking means the sheet cannot be handed over as it stands: either the customer is migrated
    /// and Settl has no payment agreement, or several agreements compete and a human must pick.
    /// </summary>
    /// <param name="isOnMigratedContract">Whether the agreement sits on the contract that owned its
    /// metering point at cutover. Several agreements on one customer number are only a decision when
    /// it is unclear which of them is live; where this is <c>true</c> the agreement has its own
    /// migrated contract and there is nothing to pick, so the count is reported without blocking.
    /// A customer with one contract per metering point — account 366645 in production — otherwise
    /// turned up entirely in red for having done nothing unusual.</param>
    public static MobilePayRowSeverity Classify(
        MobilePayContract agreement, MobilePayMatchStatus status, IReadOnlyList<string> remarks,
        bool? isOnMigratedContract = null)
    {
        if (status == MobilePayMatchStatus.NoSettlCustomerNumber)
            return MobilePayRowSeverity.Blocking;

        if (agreement.AgreementCountOnAccount > 1 && isOnMigratedContract != true)
            return MobilePayRowSeverity.Blocking;

        return remarks.Count > 0 ? MobilePayRowSeverity.Review : MobilePayRowSeverity.None;
    }
}
