using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Whether the customer can be handed over to the payment service.
///
/// <para>The sheet's job is to give the payment service a Settl customer number (E-number) alongside
/// the Xellent MobilePay agreement id, so it can create the MobilePay agreement on the Settl side. It
/// is therefore about whether the E-number could be found — <b>not</b> about whether a MobilePay
/// agreement already exists in Settl. Almost none do: Settl production held 5 MobilePay payment
/// agreements against 11.500 in total on 2026-07-30, the rest FI-Kort and Betalingsservice. Creating
/// the missing ones is what this sheet exists for.</para>
/// </summary>
public enum MobilePayMatchStatus
{
    /// <summary>
    /// The customer's Settl E-number was found — hand the row over. Any payment agreement on the
    /// metering point supplies it, whatever its payment method, because the E-number identifies the
    /// customer and not the agreement.
    /// </summary>
    Matched,

    /// <summary>
    /// The customer is migrated — a Settl metering point resolves to the account — but no payment
    /// agreement of any method exists for it, so there is no E-number and the payment service has
    /// nothing to key on. The handover cannot proceed for this customer until one exists.
    /// </summary>
    /// <para>There is deliberately no "not migrated" status. Settl decides the population —
    /// agreements are only read for accounts the scan resolved — so every row belongs to a migrated
    /// customer by construction. The status existed while the service read every agreement in the
    /// data area and labelled the surplus "not migrated", a claim it had no basis for: the scan
    /// paginates market-v3 only, so a customer migrated on water or heat alone was indistinguishable
    /// from one whose wave had not run (617 of 4.763 accounts with an active MobilePay agreement
    /// have no electricity contract part at all, production 2026-07-30).</para>
    NoSettlCustomerNumber
}

/// <summary>
/// How much attention a row needs before the sheet is handed to the payment service.
/// </summary>
public enum MobilePayRowSeverity
{
    /// <summary>Nothing to check — agreement found on a main contract and matched in Settl.</summary>
    None,

    /// <summary>Usable, but the pick rests on an assumption worth confirming.</summary>
    Review,

    /// <summary>Cannot be handed over as it stands — a human has to decide or investigate.</summary>
    Blocking
}

/// <summary>
/// One active MobilePay agreement matched against Settl, with the provenance needed to check it
/// by hand. A MobilePay agreement can sit on any contract under the customer number, so every
/// row states where its agreement was found rather than leaving it implied.
/// </summary>
public class MobilePayMatchReportRow
{
    /// <summary>Xellent customer number the agreement belongs to.</summary>
    public string Account { get; init; } = "";

    /// <summary>Contract the agreement id is actually stored on.</summary>
    public string SourceContractNumber { get; init; } = "";

    /// <summary>Whether that contract is a main or a sub-contract.</summary>
    public MobilePaySourceKind SourceKind { get; init; }


    /// <summary>Distinct active agreements on the customer number; &gt;1 needs a manual decision.</summary>
    public int AgreementCountOnAccount { get; init; }

    public MobilePayMatchStatus Status { get; init; }

    /// <summary>
    /// The Settl metering point the row is reported against — the one that pinned to this account
    /// at change of supply, and that <see cref="Cos"/> and <see cref="MigratedContractNumber"/> are
    /// read from. Stated whether or not the row matched: a row with no E-number still rests on a
    /// metering point, and leaving it blank made the pinning columns look like they came from
    /// nowhere. Empty only where the account resolved no metering point at all.
    /// </summary>
    public string MeteringPointId { get; init; } = "";

    /// <summary>
    /// What actually keyed the row into Settl's payment-agreement export: a metering point id, or —
    /// where the agreement carries none — the joint invoice number (<see cref="IsJoint"/> says
    /// which). Empty when nothing matched, which is exactly
    /// <see cref="MobilePayMatchStatus.NoSettlCustomerNumber"/>.
    ///
    /// <para>Separate from <see cref="MeteringPointId"/> because the two answer different questions
    /// and only sometimes hold the same value. One column serving both meant a joint match showed no
    /// metering point, and an unmatched row showed neither.</para>
    /// </summary>
    public string MatchKey { get; init; } = "";

    /// <summary>
    /// How many Settl metering points resolve to this account at change-of-supplier. Zero means the
    /// customer is not migrated.
    /// </summary>
    public int SettlMeteringPointCount { get; init; }

    /// <summary>
    /// End date of <see cref="SourceContractNumber"/> — the contract this row's agreement actually
    /// sits on. Held against <see cref="Cos"/> it is what tells a leftover agreement from a live
    /// one: the migrated contract ends the day before change of supply, an abandoned one long
    /// before. Null where Xellent has no end date.
    /// </summary>
    public DateOnly? SourceContractEndDate { get; init; }

    /// <summary>
    /// The Xellent contract that owned the metering point at change of supply — the one that was
    /// migrated. Reported so the attribution can be checked rather than trusted. Empty when the
    /// metering point produced no pinning at all.
    /// </summary>
    public string MigratedContractNumber { get; init; } = "";

