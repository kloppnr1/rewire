using DataMigration.Core.Reporting;

namespace DataMigration.OpsCli.Reporting;

public class PbsMatchResponse
{
    public int Matched { get; init; }
    public int MatchedJoint { get; init; }
    public int UnmatchedXellent { get; init; }
    public int UnmatchedSettl { get; init; }
    public int Mismatch { get; init; }
    public List<PbsMatchReportRow> Rows { get; init; } = [];
}

/// <summary>
/// One row of the PBS match. Where the row has a Xellent side, it carries the provenance of that
/// side — which contract the agreement was found on, of which kind, over which period, and how
/// contested the metering point was — so the attribution can be checked rather than trusted.
///
/// <para>Those columns are null, not zero or a default enum value, on a Settl-only row. Such a row
/// has no Xellent contract at all, and <c>Standalone</c> or <c>0 kandidater</c> would be a claim
/// about a contract that was never found.</para>
/// </summary>
public class PbsMatchReportRow
{
    public string? CustomerNumber { get; set; }
    public string? ContractNumber { get; set; }

    /// <summary>Main, sub- or standalone placement of the contract carrying the PBS agreement.</summary>
    public PbsSourceKind? SourceKind { get; set; }

    /// <summary>Start date of that contract — the tie-breaker the selection used on a contested
    /// metering point (newest in-force contract wins).</summary>
    public DateOnly? ContractStartDate { get; set; }

    /// <summary>End date of that contract, null where Xellent has none. A contract ending the day
    /// before the change of supplier is one the migration itself ended.</summary>
    public DateOnly? ContractEndDate { get; set; }

    /// <summary>Distinct PBS agreement numbers in force on the customer number. Above 1 means the
    /// account's metering points do not all pay the same way.</summary>
    public int? AgreementCountOnAccount { get; set; }

    /// <summary>Contracts that named this metering point, this one included. 1 means the
    /// attribution was uncontested.</summary>
    public int? CandidatesOnMeteringPoint { get; set; }

    /// <summary>How many of those were ruled out for ending before the cutover — former holders of
    /// the metering point rather than live competition.</summary>
    public int? ExpiredCandidatesOnMeteringPoint { get; set; }

    public int PbsAgreementNumber { get; set; }

    /// <summary>
    /// The contract <see cref="PbsAgreementNumber"/> was read from, when that is not
    /// <see cref="ContractNumber"/> — the customer pays through an agreement on another of their
    /// contracts, of any commodity, and this metering point's own contract has none.
    ///
    /// <para>Null in the ordinary case. Rendered into <see cref="Notes"/>, not into a column of its
    /// own: without it the row would name two contracts and say nothing about why.</para>
    /// </summary>
    public string? PbsAgreementSourceContract { get; set; }

    /// <summary>Delivery category of <see cref="PbsAgreementSourceContract"/> — routinely a
    /// different commodity, which is the point.</summary>
    public string? PbsAgreementSourceDeliveryCategory { get; set; }

    public string? MeteringpointId { get; set; }
    public string? PaymentAgreementNumber { get; set; }
    public string? ExternalCustomerNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public string? AgreementType { get; set; }
    public string? PayerName { get; set; }
    public string? JointInvoiceNumber { get; set; }
    public PbsMatchStatus MatchStatus { get; set; }

    /// <summary>
    /// Everything worth telling the reader about this row, joined for display. Built through
    /// <see cref="AddNote"/> rather than assigned: the row can pick up a remark in the match, and
    /// two more in the runner, and a plain assignment at any of those points silently drops the
    /// earlier ones.
    /// </summary>
    public string? Notes { get; set; }

    public void AddNote(string note)
    {
        if (string.IsNullOrEmpty(note)) return;
        Notes = string.IsNullOrEmpty(Notes) ? note : $"{Notes}; {note}";
    }
}

/// <summary>
/// How a row's two sides lined up.
///
/// <para><b>The population is Xellent's, not Settl's.</b> The report covers every account in the
/// profile, whether or not it ever reached Settl — unlike the MobilePay sheet, where Settl decides
/// who is in scope and a row therefore always belongs to a migrated customer. That difference is
/// what makes <see cref="UnmatchedXellent"/> ambiguous, and it is not fixable without giving PBS the
/// same Settl scan MobilePay runs. Deliberately not done; stated instead.</para>
/// </summary>
public enum PbsMatchStatus
{
    /// <summary>Metering point matches between Xellent and Settl</summary>
    Matched,
    /// <summary>Matched via Joint Invoice Number (Contractnum/Referencetomaincontract)</summary>
    MatchedJoint,

    /// <summary>
    /// Xellent has a PBS agreement for this metering point; Settl's payment agreement export has
    /// nothing for it.
    ///
    /// <para><b>Two different situations share this status, and the row cannot tell them apart:</b>
    /// the customer was migrated but Settl holds no payment agreement — a real finding — or the
    /// customer was never migrated at all, in which case there is nothing to find. Profiles are
    /// routinely migrated in part, so the count must not be read as a defect list.</para>
    /// </summary>
    UnmatchedXellent,

    /// <summary>Settl record exists but no corresponding Xellent PBS contract found</summary>
    UnmatchedSettl,
    /// <summary>Both exist but values differ</summary>
    Mismatch
}

public class SettlCsvRecord
{
    public string PaymentAgreementNumber { get; set; } = null!;
    public string PaymentMethod { get; set; } = null!;
    public string ExternalCustomerNumber { get; set; } = null!;
    public string CustomerName { get; set; } = null!;
    public string? PayerName { get; set; }
    public string AgreementType { get; set; } = null!;
    public string? MeteringpointId { get; set; }
    public string? JointInvoiceNumber { get; set; }
}
