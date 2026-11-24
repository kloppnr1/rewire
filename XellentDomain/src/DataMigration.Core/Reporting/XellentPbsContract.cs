namespace DataMigration.Core.Reporting;

/// <summary>
/// Where under the customer number the PBS agreement was found.
///
/// <para>Same three-way split as MobilePay's <c>MobilePaySourceKind</c>, and for the same reason:
/// being a main contract is a property of having sub-contracts, not of lacking a parent. Reading
/// "Hovedkontrakt" off a null <c>Referencetomaincontract</c> labels every standalone contract as a
/// main one.</para>
/// </summary>
public enum PbsSourceKind
{
    /// <summary>Agreement sits on a contract that other contracts reference as their main.</summary>
    MainContract,

    /// <summary>Agreement sits on a sub-contract (<c>Referencetomaincontract</c> set).</summary>
    SubContract,

    /// <summary>Agreement sits on a contract with neither a parent nor any sub-contracts.</summary>
    Standalone
}

/// <summary>
/// One PBS agreement on one metering point, with the provenance needed to check the attribution by
/// hand: which contract carried it, of which kind, over which period, and how many other contracts
/// lost that metering point to it.
///
/// <para>The last part is what PBS has instead of MobilePay's "is the agreement on the migrated
/// contract" column. MobilePay reads Settl's per-metering-point change-of-supplier date and asks
/// whether the agreement sits on the contract that owned the point then. PBS decides the owner
/// itself — <c>PbsContractSelection</c> keeps the newest contract that still ran at the cutover —
/// so the equivalent question is how contested that decision was.</para>
/// </summary>
public class XellentPbsContract
{
    public string ContractNumber { get; set; } = null!;
    public string CustomerAccount { get; set; } = null!;
    public int PbsAgreementNumber { get; set; }
    public string? MeteringpointId { get; set; }
    public string? Referencetomaincontract { get; set; }

    /// <summary>Main, sub- or standalone placement of the contract the agreement sits on.</summary>
    public PbsSourceKind SourceKind { get; set; }

    /// <summary>
    /// Start date of the contract that won the metering point. It is the tie-breaker the selection
    /// used — the newest in-force contract wins — so a reader checking a contested metering point
    /// needs to see it.
    /// </summary>
    public DateOnly? ContractStartDate { get; set; }

    /// <summary>
    /// End date of that contract, or null where Xellent's <c>1900-01-01</c> sentinel says there is
    /// none. Held against the profile's change-of-supplier date it tells a migrated contract (ends
    /// the day before) from one that merely happens to still be open.
    /// </summary>
    public DateOnly? ContractEndDate { get; set; }

    /// <summary>
    /// Distinct PBS agreement numbers still in force on the customer number. Greater than 1 means
    /// the customer's metering points do not all pay the same way, so a row cannot be read as
    /// speaking for the whole account.
    ///
    /// <para>An agreement number sits on exactly one contract, and a contract carrying one names
    /// exactly one consumption metering point (AXDB50 2026-08-07). So this counts metering points
    /// paid for <b>separately</b> — equally a count of agreements and of the contracts that bear
    /// them.</para>
    ///
    /// <para>It is <b>not</b> the account's metering-point count. A main contract's agreement also
    /// pays for its sub-contracts' points, which have none of their own: 4.100 accounts hold more
    /// metering points than agreements, one of them 411 points on a single agreement.</para>
    ///
    /// <para><b>Counted only over contracts that name a metering point.</b> An agreement on an
    /// umbrella main contract — which owns none — is not counted, so a row can show 0 beside a
    /// <see cref="PbsAgreementNumber"/> it inherited from exactly such a contract. 12.460 accounts
    /// in AXDB50_TEST carry their PBS that way, and 2.368 accounts in AXDB50 hold more agreements
    /// than metering points for the same reason. Widening the count is a change to what the number
    /// means, not a fix, and has not been made.</para>
    /// </summary>
    public int AgreementCountOnAccount { get; set; }

    /// <summary>
    /// How many of the account set's contracts named this metering point at all, this one included.
    /// One means the attribution was uncontested; more means the selection had to choose.
    /// </summary>
    public int CandidatesOnMeteringPoint { get; set; }

    /// <summary>
    /// How many of those were ruled out for having ended before the cutover — former holders of the
    /// metering point. Counted so a contested row shows whether the competition was real (two live
    /// contracts) or historical (one live, the rest moved out).
    /// </summary>
    public int ExpiredCandidatesOnMeteringPoint { get; set; }

    /// <summary>
    /// The contract the PBS agreement was actually read from, when that is <b>not</b>
    /// <see cref="ContractNumber"/> — the customer pays through an agreement sitting on another of
    /// their contracts, and the metering point's own contract has none.
    ///
    /// <para>Null is the ordinary case: the agreement came from the contract that owns the metering
    /// point. Set it only on the fallback, so a reader can tell the two apart without comparing
    /// numbers.</para>
    ///
    /// <para>The report has to state this. Without it <c>ContractNumber</c> and
    /// <see cref="PbsAgreementNumber"/> point at two different contracts on the same row and say
    /// nothing about it.</para>
    /// </summary>
    public string? PbsAgreementSourceContract { get; set; }

    /// <summary>
    /// Delivery category of <see cref="PbsAgreementSourceContract"/>. Worth naming because it is
    /// routinely a different commodity — a Vand/Varme contract carrying the agreement for an
    /// electricity metering point is exactly the case this fallback exists for.
    /// </summary>
    public string? PbsAgreementSourceDeliveryCategory { get; set; }
}