    /// <summary>Settl's change-of-supplier date for the metering point the row is reported against.</summary>
    public DateOnly? Cos { get; init; }

    /// <summary>
    /// Whether this agreement sits on <see cref="MigratedContractNumber"/>. Where a customer holds
    /// competing agreements this is what separates them: an agreement on a contract that ended long
    /// before cutover, and was never cancelled in Xellent, is a leftover. Null when the row has no
    /// pinning to compare against.
    /// </summary>
    public bool? IsOnMigratedContract { get; init; }

    /// <summary>Settl's external customer number (E-number), from the payment agreement CSV.</summary>
    public string? EnummerSettl { get; init; }

    /// <summary>
    /// Payment method of the agreement the E-number was taken from. Stated on the row because the
    /// match ignores the method, so a green line says "we have the customer's E-number" and not
    /// "MobilePay already exists in Settl" — and the reader needs to be able to tell which.
    ///
    /// <para>Settl's export held 5 MobilePay agreements out of 11.500 on 2026-07-30, so at most 5
    /// rows could have drawn their E-number from one. That bound is what the figure supports; the
    /// split between FI-Kort and Betalingsservice among matched rows has not been measured. Expect
    /// the MobilePay share to rise as the handover does its work.</para>
    /// </summary>
    public string? EnummerSourcePaymentMethod { get; init; }

    public string MobilePayAgreementId { get; init; } = "";

    /// <summary>
    /// When the agreement was cancelled in Xellent, or null while it stands. Only ever set on a run
    /// that asked for cancelled agreements.
    ///
    /// <para>Held against <see cref="Cos"/> it says which kind of cancellation it was: after the
    /// change of supply, the migration closed it; before, the customer did. Same distinction PBS
    /// draws with <c>Pbstransdate</c>.</para>
    /// </summary>
    public DateOnly? CancelDate { get; init; }

    /// <summary>Matched through the joint invoice number rather than the metering point.</summary>
    public bool IsJoint { get; init; }

    /// <summary>Human-readable reasons this row needs review. Empty when nothing is wrong.</summary>
    public IReadOnlyList<string> Remarks { get; init; } = [];

    public MobilePayRowSeverity Severity { get; init; }
}

/// <summary>
/// What the scan itself saw, as opposed to what came out of it. These numbers survive row
/// filtering: narrowing to a profile's accounts does not change how much of Settl was read.
///
/// <para>They are asserted rather than merely logged because every production failure in this
/// command has shown up here first. A missing <c>SetDeliveryCategory</c> made
/// <see cref="SettlMeteringPointsScanned"/> zero, and a contract-end filter made
/// <see cref="MigratedAccountsResolved"/> collapse — in both cases every row silently came back
/// "not migrated" and the run looked merely empty rather than broken.</para>
/// </summary>
/// <param name="SettlMeteringPointsScanned">Metering points read from Settl. Zero means the scan
/// never saw the environment, not that the environment is empty.</param>
/// <param name="MigratedAccountsResolved">Xellent accounts that at least one Settl metering point
/// pinned to via COS-1. These, and only these, are the report's population.</param>
/// <param name="AgreementsRead">Active MobilePay agreements found on those accounts.</param>
public sealed record MobilePayScanFacts(
    int SettlMeteringPointsScanned,
    int MigratedAccountsResolved,
    int AgreementsRead);

public class MobilePayMatchResult
{
    /// <summary>
    /// Counts derived from the rows themselves. The only place the tallies are defined, so a
    /// filtered subset (a profile view) cannot end up reporting the unfiltered totals — and the
    /// dashboard cannot drift by recomputing them by hand.
    /// </summary>
    /// <param name="scan">What the scan saw. Carry it through when re-projecting a filtered row
    /// set: the rows narrow, the scan does not.</param>
    public static MobilePayMatchResult From(
        IEnumerable<MobilePayMatchReportRow> rows, MobilePayScanFacts? scan = null)
    {
        var list = rows as List<MobilePayMatchReportRow> ?? [.. rows];
        return new MobilePayMatchResult
        {
            Scan = scan,
            Matched = list.Count(r => r.Status == MobilePayMatchStatus.Matched && !r.IsJoint),
            MatchedJoint = list.Count(r => r.IsJoint),
            NoSettlCustomerNumber = list.Count(r => r.Status == MobilePayMatchStatus.NoSettlCustomerNumber),
            NeedsReview = list.Count(r => r.Remarks.Count > 0),
            Rows = list
        };
    }

    public int Matched { get; init; }
    public int MatchedJoint { get; init; }

    /// <summary>Migrated customers with no Settl E-number, so nothing to hand over — the action list.</summary>
    public int NoSettlCustomerNumber { get; init; }

    /// <summary>What the scan saw. Null only where a caller builds a result without scanning.</summary>
    public MobilePayScanFacts? Scan { get; init; }


    /// <summary>Rows carrying at least one remark.</summary>
    public int NeedsReview { get; init; }

    public List<MobilePayMatchReportRow> Rows { get; init; } = [];
}
