using DataMigration.Core.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// The column vocabulary shared by both halves of <c>payment-match</c>.
///
/// <para>The two reports answer the same question about different payment methods, and until now
/// said so in different languages, in a different order, under different names — PBS in English
/// across HTML and CSV, MobilePay in Danish in a spreadsheet. Reading one against the other meant
/// translating first.</para>
///
/// <para>The MobilePay sheet is now exactly the shared vocabulary; PBS adds one column of its own,
/// because only PBS has to decide which contract owns a metering point.</para>
///
/// <para><b>Every column that says "aftale" means a PAYMENT agreement</b> —
/// <c>Pbsagreementnum</c> or <c>Mobilepaysubscriptionagre20135</c>. Xellent uses the same word for
/// <c>EXU_AGREEMENTTABLE</c>, the installation agreement that links a contract part to a delpoint,
/// and every metering point has one of those by construction. A header reading "Aftale-ID" was
/// therefore ambiguous in the one direction that matters: "Målepunkter med egen aftale" could be
/// read as "all of them". Spell it out. See <c>docs/xellent-datamodel.md</c> in
/// NewSettlement.XellentDomain.</para>
/// </summary>
public static class PaymentMatchColumns
{
    public const string Account = "Kundenummer";
    public const string SourceContract = "Betalingsaftale fundet på kontrakt";
    public const string ContractKind = "Kontrakttype";
    public const string ContractEnd = "Kontraktslutdato";
    /// <summary>
    /// How many of the customer's metering points are paid by an agreement of their own. Above 1
    /// means the customer does not pay for everything the same way, which is the only thing the
    /// number is read for.
    ///
    /// <para>Named after metering points because a payment-agreement number sits on exactly one
    /// contract, and such a contract names exactly one consumption metering point (AXDB50
    /// 2026-08-07). The earlier "Aftaler på kundenr." invited the wrong question — whether it
    /// counted contracts — and metering points are what a reader can act on.</para>
    ///
    /// <para><b>Not</b> the account's metering-point count: a main contract's payment agreement also
    /// pays for sub-contract points that have none of their own, up to 411 on one agreement. Hence
    /// "egen".</para>
    ///
    /// <para>Only metering-point-bearing contracts are counted, so a row that inherited its payment
    /// agreement from an umbrella main contract can show 0 beside a
    /// <see cref="AgreementId"/>. See <c>XellentPbsContract.AgreementCountOnAccount</c>.</para>
    /// </summary>
    public const string AgreementsOnAccount = "Målepunkter med egen betalingsaftale";
    public const string Status = "Status";
    public const string Remark = "Bemærkning";
    public const string MeteringPoint = "Målepunkt";
    public const string MatchKey = "Nøgle der matchede i Settl";
    public const string Enummer = "Enummer Settl";
    public const string EnummerPaymentMethod = "E-nummer hentet fra betalingsmetode";

    /// <summary>
    /// The contract that was migrated — the one owning the metering point at the cutover. Not
    /// necessarily <see cref="SourceContract"/>: an agreement can sit elsewhere under the customer
    /// number, on a MobilePay contract of any kind, or on a PBS row's main contract.
    /// </summary>
    public const string MigratedContract = "Migreret kontrakt";

    /// <summary>
    /// Whether the agreement sits on the migrated contract. The column that settles a row where the
    /// two contracts differ: an inherited or left-behind agreement is not the same claim as one
    /// found where the metering point actually lives.
    ///
    /// <para>PBS had no use for this until the main-contract fallback: before it, the row's contract
    /// was always the one the agreement came from, so the answer was "Ja" by construction and said
    /// nothing. Now it separates an inherited agreement from an own one.</para>
    /// </summary>
    public const string OnMigratedContract = "Betalingsaftale på migreret kontrakt";

    /// <summary>
    /// Change of supplier. MobilePay reads it per metering point from Settl; PBS has only the
    /// profile's one date for the whole run, and writes that. Deliberately kept in both even though
    /// it repeats on every PBS row: the two reports being readable against each other is worth more
    /// than sparing a column.
    /// </summary>
    public const string Cos = "COS";

    /// <summary>
    /// The Xellent agreement's own id — a PBS agreement number or a MobilePay agreement id. One
    /// column rather than two differently-named ones: it is the same thing about the same agreement,
    /// and the reports are meant to be read against each other.
    /// </summary>
    public const string AgreementId = "Betalingsaftale-ID";

    /// <summary>
    /// The agreement's own state in Xellent. The two payment methods are modelled differently — PBS
    /// by a transaction code (Aktiv / FI-Kort / Afmeldt / Ingen PBS), MobilePay by nothing but a
    /// cancellation date — so the values differ, but the question is the same: may this agreement be
    /// acted on?
    ///
    /// <para>MobilePay could not answer it while the reader returned uncancelled agreements only;
    /// the column would have read "Aktiv" on every row. With <c>--include-terminated</c> the
    /// cancelled ones come along and the column earns its place.</para>
    /// </summary>
    public const string AgreementStatus = "Betalingsaftale-status (Xellent)";

    /// <summary>
    /// The whole MobilePay sheet, and the first fifteen columns of the PBS one. A test asserts the
    /// two agree on this list, so the alignment cannot quietly rot the next time a column is added.
    /// </summary>
    public static readonly string[] Shared =
    [
        Account, SourceContract, ContractKind, ContractEnd, AgreementsOnAccount,
        Status, Remark, MeteringPoint, MatchKey, Enummer, EnummerPaymentMethod,
        MigratedContract, OnMigratedContract, Cos, AgreementId, AgreementStatus
    ];

    // --- PBS-only tail ---

    /// <summary>
    /// How many contracts named this metering point, this one included. 1 means the attribution was
    /// uncontested.
    ///
    /// <para>PBS-only because only PBS has to make the choice: it decides the metering point's owner
    /// from Xellent, where MobilePay reads Settl's change-of-supplier date and is told the answer.
    /// The companion count of how many were dropped for expiring before the cutover was removed —
    /// measured over a production profile 2026-08-07 it was <c>Kandidater − 1</c> on every one of
    /// the 111 contested rows, so it never said anything this column does not.</para>
    /// </summary>
    public const string CandidatesOnMeteringPoint = "Kandidater på målepunkt";

    public static readonly string[] Pbs = [.. Shared, CandidatesOnMeteringPoint];

    /// <summary>
    /// MobilePay's answer for <see cref="AgreementStatus"/>. Xellent has no code here — an agreement
    /// either carries a cancellation date or it does not — so there are two values where PBS has
    /// four.
    /// </summary>
    public static string MobilePayAgreementStatusLabel(DateOnly? cancelDate) =>
        cancelDate is null ? "Aktiv" : $"Opsagt {cancelDate:yyyy-MM-dd}";

    /// <summary>The MobilePay sheet is exactly the shared vocabulary — it has nothing PBS lacks.</summary>
    public static readonly string[] MobilePay = Shared;

    // --- Labels ---

    /// <summary>
    /// Null means the row has no Xellent contract at all — a Settl-only row. Naming a placement
    /// there would describe a contract that was never found.
    /// </summary>
    public static string ContractKindLabel(PbsSourceKind? kind, string empty = "") => kind switch
    {
        PbsSourceKind.MainContract => "Hovedkontrakt",
        PbsSourceKind.SubContract => "Underkontrakt",
        PbsSourceKind.Standalone => "Enkeltkontrakt",
        _ => empty
    };

    public static string ContractKindLabel(MobilePaySourceKind kind) => kind switch
    {
        MobilePaySourceKind.MainContract => "Hovedkontrakt",
        MobilePaySourceKind.SubContract => "Underkontrakt",
        _ => "Enkeltkontrakt"
    };

    public static string StatusLabel(PbsMatchStatus status) => status switch
    {
        PbsMatchStatus.Matched => "Match",
        PbsMatchStatus.MatchedJoint => "Match (fællesfaktura)",
        PbsMatchStatus.UnmatchedXellent => "Uden match (Xellent)",
        PbsMatchStatus.UnmatchedSettl => "Uden match (Settl)",
        PbsMatchStatus.Mismatch => "Uoverensstemmelse",
        _ => "Ukendt"
    };

    public static string StatusLabel(MobilePayMatchStatus status) => status switch
    {
        MobilePayMatchStatus.Matched => "E-nummer fundet",
        _ => "INTET E-nummer i Settl"
    };

    /// <summary>
    /// Names the kind of key, because the column holds two kinds that look alike. A joint invoice
    /// number in a column beside a metering point reads as a malformed metering point.
    /// </summary>
    public static string MatchKeyLabel(string? key, bool isJoint) =>
        string.IsNullOrEmpty(key) ? ""
        : isJoint ? $"Fællesfaktura {key}"
        : key;

    public static string YesNo(bool? value) => value switch
    {
        true => "Ja",
        false => "Nej",
        null => ""
    };

    // The outputs spell an absent value differently — the HTML table has always used "-", the CSV
    // and the spreadsheet an empty field — so the placeholder is the caller's to pick.
    public static string Date(DateOnly? value, string empty = "") =>
        value?.ToString("yyyy-MM-dd") ?? empty;

    public static string Count(int? value, string empty = "") =>
        value?.ToString() ?? empty;
}
