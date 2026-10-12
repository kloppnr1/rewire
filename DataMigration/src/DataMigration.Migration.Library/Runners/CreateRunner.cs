using System.Text;
using DataMigration.Core;
using DataMigration.Core.Models;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Xellent;
using DataMigration.Domain.Xellent.Validation;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.Migration.Services.Extraction;
using DataMigration.Migration.Services.Settl;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlClient;
using DataMigration.SettlClient.Internal;
using SettlClientImpl = DataMigration.SettlClient.SettlClient;

namespace DataMigration.Migration.Runners;


/// <summary>How a single customer ended up in Settl. Emitted per customer via
/// <see cref="CreateRunner.OnCustomerCompleted"/>.</summary>
public enum CustomerOutcomeKind
{
    Created,
    Existing,
    Failed
}

/// <summary>
/// Per-customer result, reported as each customer finishes so a caller can journal progress
/// while the run is still going. <paramref name="RepresentativeAccount"/> is the padded form —
/// the same value that appears in <see cref="MigrationReport.ExistingCustomerAccounts"/> and on
/// the report's customer DTOs — so outcomes and report rows join directly.
/// </summary>
public record CustomerOutcome(
    string RepresentativeAccount,
    CustomerOutcomeKind Kind,
    IReadOnlyList<string> Errors);

public record AccountGrouping(
    string RepresentativeAccount,
    List<string> MemberAccounts,
    string? GroupNum,
    string? GroupName,
    string? Cvr,
    Dictionary<string, string> AccountNames);

/// <summary>
/// Synthetic cross-CVR owner-customer surfaced to the dashboard so the migration UI can
/// render which child accounts hang under which owner.
/// </summary>
public record OwnerCustomerInfo(
    Guid OwnerId,
    string CustomerNumber,
    string ClusterKey,
    string? GroupName,
    string? Cvr,
    string? PostalCode,
    string? City,
    string? CountryCode,
    List<string> LinkedAccountnums);

/// <summary>
/// En ejer-klynge der ikke blev til en ejer-kunde, og hvorfor. Klyngen samles udelukkende på
/// TG4, så dens medlemmer kan have forskellige CVR — men ejer-kunden bygges på det laveste
/// medlems stamdata, og mangler det medlem eller dets CVR, bliver klyngen droppet. Det stod
/// før kun som en advarsel i loggen, hvor den drukner i en all-active-kørsel.
/// </summary>
public sealed record OwnerClusterSkipInfo(
    string ClusterKey,
    string? GroupName,
    string Reason,
    string? SourceAccount,
    IReadOnlyList<string> MemberAccounts);

public sealed record MpBillingExemptionInfo(
    string ProductElementCode,
    string TemplateMatched,
    double Percentage);

/// <summary>
/// Why this metering point's contract is not simply its product name: the contract template it
/// sits on, the product, and the customer identification the rule answered for. Carried per
/// metering point — like <see cref="MpBillingExemptionInfo"/> — so a report can show WHICH rule
/// moved it, not just where it ended up.
///
/// <para>The whole point of the rule is that it moves a handful of metering points and leaves
/// thousands of look-alikes on the same product alone. Two rows in a report reading contract
/// <c>V ErhVari</c> and contract <c>V Variabel</c> for the same product look like an inconsistency
/// until this says which rule decided each of them.</para>
/// </summary>
public sealed record MpContractRuleInfo(
    string ContractTemplate,
    string ProductNum,
    string Identification,
    string ContractName);

/// <summary>
/// Why this metering point's contract came from the charger it's linked to, not from its own
/// product: the MP's own El-ekstern product, the charger's live Charge_Abo product (or
/// <see cref="Configuration.ElectricityChargerContractRules.NoChargeKey"/> when the account's
/// charger has no live Charge/Charge_Abo part yet), and the contract the pair resolved to. Kept
/// separate from <see cref="MpContractRuleInfo"/> — there is no contract template or customer
/// identification here, both fields that record would otherwise leave misleadingly empty.
/// </summary>
public sealed record MpChargerContractRuleInfo(
    string ElProductNum,
    string ChargeAboProductNum,
    string ContractName);

/// <summary>
/// Per-MP price-list window for report display: the Settl PriceList <b>name</b> (translated
/// from the contract part's Xellent rate code via <see cref="DataMigration.Migration.Services.PriceListNameResolver"/>)
/// plus the contract part's validity window. CSV-independent — no Settl PriceList ID is resolved
/// (consumers want the name, not the GUID). <see cref="End"/> is null for open-ended parts
/// (Xellent <c>EndDateNull</c> sentinel).
/// </summary>
public sealed record MpPriceListInfo(
    string Name,
    DateTime Start,
    DateTime? End);

/// <summary>
/// Per-payer billing summary for report display — the FINAL translated values that land in
/// Settl (channel defaulting already applied: MobilePay is unsupported and becomes
/// PDF/FIK/BS; Xellent PBS-/MobilePay-ids are deliberately dropped at the translator).
/// </summary>
public sealed record CustomerPayerBillingInfo(
    string? PayerIdentifier,
    string? ElectronicNumber,
    string? InvoiceChannel,
    string? PaymentChannel,
    string? PayerType,
    string? BuyerReferenceIdentifier,
    string? AgreementIdentifier,
    string? OrderIdentifier,
    string? BookkeepingReference);

/// <summary>
/// Per-MP e-faktura-reference override: de felter der divergerer på tværs af payerens
/// kontrakter og derfor er skubbet ned på MP-niveau (jf. <c>MeteringPoint.BuyerInvoicing</c>).
/// EAN er aldrig på MP — den hører altid til payeren.
/// </summary>
public sealed record MpBillingOverrideInfo(
    string? OrderIdentifier,
    string? AgreementIdentifier,
    string? BuyerReferenceIdentifier);

/// <summary>
/// En kunde der er sorteret fra fordi hverken CVR eller CPR er udfyldt på disponent 1 —
/// uden identifikation kan kunden ikke oprettes i Settl. <see cref="SecondaryCvr"/> og
/// <see cref="SecondaryCpr"/> er disponent 2, taget med til orientering: de indgår ikke i
/// beslutningen, for det er disponent 1 der bliver til Settls identifikation.
/// <see cref="MeteringPointCount"/> er de målepunkter kunden ville have haft med.
///
/// <para><see cref="RawCvr"/>, <see cref="RawCpr"/> og <see cref="AlternativeNumber"/> er
/// felterne som de står i Xellent, ikke som mapperen efterlod dem. Uden dem kan rapporten
/// ikke skelne et tomt felt fra et der holder noget ubrugeligt — og det er to forskellige
/// opgaver for den der skal rette op. <see cref="Cause"/> er hvilken af dem det er;
/// <see cref="CauseText"/> er overskriften, båret med så report.html og dashboardet ikke
/// kan komme til at sige hver sit om samme gruppe.</para>
/// </summary>
public sealed record MissingIdentityCustomerInfo(
    string AccountNumber,
    string Name,
    string? AdditionalName,
    string? StreetName,
    string? PostalCode,
    string? City,
    string? SecondaryCvr,
    string? SecondaryCpr,
    int MeteringPointCount,
    string? RawCvr,
    string? RawCpr,
    string? AlternativeNumber,
    int AlternativeNumberCode,
    MissingIdentityCause Cause,
    string CauseText);

public class MigrationReport
{
    public List<AccountGrouping> AccountGroupings { get; set; } = [];
    public Dictionary<string, List<string>> AccountMeteringPoints { get; set; } = [];
    public Dictionary<string, List<string>> ContractMeteringPoints { get; set; } = [];
    public int CustomersExtracted { get; set; }
    public int CustomersCreated { get; set; }
    public int CustomersExisting { get; set; }
    public int CustomersFailed { get; set; }
    public int PayersExtracted { get; set; }
    public int PayersCreated { get; set; }
    public int PayersExisting { get; set; }
    public int PayersFailed { get; set; }
    public int MeteringPointsExtracted { get; set; }
    public int MeteringPointsCreated { get; set; }
    public int MeteringPointsExisting { get; set; }
    public int MeteringPointsFailed { get; set; }
    /// <summary>CustomerNumbers (padded form, matching <see cref="Customers"/>[].CustomerNumber) for customers found existing in Settl.</summary>
    public HashSet<string> ExistingCustomerAccounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Entries from <see cref="CreateOptions.SelectedAccounts"/> that matched no extracted
    /// customer. Surfaced structurally (not just as a log line) so a caller resuming an
    /// interrupted run can tell "these accounts left the scope" apart from "nothing ran".
    /// </summary>
    public List<string> MissingSelectedAccounts { get; set; } = [];
    /// <summary>PayerIdentifications der ramte i Settl — kan være legacy (un-padded) form for production-payers oprettet før pad-9-reglen.</summary>
    public HashSet<string> ExistingPayerIdentifications { get; set; } = new(StringComparer.Ordinal);
    /// <summary>MP identifications fundet i Settl under dry-run/create.</summary>
    public HashSet<string> ExistingMeteringPointIdentifications { get; set; } = new(StringComparer.Ordinal);
    public int CustomerMpsLinked { get; set; }
    public int CustomerMpsFailed { get; set; }
    public int PayerMpsLinked { get; set; }
    public int PayerMpsFailed { get; set; }
    public int ContractMpsLinked { get; set; }
    public int ContractMpsFailed { get; set; }
    public int BillingExemptionsApplied { get; set; }
    public int BillingExemptionsSkipped { get; set; }
    public int BillingExemptionsFailed { get; set; }
    public int SettlementGroupsAdded { get; set; }
    public int SettlementGroupsSkippedExisting { get; set; }
    public int SettlementGroupsFailed { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Log { get; set; } = [];
    public List<CreateCustomerDto> Customers { get; set; } = [];
    public List<CreatePayerDto> Payers { get; set; } = [];
    public List<MeteringPointDto> MeteringPoints { get; set; } = [];
    public HashSet<string> JoinedMeteringPoints { get; set; } = [];
    public Dictionary<Guid, string> ExternalOwners { get; set; } = [];
    public List<OwnerCustomerInfo> OwnerCustomers { get; set; } = [];

    /// <summary>Ejer-klynger der ikke blev til en ejer-kunde, med grunden. Medlemmerne er
    /// migreret som almindelige kunder — de hænger bare ikke under en ejer.</summary>
    public List<OwnerClusterSkipInfo> SkippedOwnerClusters { get; set; } = [];
    public List<FingerprintSplitInfo> FingerprintSplits { get; set; } = [];

    /// <summary>
    /// Kørte kørslen uden sammenlægning (all-active: én Settl-kunde per Xellent-konto)?
    /// Bæres med fordi to af rapportens afsnit kun betyder noget når der merges:
    /// fingerprint-splits og email-advarslen siger begge "de her konti bliver til hver sin
    /// Settl-kunde fordi fingeraftrykket varierer", og uden merge sker det uanset. Uden
    /// flaget kan visningen ikke skelne, for den ser kun resultatet.
    /// </summary>
    public bool RepresentativeMergeDisabled { get; set; }

    /// <summary>Kunder der er sorteret fra fordi de hverken har CVR eller CPR. De er ikke
    /// migreret og optræder derfor ikke i nogen anden liste eller tæller i rapporten.</summary>
    public List<MissingIdentityCustomerInfo> CustomersWithoutIdentity { get; set; } = [];

    /// <summary>El-ekstern only: kunder uden både CVR og CPR (og intet i alternativ-kundenummeret
    /// heller) som i stedet for at blive sorteret fra har fået det fiktive CPR
    /// "1111110000" og migreres som en almindelig Person-kunde. Optræder derfor i alle
    /// andre lister og tællere i rapporten, ligesom en hvilken som helst anden kunde.</summary>
    public List<MissingIdentityCustomerInfo> CustomersWithFictiveCpr { get; set; } = [];

    /// <summary>Fingeraftrykket per repræsentant, som <see cref="FingerprintSplits"/> er udledt af.
    /// Bæres med ud så en batched kørsel kan lægge sine batches sammen og stille split-spørgsmålet
    /// én gang over hele scopet: en batch alene ser et CVR's konti spredt over andre batches som
    /// var de urelaterede.</summary>
    public Dictionary<string, AccountFingerprint> Fingerprints { get; set; } = [];

    /// <summary>De felter email-advarslen læser, per konto — taget af den færdigt mappede kunde.
    /// Bæres med ud af samme grund som <see cref="Fingerprints"/>: advarslen tæller konti med
    /// samme navn og adresse, og en batch kan kun tælle sin egen andel.</summary>
    public List<CustomerEmailFacts> CustomerEmailFacts { get; set; } = [];
    public Dictionary<string, string> MpContractNames { get; set; } = [];
    public List<DataMigration.Migration.Services.ContractNameResolver.MpContractLink> MpContractLinks { get; set; } = [];
    public Dictionary<string, string> MpProductNums { get; set; } = [];
    public Dictionary<string, string> MpPaymentTerms { get; set; } = [];
    /// <summary>Per-MP billing exemption decision (mp-identification → matched template).</summary>
    public Dictionary<string, MpBillingExemptionInfo> MpBillingExemptions { get; set; } = [];
    /// <summary>Per-MP template+product contract rule(s) (mp-identification → the rule(s) that
    /// decided its contract). A list, not a single value: an MP with more than one contract part
    /// (a current one plus a staged future one) is resolved once per part, and each can hit the
    /// rule with a different answer. Only the metering points the rule actually moved appear;
    /// everybody else got their contract from the product mapping or the product name, which the
    /// report already shows.</summary>
    public Dictionary<string, List<MpContractRuleInfo>> MpContractRules { get; set; } = [];
    /// <summary>Per-MP charger contract rule(s) (mp-identification → the electricity+charger product
    /// pair(s) that decided its contract). A list for the same reason as <see cref="MpContractRules"/>.
    /// Only the metering points this rule actually moved appear.</summary>
    public Dictionary<string, List<MpChargerContractRuleInfo>> MpChargerContractRules { get; set; } = [];
    /// <summary>Per-MP price-list windows (mp-identification → name + start/end per contract part).
    /// Name resolved CSV-free from the rate code; list because an MP can span several parts/windows.</summary>
    public Dictionary<string, List<MpPriceListInfo>> MpPriceLists { get; set; } = [];
    /// <summary>Per-customer payer billing summaries (RAW Xellent accountnum → payer list, keyed
    /// like <see cref="CustomerSettlementGroups"/> so account-tree lookups match). Final
    /// translated Settl values — see <see cref="CustomerPayerBillingInfo"/>.</summary>
    public Dictionary<string, List<CustomerPayerBillingInfo>> CustomerPayerBilling { get; set; } = [];
    /// <summary>Per-MP e-faktura-reference override (mp-identification → divergerende felter på MP-niveau). Kun MPs hvor <c>BuyerInvoicing != null</c>.</summary>
    public Dictionary<string, MpBillingOverrideInfo> MpBillingOverrides { get; set; } = [];

    /// <summary>
    /// Ladestandere knyttet til det el-målepunkt de lader fra (mp-identification → installationer).
    /// Koblingen kommer fra el-siden: en kontraktdel med et ladestander-produkt peger på præcis ét
    /// E17-forbrugsmålepunkt. Rent display — populeres også i dry-run og offline, ligesom
    /// <see cref="CustomerSettlementGroups"/>. Der migreres ingen ladestandere; rækkerne er her
    /// for at kunne se INDEN migreringen at el-kontrakten lukkes mens ladestanderen bliver stående.
    /// </summary>
    public Dictionary<string, List<EvCharger>> EvChargersByMeteringPoint { get; set; } = [];

    /// <summary>
    /// Ladestandere der ikke kunne knyttes til ét målepunkt (Xellent accountnum → installationer):
    /// enten har kontoen intet ladestander-el-produkt, eller også peger produkterne på flere
    /// kandidat-målepunkter — og i det tilfælde står der også en advarsel fra domænet. Nøglen er
    /// det RÅ accountnum ladestander-kontrakten hænger under, ikke nødvendigvis en repræsentant.
    /// </summary>
    public Dictionary<string, List<EvCharger>> EvChargersByAccount { get; set; } = [];
    /// <summary>Per-customer forventet Settl Settlement Group(s) (account number → gruppenavne),
    /// opløst lokalt fra kontrakternes billing-plan-koder via <see cref="CreateOptions.SettlementGroupMap"/>.
    /// Matcher granulariteten af den faktiske assignment (kunden tilføjes gruppen, ikke MP'et).
    /// Rent display — populeres også i dry-run/offline, modsat de live-mutations-tællere
    /// <see cref="SettlementGroupsAdded"/> m.fl.</summary>
    public Dictionary<string, List<string>> CustomerSettlementGroups { get; set; } = [];
    /// <summary>
    /// How each account came out of the metering-point phase — landed, failed, or not attempted.
    /// Filled only by a metering-points-only run (<see cref="CreateOptions.MeteringPointsOnly"/>),
    /// which otherwise reports not a single customer outcome: without this a caller keeping score
    /// has only "did the whole run go through" to go on, and one refused metering point costs every
    /// other account in the batch its bookkeeping. An empty list means the phase answered about
    /// nobody — not that nobody landed. The account number is the RAW Xellent form, unlike
    /// <see cref="CustomerOutcome"/>. Each outcome also names the account's metering points and how
    /// each one came out, because such a run leaves no Settl customer to ask for them afterwards.
    /// </summary>
    public List<DataMigration.Migration.Services.MeteringPointAccountOutcome> MeteringPointAccountOutcomes { get; set; } = [];
    public List<string> CollectorInfos { get; set; } = [];
    public List<string> CollectorWarnings { get; set; } = [];
    public List<string> CollectorErrors { get; set; } = [];
    public int CollectorErrorCount { get; set; }
    public int CollectorWarningCount { get; set; }
    public List<ValidationEntry> ValidationEntries { get; set; } = [];
}

public class CreateOptions
{
    public string[] Accounts { get; set; } = [];

    /// <summary>
    /// Extract everything the data area, commodity and reference date select, instead of an
    /// explicit account list. Set for whole-commodity runs: enumerating tens of thousands of
    /// accounts only to send them back as query parameters exceeds what SQL Server will plan,
    /// and the mapper's own predicates already express the same scope.
    /// <see cref="Accounts"/> is ignored when this is set; <see cref="SelectedAccounts"/> still
    /// applies afterwards, which is how a resume narrows a full extract to the outstanding few.
    /// </summary>
    public bool LoadEntireDataArea { get; set; }
    public string DataAreaId { get; set; } = "hol";
    public required string DeliveryCategory { get; set; }
    public bool DryRun { get; set; }

    /// <summary>
    /// Fully offline dry-run: every Settl client is swapped for a no-op mock so zero API calls
    /// are made. All entities report as "would create" (no existing-detection, no contract-GUID
    /// resolution, no settlement-group validation). Forces <see cref="DryRun"/>. Use for fast
    /// extraction/payload smoke-tests without Settl reachability.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// When true (default), the per-entity Settl existence-searches (customer/payer/MP) are
    /// served from a few up-front paginated bulk-loads held in memory instead of one round-trip
    /// per entity. Dry-run trusts the snapshot fully (a miss = not-found, no call); live falls
    /// through to a real search on a miss so a stale snapshot can never cause a duplicate create.
    /// Ignored when <see cref="Offline"/> is set. Set false to restore per-entity lookups.
    /// </summary>
    public bool BulkCacheReads { get; set; } = true;

    /// <summary>
    /// Hvor mange el-målepunkter der sendes i ét opret-kald i fasen før kunde-løkken. Settl er
    /// ikke umiddelbart konsistent, så et nyoprettet målepunkt skal læses tilbage efter en pause
    /// — og gevinsten er præcis hvor mange målepunkter der deles om den pause. Et fejlet bundt
    /// halveres ned til enkeltmålepunkter, så et for stort tal koster tid, aldrig rigtighed.
    ///
    /// <para>Standarden er sat højt med vilje. Målt på featuretest 13-08-2026: 31 målepunkter i
    /// ét kald kostede to sekunders ventetid i alt, mod 11 sekunder for 13 målepunkter én ad
    /// gangen. Hvor stort et bundt Settl selv tager imod, er ikke afprøvet ud over 31 — halvering
    /// ved afvisning er det der gør et for højt tal ufarligt.</para>
    /// </summary>
    public int MeteringPointCreateBatchSize { get; set; } = 500;

    /// <summary>
    /// Hvor mange målepunkts-opslag der må være undervejs mod Settl på én gang — både
    /// findes-det-i-forvejen-opslagene og tilbagelæsningen efter en oprettelse. Standarden er 8.
    ///
    /// <para>Det her er håndtaget hvis Settl begynder at svare 429: skru ned, kør igen. Sæt den
    /// til 1 for at få den gamle adfærd, hvor opslagene gik ét ad gangen. Ingen tælling og ingen
    /// rækkefølge ændrer sig af tallet — kun hvor mange kald der er i luften samtidig.</para>
    /// </summary>
    public int SearchConcurrency { get; set; } = 8;

    /// <summary>
    /// Opret kun målepunkterne og stop der — før kunder, betalere, ejer-kunder, billing-config,
    /// fritagelser og koblinger. Målepunkterne er den dyre og skrøbelige del af en all-active-
    /// kørsel; det her gør det muligt at tage dem alene, se hvad der lykkedes, og først derefter
    /// lade resten følge efter.
    ///
    /// <para>Genoptagelsen er en helt almindelig kørsel over samme scope: hvert trin i
    /// kunde-løkken slår op før det skriver, og billing-config og fritagelser er bygget som
    /// backfill over alle målepunkter — ikke kun nyoprettede.</para>
    ///
    /// <para>Kræver en levende kørsel. Målepunkts-fasen springes helt over i dry-run og offline,
    /// så kombinationen ville ikke lave noget som helst.</para>
    /// </summary>
    public bool MeteringPointsOnly { get; set; }

    /// <summary>
    /// Let Settl fill in municipality code, street code and DAR identifier on an electricity MP
    /// when they can be derived from the rest of the address we send. On by default.
    /// </summary>
    public bool EnrichAddress { get; set; } = true;

    /// <summary>
    /// Create an electricity MP even when no DAR entry matches the address exactly. Off by
    /// default, so a mismatched address fails loudly instead of landing unvalidated — a run turns
    /// it on once the rejection rate is known and accepted. Settl validates in featuretest only.
    /// </summary>
    public bool SkipAddressValidation { get; set; }

    /// <summary>
    /// El-ekstern only, off by default: an account with genuinely no CVR/CPR anywhere in Xellent
    /// is kept and given the fictive CPR "1111110000" instead of being excluded. See
    /// <see cref="Services.Extraction.MissingIdentityFilter"/>. Has no effect on other delivery
    /// categories even when set — the El-ekstern check happens alongside this flag, not instead
    /// of it, so turning this on for a water/heat run cannot accidentally start stamping those
    /// customers too.
    /// </summary>
    public bool AssignFictiveCprForNoIdentity { get; set; }

    public Dictionary<string, string>? GroupOwners { get; set; }
    public Dictionary<string, string>? AccountOwners { get; set; }
    public Dictionary<string, Configuration.ProductNumContractEntry>? ProductNumContractMap { get; set; }

    /// <summary>
    /// Contract template → product pattern → Settl contract per customer identification. More
    /// specific than <see cref="ProductNumContractMap"/> and consulted before it — see
    /// <see cref="DataMigration.Migration.Services.ContractNameResolver"/> for the full precedence.
    /// </summary>
    public Dictionary<string, Dictionary<string, Configuration.TemplateProductContractEntry>>? TemplateProductContractMap { get; set; }

    /// <summary>
    /// Xellent contract number → <c>Contracttemplatenum</c>. <b>Run data, not profile config</b>:
    /// filled by <see cref="DataMigration.Migration.Services.MigrationDomainLoad"/> — the one place
    /// every host passes through and the only one holding a Xellent connection — and only when
    /// <see cref="TemplateProductContractMap"/> is configured, so a run that does not use the
    /// template dimension issues no extra query. Empty leaves the template rules inert.
    /// </summary>
    public Dictionary<string, string>? ContractTemplatesByContractNum { get; set; }
    public Dictionary<string, string>? GroupNumContractMap { get; set; }
    public Dictionary<string, string>? AccountNumContractMap { get; set; }

    /// <summary>
    /// El-ekstern productnum → charger's live Charge_Abo productnum (or
    /// <see cref="Configuration.ElectricityChargerContractRules.NoChargeKey"/>) → Settl contract.
    /// More specific than <see cref="TemplateProductContractMap"/> and consulted before it — see
    /// <see cref="DataMigration.Migration.Services.ContractNameResolver"/> for the full precedence.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? ElectricityChargerContractMap { get; set; }
    public DateTime? IncludeEndedCutoff { get; set; }

    /// <summary>
    /// Start of the contract-part restore window (inclusive). Set together with
    /// <see cref="RestorePartsChangedBefore"/>; see that property for the whole story.
    /// </summary>
    public DateTime? RestorePartsChangedFrom { get; set; }

    /// <summary>
    /// End of the contract-part restore window (exclusive). Off by default — both null means the
    /// extraction behaves exactly as it always has, and nothing is read from
    /// <c>EXU_CONTRACTPARTLOG</c>. Setting only one of the two bounds is a configuration error and
    /// throws when the window is read.
    ///
    /// <para><b>Opt-in bridge for the 2026-08-31 incident.</b> An unattended Xellent batch ran that
    /// morning and changed 51.363 El-ekstern contract parts in dataarea <c>hol</c> across roughly
    /// 44.600 accounts: 49.261 got an <c>Enddate</c> written (typically open-ended → 2026-08-31) and
    /// 2.097 future parts were deleted outright. A <c>create</c> run therefore sees contracts that
    /// are already over — it either migrates a contract link that is dead on the change-of-supplier
    /// date, or the account is refused at the missing-contract guard and nothing lands for it. With
    /// the window set, the pre-batch state is read back out of the log at extraction time: the
    /// overwritten end dates are reverted, the deleted parts are added back with their metering
    /// point resolved through the agreement, and the ordinary part-date rule is then re-applied, so
    /// a part whose real end date was already in the past still drops out.</para>
    ///
    /// <para>The window for that incident is
    /// <c>[2026-08-31 09:13:49.000, 2026-08-31 09:59:33.000)</c> — first logged row to one tick past
    /// the last. It stays bounded and caller-supplied on purpose: an open-ended "everything since"
    /// would also undo a legitimate change made later the same day.</para>
    ///
    /// <para><b>This is a bridge, not a repair.</b> The data in Xellent stays wrong while the option
    /// is on; only what the extraction sees is corrected. Xellent must be corrected at source, and
    /// this option removed once that has happened.</para>
    ///
    /// <para>Feeds <c>MapperOptions.RestorePartsChangedFrom/Before</c>, which widens the
    /// <b>part-level</b> end-date cutoff only. The header-level <see cref="IncludeEndedCutoff"/> is
    /// untouched — widening that would drag genuinely terminated contracts into the run and create
    /// real Settl customers for them.</para>
    /// </summary>
    public DateTime? RestorePartsChangedBefore { get; set; }

    /// <summary>Dry-run only: process only the first N representative customers (post-merge,
    /// deterministic by AccountNumber). Full Xellent extract still runs — slice is applied
    /// inside <see cref="CreateRunner.RunAsync"/> after merge logic.</summary>
    public int? AccountLimit { get; set; }

    /// <summary>Live-run only: process only these specific representative AccountNumbers.
    /// Members of merge-groups follow automatically since each <c>Customer</c> already
    /// represents the entire merge cluster.</summary>
    public string[]? SelectedAccounts { get; set; }

    /// <summary>Optional allow-list af Xellent Meterpointtypecode. Tom/null = alle (kun D14*-eksklusion gælder).</summary>
    public string[]? MeterPointTypeCodes { get; set; }
    public required DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }

    /// <summary>Slår CVR+fingerprint-merge fra: hver Xellent-konto bliver sin egen Settl-kunde.
    /// Til whole-category-kørsler der migrerer kontosættet i uafhængige batches — en merge-gruppe
    /// delt over to batches giver to Settl-kunder alligevel, så en per-batch-merge gør bare
    /// resultatet afhængigt af batch-inddelingen. Profilbaserede kørsler lader den stå false.
    /// Slår <see cref="LegacyTg4Merge"/>.</summary>
    public bool DisableRepresentativeMerge { get; set; }

    /// <summary>TechnicGroup4-værdier der hverken må merge eller danne OwnerCluster.
    /// Konti i disse grupper migreres standalone. Tom/null = ingen.</summary>
    public string[]? IgnoredGroups { get; set; }

    /// <summary>Ejer-klynger for hele kørslens scope, fundet på forhånd med
    /// <see cref="DataMigration.Migration.Services.OwnerClusterSelector"/>. Sat af batchede
    /// kørsler: en batch der selv udleder klyngerne kan kun se sine egne konti, så en klynge
    /// spredt over mange batches blev mødt som en ny klynge hver gang og fik sin egen ejer-kunde
    /// hver gang. Null = kørslen udleder klyngerne af sit eget sæt som hidtil.</summary>
    public List<DataMigration.Migration.Services.Extraction.OwnerCluster>? OwnerClusters { get; set; }

    /// <summary>Ejer-kunder og nummertildeling delt på tværs af kørslens batches. Sættes sammen
    /// med <see cref="OwnerClusters"/>: den første batch der møder en klynge opretter ejeren og
    /// skriver den her, resten linker deres medlemmer til den samme. Null = hver kørsel står
    /// alene, som en profilbaseret kørsel gør.</summary>
    public DataMigration.Migration.Services.OwnerRunState? OwnerState { get; set; }

    /// <summary>Target Settl environment ("production", "featuretest", "production-hev", "production-vhel" eller null).
    /// CreateRunner bruger <see cref="DataMigration.Migration.Services.CustomerNumberPolicy.PadForEnv"/> til at normalisere
    /// CustomerNumber baseret på env'et. Callere behøver ikke selv kende padding-reglen.</summary>
    public string? Env { get; set; }

    /// <summary>ProductElementId (Settl Guid) of the element to fully exempt for matching contracts. Null disables.</summary>
    public Guid? BillingExemptionProductElementId { get; set; }

    /// <summary>ProductElementCode label used in log lines (e.g. "Fakturagebyr").</summary>
    public string? BillingExemptionProductElementCode { get; set; }

    /// <summary>Suffix for ContractTemplate that triggers the exemption (e.g. "_Erh_UG"). Null/empty disables.</summary>
    public string? BillingExemptionTemplateSuffix { get; set; }

    /// <summary>Fraction to exempt as a Settl decimal between 0 and 1 (default 1 = 100%).</summary>
    public double BillingExemptionPercentage { get; set; } = 1;

    /// <summary>When true, per-customer migration failures log a warning and the run continues with the next customer instead of aborting.</summary>
    public bool ContinueOnError { get; set; }

    /// <summary>
    /// BillingPlanNum → Settlement Group name. Null disables the AssignSettlementGroups step entirely;
    /// empty dictionary fails any bundle whose MPs have a BillingPlanNum (= unmapped).
    /// </summary>
    public Dictionary<string, string>? SettlementGroupMap { get; set; }

}

public class CreateRunner
{
    private readonly SettlApiOptions _apiOptions;
    // Non-readonly: offline mode (see RunAsync) swaps these for no-op mocks so the entire
    // pipeline — pre-loop resolvers and per-customer loop alike — runs without any Settl calls.
    private ISettlCustomerClient _customerClient;
    private ISettlPayerClient _payerClient;
    private ISettlMeteringPointClient _meteringPointClient;
    private ISettlContractClient _contractClient;
    private ISettlSettlementGroupClient _settlementGroupClient;
    private readonly IWarningCollector _warnings;
    private readonly List<string> _log = [];
    private readonly StringBuilder _lineBuffer = new();
    private Action<string>? _onLogLine;

    /// <summary>Optional callback invoked for each log line (for live streaming to dashboard).</summary>
    public Action<string>? OnLogLine { set => _onLogLine = value; }

    /// <summary>
    /// Optional callback invoked as work settles: (done, total). <b>Enheden følger fasen</b> —
    /// <see cref="MigrationPhases.CreatingMeteringPoints"/> tæller målepunkter,
    /// <see cref="MigrationPhases.MigratingCustomers"/> tæller kunder. En viser der lægger
    /// tallene sammen på tværs af faser, lægger æbler til pærer; læs
    /// <see cref="OnPhase"/> først.
    /// </summary>
    public Action<int, int>? OnProgress { get; set; }

    /// <summary>
    /// Optional callback invoked as each customer finishes, before the next one starts —
    /// including the customer that stops the run when <see cref="CreateOptions.ContinueOnError"/>
    /// is off. Lets a caller journal per-account progress while the run is still in flight,
    /// which is the only way to know how far an interrupted run got: the report carries
    /// counters, not account-level outcomes, and a cancelled run returns no report at all.
    /// Not invoked in capture-mode (reconcile), where nothing is written to Settl.
    /// </summary>
    public Action<CustomerOutcome>? OnCustomerCompleted { get; set; }

    /// <summary>Optional callback invoked once domain loading + contract resolution finishes (before migrate-loop).
    /// Receives the in-progress MigrationReport with CollectorErrors/CollectorWarnings populated from _warnings.
    /// The report reference is the same one returned by RunAsync — subsequent mutations will be visible to callers.</summary>
    public Action<MigrationReport>? OnExtractionComplete { get; set; }

    /// <summary>Optional callback invoked when the runner enters a new pipeline phase — se
    /// <see cref="MigrationPhases"/> for navnene og rækkefølgen. Pass-through for the dashboard's
    /// phase indicator. Called with null at completion, og en fase meldes kun ind når kørslen
    /// faktisk laver den.</summary>
    public Action<string?>? OnPhase { get; set; }

    /// <summary>Kaldes når kørslens fælles Settl-kontekst er bygget, lige før kunderne behandles.
    /// Findes så en test kan se hvad valgene fra <see cref="CreateOptions"/> faktisk endte som i
    /// konteksten — det er den eneste vej, konteksten går ikke videre til nogen af klienterne.</summary>
    internal Action<SettlMigrationContext>? OnMigrationContextBuilt { get; set; }

    public CreateRunner(
        SettlApiOptions apiOptions,
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient meteringPointClient,
        ISettlContractClient contractClient,
        ISettlSettlementGroupClient settlementGroupClient,
        IWarningCollector? warnings = null)
    {
        _apiOptions = apiOptions;
        _customerClient = customerClient;
        _payerClient = payerClient;
        _meteringPointClient = meteringPointClient;
        _contractClient = contractClient;
        _settlementGroupClient = settlementGroupClient;
        _warnings = warnings ?? new WarningCollector();
    }

    /// <param name="loadResult">Pre-loaded domain data from <c>XellentDomainMapper.LoadAsync</c>.
    /// The caller is responsible for DB connectivity verification and mapper construction.</param>
    /// <param name="capturePlans">
    /// When non-null, the runner operates in <b>capture-mode</b> (used by the read-only
    /// <c>reconcile</c> command): the entire prep — extraction, merge/representative, owner
    /// resolution, contract-link resolution, exemptions, legacy-id maps, pad-9 — and the
    /// per-customer payload building run <b>identically to create</b>, but each customer's
    /// intended payload is captured via <see cref="SettlClientImpl.CapturePlanAsync"/> into this
    /// list instead of being POSTed via MigrateAsync. No writes, no create-aggregation. This
    /// shared path is what guarantees reconcile cannot drift from create. Pass
    /// <c>options.DryRun=true</c> so the owner-resolution prep stays read-only too.
    /// </param>
    public async Task<(int ExitCode, MigrationReport Report)> RunAsync(
        CreateOptions options,
        XellentLoadResult loadResult,
        CancellationToken cancellationToken = default,
        List<SettlMigrationPlan>? capturePlans = null)
    {
        // En målepunkts-kørsel skriver, eller den gør ingenting: målepunkts-fasen springes helt
        // over i dry-run, offline og capture-tilstand. Afvises frem for at køre og lade som om,
        // for en kørsel der melder "0 målepunkter oprettet" ligner en der ikke havde noget at lave.
        if (options.MeteringPointsOnly && (options.DryRun || options.Offline || capturePlans != null))
        {
            var why = capturePlans != null ? "reconcile" : options.Offline ? "--offline" : "--dry-run";
            var message = $"--mp-only kan ikke kombineres med {why}: målepunkter oprettes kun i en "
                        + "levende kørsel, så kombinationen ville ikke lave noget.";
            LogLine($"ERROR: {message}");
            var rejected = new MigrationReport();
            rejected.Errors.Add(message);
            rejected.Log = [.. _log];
            return (1, rejected);
        }

        LogLine(options.LoadEntireDataArea
            ? "=== Settl API Migration (hele leveringskategorien) ==="
            : $"=== Settl API Migration ({options.Accounts.Length} accounts) ===");
        LogLine();

        var report = new MigrationReport();

        // === Offline mode: swap every Settl client for a no-op mock ===
        // One swap makes the entire pipeline offline — pre-loop resolvers (owner/allocator) and
        // the per-customer loop alike. All searches return not-found, so every entity reports as
        // "would create". Contract-GUID resolution and settlement-group validation are skipped
        // below (empty mocks would otherwise emit false errors); the per-customer settlement-group
        // assignment is disabled by nulling SettlementGroupMap in the context. Offline implies dry-run.
        if (options.Offline)
        {
            options.DryRun = true;
            _customerClient = new MockCustomerClient();
            _payerClient = new MockPayerClient();
            _meteringPointClient = new MockMeteringPointClient();
            _contractClient = new MockContractClient();
            _settlementGroupClient = new MockSettlementGroupClient();
            LogLine("OFFLINE MODE: all Settl API calls skipped — every entity reported as 'would create'.");
            LogLine();
        }

        LogLine($"Target API: {(options.Offline ? "(offline — no Settl calls)" : _apiOptions.BaseUrl)}");
        LogLine($"Dry Run: {options.DryRun}");
        // Spelled out on every run: the two DAR switches decide whether a mismatched address is
        // rejected or lands anyway, and that has to be readable from the log afterwards.
        LogLine($"DAR address: enrich={(options.EnrichAddress ? "on" : "off")}, "
              + $"validation={(options.SkipAddressValidation ? "skipped" : "enforced")}");
        LogLine(options.LoadEntireDataArea
            ? $"Accounts: hele {options.DataAreaId}/{options.DeliveryCategory}"
            : $"Accounts: {options.Accounts.Length}");
        if (!options.LoadEntireDataArea && options.Accounts.Length <= 10)
            LogLine($"  [{string.Join(", ", options.Accounts)}]");
        LogLine();

        // Optional slice: dry-run-limit eller live-run-selection. Anvendes EFTER fuld extract
        // så CVR+Fingerprint merge / OwnerClusters / JointInvoiceGroups regnes på fulde sæt.
        loadResult = ApplyCustomerFilter(loadResult, options, report);

        // A non-empty selection that matches nothing is never a legitimate no-op — it means the
        // caller's account list is stale (or wrongly padded). Without this the run would report
        // success having migrated zero customers, which reads exactly like a completed resume.
        if (options.SelectedAccounts is { Length: > 0 } && loadResult.Customers.Count == 0)
        {
            var message =
                $"SelectedAccounts matchede ingen af de {report.MissingSelectedAccounts.Count} angivne konti i extractet — " +
                "kontosættet er sandsynligvis forældet i forhold til Xellent. Ingen kunder blev behandlet.";
            LogLine($"ERROR: {message}");
            report.Errors.Add(message);
            report.Log = [.. _log];
            OnPhase?.Invoke(null);
            return (1, report);
        }

        // Kunder uden både CVR og CPR tages ud her, før noget andet rører extractet: uden
        // identifikation kan de ikke oprettes i Settl, og så skal de heller ikke tælles med
        // eller vises som om de blev det. Rapporten viser dem i deres egen sektion.
        loadResult = ApplyMissingIdentityFilter(loadResult, report, options);

        // === Bulk read-cache: front the per-entity Settl existence-searches with a few up-front
        // paginated bulk-loads served from memory. One round-trip per customer/payer/MP search is
        // replaced by a handful of page calls (large dry-run win); link-GETs on fabricated dry-run
        // / just-created Guids are short-circuited. Live misses fall through to a real search so a
        // stale snapshot can never cause a duplicate create. Disabled offline (mocks already make
        // zero calls) and via BulkCacheReads=false. Must wrap before owner resolution (uses
        // _customerClient) so every downstream consumer sees the cache.
        BulkReadCacheStats? bulkStats = null;
        if (!options.Offline && options.BulkCacheReads)
        {
            // Water/heat MPs use a separate search endpoint that isn't cached — only preload the
            // electricity (market v3) snapshot when the extract actually has electricity MPs.
            var hasElectricityMps = loadResult.MeteringPoints.Any(mp => mp.Grid != null);

            // En målepunkts-kørsel slår aldrig en kunde eller en betaler op: ejer-opslaget springes
            // over for den, og den stopper før kunde-løkken. Så er de to snapshots ren ventetid —
            // og mod production er det hele miljøet, hver gang der trykkes på knappen. Klienterne
            // gives tilbage uindpakkede, så et opslag der alligevel skulle komme, går til Settl og
            // ikke til et tomt snapshot der ville svare "findes ikke".
            var preloadCustomersAndPayers = !options.MeteringPointsOnly;

            OnPhase?.Invoke(MigrationPhases.PreloadingSettlCache);
            LogLine("--- Settl bulk read-cache preload ---");
            var cache = await SettlBulkReadCache.CreateAsync(
                _customerClient, _payerClient, _meteringPointClient,
                options.DryRun, options.DeliveryCategory, hasElectricityMps, LogLine, cancellationToken,
                preloadCustomers: preloadCustomersAndPayers,
                preloadPayers: preloadCustomersAndPayers);

            bulkStats = cache.Stats;
            _customerClient = cache.CustomerClient;
            _payerClient = cache.PayerClient;
            _meteringPointClient = cache.MeteringPointClient;

            // "0 kunder" og "ikke hentet" er to forskellige ting, og kun den ene er en tom Settl.
            static string Loaded(bool preloaded, int count, string entity)
                => preloaded ? $"{count} {entity}" : $"{entity}: ikke hentet";
            LogLine("  bulk-loaded "
                  + $"{Loaded(preloadCustomersAndPayers, bulkStats.CustomersLoaded, "customers")} / "
                  + $"{Loaded(preloadCustomersAndPayers, bulkStats.PayersLoaded, "payers")} / "
                  + $"{Loaded(hasElectricityMps, bulkStats.MpsLoaded, "MPs")}");
            LogLine();
        }

        // SettlMappingService wraps loadResult for grouping, mapping and report support queries.
        var extractionService = new SettlMappingService(
            loadResult,
            _warnings,
            options.ReferenceDate,
            options.DeliveryCategory);

        // Råstoffet bag de to rapport-afsnit nedenfor, båret med ud så en batched kørsel kan
        // lægge sine batches sammen og regne dem én gang over hele scopet. Uden sammenlægning
        // er der intet at regne — hverken her eller i dashboardets akkumulering — så det bæres
        // heller ikke med: én post per konto per batch er ren vægt i en rapport der i forvejen
        // fylder megabytes på 45.000 konti.
        if (!options.DisableRepresentativeMerge)
        {
            report.Fingerprints = new Dictionary<string, AccountFingerprint>(
                loadResult.FingerprintByRepresentative, StringComparer.Ordinal);
            report.CustomerEmailFacts = [.. loadResult.Customers.Select(c => new CustomerEmailFacts(
                AccountNumber: c.AccountNumber,
                Cvr: c.Cvr,
                Name: c.Name,
                AdditionalName: c.AdditionalName,
                StreetName: c.Address.StreetName,
                PostalCode: c.Address.PostalCode,
                City: c.Address.City,
                Email: c.Contact.Email))];
        }

        // Begge afsnit handler om hvad sammenlægningen gør: "de her konti bliver til hver sin
        // Settl-kunde fordi fingeraftrykket varierer". Er sammenlægningen slået fra, bliver hver
        // konto sin egen kunde uanset fingeraftryk — så påstanden er falsk, og email-advarslens
        // handlingsanvisning ("ensret mailen i Xellent, så bliver de én kunde") virker ikke.
        // Springes over frem for at skjules i visningen: på 45.000 konti er det også arbejde
        // brugt på et spørgsmål ingen har stillet.
        report.RepresentativeMergeDisabled = options.DisableRepresentativeMerge;
        if (!options.DisableRepresentativeMerge)
        {
            report.FingerprintSplits = loadResult.FingerprintSplitsByCvr().ToList();
            if (report.FingerprintSplits.Count > 0)
            {
                LogLine("--- Fingerprint splits ---");
                foreach (var split in report.FingerprintSplits)
                {
                    LogLine($"  CVR {split.Cvr}: {split.VariationCount} variationer ({split.Variations.Sum(v => v.Accountnums.Count)} kunder)");
                    foreach (var v in split.Variations)
                        LogLine($"    [{v.Accountnums.Count}] {v.Name} | {v.Street} | {v.PostalCode} {v.City} :: {string.Join(", ", v.Accountnums)}");
                }
                LogLine();
            }

            new CustomerValidationRunner([])
                .RunBatchAndCollect([new EmailOnlyFingerprintSplitValidator()], loadResult.Customers, _warnings);
        }

        // Extract account groupings (needed for ContractNameResolver, CustomerOwnerResolver, report)
        report.AccountGroupings = await extractionService.ExtractAccountGroupingsAsync(options.Accounts);

        // Extract account-MP mapping (needed for ContractNameResolver and report fields)
        var accountMpMapping = await extractionService.ExtractAccountMeteringPointMappingAsync(options.Accounts);
        report.AccountMeteringPoints = accountMpMapping.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(mp => mp.MeteringPointId).Distinct().ToList());
        report.JoinedMeteringPoints = accountMpMapping.Values
            .SelectMany(v => v.Where(mp => mp.IsJoined).Select(mp => mp.MeteringPointId))
            .ToHashSet();
        report.ContractMeteringPoints = accountMpMapping.Values
            .SelectMany(v => v)
            .GroupBy(mp => mp.ContractNum)
            .ToDictionary(g => g.Key, g => g.Select(mp => mp.MeteringPointId).Distinct().ToList());

        // Store ProductNum + PaymentTerm per MP from extraction tuple (dashboard display).
        // PriceArea is NOT stored separately — it's read directly from
        // MeteringPointDto.Grid.MarketBalanceAreaName ("DK1"/"DK2") in DryRunResultDto.FromReport.
        foreach (var mps in accountMpMapping.Values)
            foreach (var mp in mps)
            {
                report.MpProductNums.TryAdd(mp.MeteringPointId, mp.ProductNum);
                report.MpPaymentTerms.TryAdd(mp.MeteringPointId, mp.PaymentTerm);

                // Price-list window per contract part (name + start/end), resolved CSV-free from the
                // rate code. An MP can appear once per contract part, so collect distinct windows.
                if (!string.IsNullOrEmpty(mp.AmrCode))
                {
                    var entry = new MpPriceListInfo(
                        PriceListNameResolver.TranslateAmrcode(mp.AmrCode),
                        mp.ContractPartStartdate,
                        mp.ContractPartEnddate == MigrationConstants.EndDateNull ? null : mp.ContractPartEnddate);
                    if (!report.MpPriceLists.TryGetValue(mp.MeteringPointId, out var windows))
                        report.MpPriceLists[mp.MeteringPointId] = windows = [];
                    if (!windows.Contains(entry))
                        windows.Add(entry);
                }
            }

        // Store per-MP e-faktura-reference overrides (dashboard display + variation tag).
        // BuyerInvoicing is non-null only when a field diverges across the payer's contracts
        // and is pushed down to this MP (see XellentDomainMapper.ApplyMpBillingOverrides).
        foreach (var mp in loadResult.MeteringPoints)
        {
            if (mp.BuyerInvoicing is not { } refs) continue;
            report.MpBillingOverrides.TryAdd(mp.Identification, new MpBillingOverrideInfo(
                refs.OrderIdentifier,
                refs.AgreementIdentifier,
                refs.BuyerReferenceIdentifier));
        }

        // Bær ladestanderne videre til rapporten, som mapperen efterlod dem: dem der kunne knyttes
        // til et el-målepunkt, og dem der ikke kunne. Ingen af dem migreres — pointen er at kunne se
        // før kørslen at el-kontrakten lukkes i Xellent mens ladestanderen bliver stående.
        report.EvChargersByMeteringPoint = loadResult.EvChargersByMeteringPoint
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        report.EvChargersByAccount = loadResult.EvChargersByAccount
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);

        // Store per-MP billing-exemption decision (dashboard display). The mapper marks an MP with
        // the matched contract-template (mp.BillingExemptionTemplate) when it qualifies; code +
        // percentage come from the profile config. Same decision SettlClient applies to Settl.
        if (!string.IsNullOrWhiteSpace(options.BillingExemptionTemplateSuffix))
        {
            var exemptionCode = options.BillingExemptionProductElementCode
                ?? options.BillingExemptionProductElementId?.ToString()
                ?? "";
            foreach (var mp in loadResult.MeteringPoints)
            {
                if (mp.BillingExemptionTemplate is not { } matchedTemplate) continue;
                report.MpBillingExemptions.TryAdd(mp.Identification, new MpBillingExemptionInfo(
                    exemptionCode,
                    matchedTemplate,
                    options.BillingExemptionPercentage));
            }
        }


        // Build customer DTOs for owner-resolver input and dashboard report.
        // These DTOs are NOT used for Settl API calls — SettlClient builds DTOs internally.
        var customerDtos = loadResult.Customers.Select(CustomerTranslator.Build).ToList();

        // Apply env-specific CustomerNumber normalisation (pad-9 til præcis 6 cifre i production
        // — null = no-op). Skal ske før owner-mapping bygges, så ownerByCustomerNumber er
        // keyed på samme form som SettlClient bruger til søgning og create.
        foreach (var dto in customerDtos)
        {
            if (string.IsNullOrEmpty(dto.CustomerNumber)) continue;
            var padded = CustomerNumberPolicy.PadForEnv(dto.CustomerNumber, options.Env, out var wasPadded);
            if (wasPadded)
            {
                var msg = $"CustomerNumber {dto.CustomerNumber} padded to {padded} for target environment";
                LogLine($"  INFO: {msg}");
                _warnings.AddInfo(msg);
            }
            dto.CustomerNumber = padded;
        }

        report.Customers = customerDtos;
        report.CustomersExtracted = customerDtos.Count;

        // Payer DTOs for report display. CustomerId must match the Id on the corresponding customer DTO
        // so test/report joins (payer.CustomerId == customer.Id) resolve correctly.
        var payersByAccount = loadResult.Customers
            .Zip(customerDtos, (c, dto) => (
                c.AccountNumber,
                Payers: c.Payers.Select(p => PayerTranslator.Build(c, p, dto.Id)).ToList()))
            .ToList();
        report.Payers = payersByAccount.SelectMany(x => x.Payers).ToList();
        report.PayersExtracted = report.Payers.Count;

        // Per-customer payer billing summary (display), keyed on RAW accountnum so the account
        // tree (raw representativeAccount) can look it up directly — same keying as
        // CustomerSettlementGroups. Values are the final translated Settl fields.
        foreach (var (acct, payerDtos) in payersByAccount)
        {
            if (payerDtos.Count == 0) continue;
            report.CustomerPayerBilling[acct] = payerDtos.Select(p => new CustomerPayerBillingInfo(
                p.PayerInformation?.PayerIdentifier,
                p.PayerBilling?.ElectronicNumber,
                p.PayerBilling?.InvoiceChannel,
                p.PayerBilling?.PaymentChannel,
                p.PayerBilling?.PayerType,
                p.PayerBilling?.BuyerReferenceIdentifier,
                p.PayerBilling?.AgreementIdentifier,
                p.PayerBilling?.OrderIdentifier,
                p.PayerBilling?.BookkeepingReference)).ToList();
        }

        // MPs — not built as DTOs upfront; created inside SettlClient per customer.
        report.MeteringPointsExtracted = loadResult.MeteringPoints.Count;

        // === Owner resolution (operates on DTO list, sets .Owner before migration) ===
        // Springes over i en målepunkts-kørsel: klynge-opløsningen OPRETTER rigtige kunder i Settl
        // (ejer-kundenumre fra 900000), og den ligger før målepunkts-fasen. Uden det her ville en
        // kørsel der lover "kun målepunkter" alligevel have oprettet kunder. Genoptagelsen laver
        // dem, og det er den rigtige rækkefølge alligevel — ejeren skal findes før medlemmerne
        // kan kobles til den.
        var ownerResolver = new CustomerOwnerResolver(_customerClient, _warnings, LogLine);
        if (!options.MeteringPointsOnly)
            await ownerResolver.ApplyAsync(customerDtos, report.AccountGroupings, options, report.ExternalOwners, cancellationToken);

        // Udleverede klynger vinder: de er fundet på hele kørslens scope, hvor de klynger vi selv
        // kunne udlede kun ville dække de konti denne batch tilfældigvis holder.
        var ownerClusters = options.MeteringPointsOnly
            ? []
            : options.OwnerClusters
                ?? extractionService.BuildOwnerClusters(report.AccountGroupings, options.IgnoredGroups);
        if (ownerClusters.Count > 0)
        {
            LogLine("--- CVR+TG4 owners ---");
            var clusterResolver = new OwnerClusterResolver(_customerClient, _warnings, LogLine, options.OwnerState);
            await clusterResolver.ApplyAsync(customerDtos, ownerClusters, report.ExternalOwners, report.OwnerCustomers, options.DryRun, cancellationToken, report.SkippedOwnerClusters);
        }

        // Build owner-by-customer-number mapping for SettlClient context.
        var ownerByCustomerNumber = customerDtos
            .Where(c => c.CustomerNumber != null && c.Owner != null)
            .ToDictionary(c => c.CustomerNumber!, c => c.Owner!.OwnerId);

        // === Contract Name Resolution ===
        // Offline: resolve the EXPECTED contract names locally (no Settl) so the report still shows
        // them. Only the Settl GUID lookup (LoadContractsAsync) is skipped — it would emit a
        // collector-error per contract against the empty mock. ContractsByName stays empty, so
        // BuildMpContractLinks produces no links (no contract-link mutation attempted offline).
        // Whether the customer is a company decides the contract on the templates that hold both
        // kinds. Read off the already-mapped customer — Cvr is null unless it validated as 8 digits
        // — so the rule is the mapper's, not a second one, and no extra Xellent trip is needed.
        var identityByAccount = loadResult.Customers.ToDictionary(
            c => c.AccountNumber,
            c => !string.IsNullOrEmpty(c.Cvr) ? CustomerIdentity.Cvr
                : !string.IsNullOrEmpty(c.Cpr) ? CustomerIdentity.Cpr
                : CustomerIdentity.Blank,
            StringComparer.Ordinal);

        ContractNameResolver.Result contractResolution;
        if (options.Offline)
        {
            var contractResolver = new ContractNameResolver(_contractClient, _warnings, LogLine);
            contractResolution = contractResolver.ResolveLocal(
                options, report.AccountGroupings, accountMpMapping, identityByAccount,
                loadResult.EvChargersByMeteringPoint, loadResult.EvChargersByAccount);
        }
        else
        {
            OnPhase?.Invoke(MigrationPhases.ResolvingContracts);
            var contractResolver = new ContractNameResolver(_contractClient, _warnings, LogLine);
            contractResolution = await contractResolver.ResolveAsync(
                options, report.AccountGroupings, accountMpMapping, cancellationToken, identityByAccount,
                loadResult.EvChargersByMeteringPoint, loadResult.EvChargersByAccount);
        }
        report.MpContractNames = contractResolution.MpContractNames;
        // Which rule mapped the contract, for the metering points a template rule moved. The
        // contract itself is already in MpContractNames/MpContractLinks and shows in the report;
        // this is what makes two metering points on the same product with different contracts read
        // as a decision instead of an inconsistency.
        report.MpContractRules = contractResolution.MpContractRules;
        report.MpChargerContractRules = contractResolution.MpChargerContractRules;
        // Non-electricity profiles (water, heat) send OpenEnded=true to Settl (End=null).
        // Mask ContractPartEnddate in the report so the display matches what Settl actually receives.
        var openEndedContractLinks = options.DeliveryCategory != MigrationConstants.DeliveryCategoryElectricity;
        report.MpContractLinks = openEndedContractLinks
            ? contractResolution.Links.Select(l => l with { ContractPartEnddate = MigrationConstants.EndDateNull }).ToList()
            : contractResolution.Links;

        // === Expected settlement group per customer (display) ===
        // Local mapping: each customer's contract billing-plan codes → Settl Settlement Group name(s)
        // via the profile's SettlementGroupMap. Matches the granularity of the actual assignment
        // (the customer — not the MP — is added to the group). Resolved without Settl so the EXPECTED
        // group is visible in (offline) dry-run too — the live assignment counters only fill on a hot run.
        report.CustomerSettlementGroups = options.SettlementGroupMap is { Count: > 0 }
            ? SettlementGroupExpectationResolver.ResolveByCustomer(loadResult.Customers, options.SettlementGroupMap)
            : [];

        // === Settlement Group Preflight ===
        // Validate every mapped settlement group exists in Settl BEFORE we start mutating
        // customers/MPs. Without this, missing-group errors surface mid-loop with several
        // customers already created — manual cleanup required to retry.
        // Skipped offline: ListAllAsync needs Settl; the empty mock would flag every mapped group
        // as MISSING (collector-error). Per-customer group assignment is disabled via the context below.
        if (!options.Offline && options.SettlementGroupMap is { Count: > 0 })
        {
            await ValidateSettlementGroupsAsync(options.SettlementGroupMap, cancellationToken);
        }

        // Pre-resolve to SettlMpContractLink list for SettlClient (Guid-keyed, deduped by (mpId, contractId)).
        var mpContractLinks = BuildMpContractLinks(contractResolution, openEndedContractLinks);

        LogLine();

        // Per-MP data for SettlClient (use case 2) is pre-loaded in XellentLoadResult — no extra
        // DB roundtrip needed. Billing-exemption qualification now lives on the MP itself
        // (mp.BillingExemptionTemplate, set by XellentDomainMapper from the configured suffix).
        var mpOrderIdentifiers = loadResult.MpOrderIdentifiers;

        // === Build billing-exemption context ===
        // Carries only the Settl-side application (ProductElementId @ %); the qualify-decision is the
        // mapper's (driven by options.BillingExemptionTemplateSuffix → MapperOptions). Built only when
        // both the product element and the suffix are configured.
        SettlBillingExemptionContext? settlExemptionContext = null;
        if (options.BillingExemptionProductElementId is { } peId
            && !string.IsNullOrWhiteSpace(options.BillingExemptionTemplateSuffix))
        {
            settlExemptionContext = new SettlBillingExemptionContext(
                peId,
                options.BillingExemptionProductElementCode ?? peId.ToString(),
                options.BillingExemptionPercentage);
            LogLine($"BillingExemption ProductElement '{settlExemptionContext.ProductElementCode}' = {peId}");
            LogLine();
        }

        // === Build legacy-payer-identifier map (pad-9 → original) ===
        // Pad-9-reglen blev indført efter at nogle production-payers var oprettet i Settl med
        // deres rå un-paddede identifier. SettlClient bruger map'et som fallback: hvis primær
        // lookup på padded form misser, prøves un-padded alias før vi konkluderer "ny payer".
        // Inkluderer både payer-identifiers og customer-accountnumbers (sidstnævnte fordi
        // SettlClient falder tilbage til CustomerNumber som default payer-id når payer.Identifier
        // er tom — så legacy-fallback skal også dække den case).
        var legacyPayerIdentifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in loadResult.Customers)
        {
            var paddedAcc = CustomerNumberPolicy.PadForEnv(c.AccountNumber, options.Env, out var accPadded);
            if (accPadded) legacyPayerIdentifiers[paddedAcc] = c.AccountNumber;

            foreach (var p in c.Payers)
            {
                if (string.IsNullOrEmpty(p.Identifier)) continue;
                var multiSuffix = MultiPayerSuffix(p.Identifier, c.AccountNumber);
                if (multiSuffix != null)
                {
                    // Multi-payer {AccountNumber}-{n}: legacy entry uses padded AccountNumber part.
                    if (accPadded) legacyPayerIdentifiers[$"{paddedAcc}-{multiSuffix}"] = p.Identifier;
                    continue;
                }
                var paddedId = CustomerNumberPolicy.PadForEnv(p.Identifier, options.Env, out var idPadded);
                if (idPadded) legacyPayerIdentifiers[paddedId] = p.Identifier;
            }
        }

        // === Build migration context (shared across all per-customer calls) ===
        var migrationContext = new SettlMigrationContext(
            DryRun: options.DryRun,
            ChangeOfSupplierDate: options.ReferenceDate,
            MpContractLinks: mpContractLinks,
            ExemptionContext: settlExemptionContext,
            // Offline: drop the settlement-group map so per-customer AssignSettlementGroups doesn't
            // run (FindByNameAsync against the empty mock would add a "not found" error per group).
            SettlementGroupMap: options.Offline ? null : options.SettlementGroupMap,
            OwnerByCustomerNumber: ownerByCustomerNumber.Count > 0 ? ownerByCustomerNumber : null,
            MpOrderIdentifiers: mpOrderIdentifiers,
            LegacyPayerIdentifiers: legacyPayerIdentifiers.Count > 0 ? legacyPayerIdentifiers : null,
            Offline: options.Offline,
            EnrichAddress: options.EnrichAddress,
            SkipAddressValidation: options.SkipAddressValidation,
            MeteringPointCreateBatchSize: options.MeteringPointCreateBatchSize,
            SearchConcurrency: options.SearchConcurrency,
            // Tilbagelæsningen findes kun for at lære målepunkternes Settl-id'er, og de bruges
            // først af koblinger, faktureringsopsætning og fritagelser. En målepunkts-kørsel
            // stopper før alt det og smider id'erne væk igen — så er søgningerne og pauserne
            // mellem dem betalt for ingenting.
            ResolveCreatedMeteringPointIds: !options.MeteringPointsOnly);

        OnMigrationContextBuilt?.Invoke(migrationContext);

        // Build MP lookup for SettlClient (identification → Domain MeteringPoint)
        var mpsByIdentification = loadResult.MeteringPoints
            .Where(mp => !string.IsNullOrEmpty(mp.Identification))
            .ToDictionary(mp => mp.Identification);

        // Build MP DTOs for report (dedup: same MP can appear in multiple contract parts).
        var mpDtoStart = DateOnly.FromDateTime(options.ReferenceDate);
        var seenMpIds = new HashSet<string>();
        var mpDtos = new List<MeteringPointDto>();
        foreach (var customer in loadResult.Customers)
        {
            foreach (var contract in customer.Contracts)
            {
                foreach (var assignment in contract.MeteringPoints)
                {
                    if (!seenMpIds.Add(assignment.MeteringPointId)) continue;
                    if (!mpsByIdentification.TryGetValue(assignment.MeteringPointId, out var mp)) continue;
                    mpDtos.Add(MeteringPointTranslator.Build(mp, customer));
                }
            }
        }
        report.MeteringPoints = mpDtos;

        // Snapshot extraction-phase warnings/errors into report so callers (e.g. dashboard via
        // OnExtractionComplete) can render them before the migrate-loop runs. WriteAndFinalize
        // overwrites these with the final values at end of run.
        if (OnExtractionComplete != null)
        {
            report.CollectorErrors = _warnings.GetErrors().ToList();
            report.CollectorWarnings = _warnings.GetWarnings().ToList();
            OnExtractionComplete(report);
        }

        // === Per-customer migration pipeline (via SettlClient) ===
        var settlClient = new SettlClientImpl(
            _customerClient, _payerClient, _meteringPointClient, _contractClient, _settlementGroupClient,
            options.DeliveryCategory, LogLine);

        var orderedCustomers = loadResult.Customers.OrderBy(c => c.AccountNumber).ToList();

        // === Accounts whose contract is not in Settl ===
        // These accounts are NOT taken out of the run. They go through the customer loop like
        // everybody else — customer, payer, metering points, billing and the customer/payer links
        // are all created — and only the contract link is left out, because BuildMpContractLinks
        // never produced one for a name Settl does not have. What keeps them from being lost is
        // the booking, not a refusal: each such account comes out Failed, with an error naming
        // the contract, so the drain keeps it in its queue and runs it again once the mapping (or
        // the contract in Settl) is in place. That re-run is additive — customer and payer are
        // search-or-create, links are deltas — so it adds exactly the missing link and nothing else.
        //
        // This replaces the refusal from 2026-08-18. The incident behind that refusal was an
        // account booked as FINISHED with no contract link; the refusal cured it by writing
        // nothing, which also kept thousands of otherwise-healthy accounts out of Settl for as
        // long as their contract was unmapped. The Failed booking alone cures the incident.
        //
        // Only the accounts that need the missing name are booked as Failed. The rest of the batch
        // is untouched — one wrong product name is not a reason to hold up healthy accounts.
        //
        // Capture-mode (reconcile) is left alone: it writes nothing and books nobody.
        //
        // A metering-points-only run books nobody either: it never links a contract to anything,
        // and it stops before the customer loop. It says so in a warning per account, so the
        // completion run's Failed booking is not the first anybody hears of the contract.
        var missingContractAccounts = capturePlans == null
            ? contractResolution.AccountsMissingContracts()
            : [];

        if (options.MeteringPointsOnly && missingContractAccounts.Count > 0)
        {
            LogLine("--- Accounts whose contract is not in Settl (metering points are created anyway) ---");
            foreach (var blocked in missingContractAccounts)
            {
                var msg = $"Account {blocked.Account}: {MissingContractSubject(blocked)} in Settl. "
                        + "This run only creates metering points, so nothing here depends on the "
                        + "contract — the completion run will create the account without its "
                        + "contract link and book it as failed until the contract is mapped (or "
                        + "created in Settl).";
                LogLine($"  WARN: {msg}");
                _warnings.AddWarning(msg);
            }
            LogLine();
        }

        // Raw account number → what it is missing. Looked up per customer in the loop below; the
        // customer loop works on the raw number until just before MigrateAsync, so no padding here.
        var missingContractByAccount = options.MeteringPointsOnly
            ? new Dictionary<string, ContractNameResolver.AccountMissingContracts>(StringComparer.Ordinal)
            : missingContractAccounts.ToDictionary(b => b.Account, StringComparer.Ordinal);
        if (missingContractByAccount.Count > 0)
        {
            LogLine("--- Accounts whose contract is not in Settl (created without the contract link) ---");
            foreach (var blocked in missingContractByAccount.Values)
                LogLine($"  {blocked.Account}: {MissingContractSubject(blocked)} in Settl");
            LogLine();
        }

        // Alle el-målepunkter oprettes her, før den første kunde. Ét nyt målepunkt skal læses
        // tilbage efter en pause fordi Settl ikke er umiddelbart konsistent, og i kunde-løkken
        // betaler hvert målepunkt den pause alene; her deler et helt bundt den. Det giver
        // samtidig alle målepunktsfejl at vide før noget som helst er oprettet.
        //
        // Kunderne sendes u-paddede med vilje: målepunktets payload bruger kundens navn,
        // CVR/CPR og disponent 2 — aldrig kontonummeret — så padningen ændrer intet her, og
        // at hive den ud af løkken ville flytte dens INFO-linjer væk fra den kunde de handler om.
        //
        // Capture-tilstand (reconcile) springer over: den skriver ikke, og den skal blive ved
        // med at gå kunde for kunde, så den ikke kan drive fra create.
        if (capturePlans == null && !options.DryRun)
        {
            // Fasen melder selv sin fremdrift efter hvert bundt, og den melder først noget når den
            // faktisk har målepunkter at oprette. Derfor meldes fasen ind ved dens første melding
            // og ikke før: en vand- eller varme-kørsel når herind, men opretter ingen målepunkter
            // — de er lagt ind udefra og bliver kun slået op — så en fase der blev meldt på forhånd
            // ville love arbejde der aldrig sker.
            //
            // Nævneren tages fra meldingen, ikke fra vores egen optælling. Fasen tæller distinkte
            // el-målepunkter med net, første ejer vinder, og de to regler er ikke garanteret at
            // ende samme sted. En søjle der tæller mod et mål fasen ikke kender, er værre end en
            // der står stille.
            var mpPhaseAnnounced = false;
            var precreate = await settlClient.PrecreateMeteringPointsAsync(
                orderedCustomers, mpsByIdentification, migrationContext, cancellationToken,
                (settled, total) =>
                {
                    if (!mpPhaseAnnounced)
                    {
                        mpPhaseAnnounced = true;
                        OnPhase?.Invoke(MigrationPhases.CreatingMeteringPoints);
                    }
                    OnProgress?.Invoke(settled, total);
                });

            // Settl's own JSON envelope is not a message to a human. What it refused the metering
            // point for is in there, and that is what goes in the log — the envelope around it
            // told the operator nothing and cost forty lines to say it.
            report.Errors.AddRange(precreate.Errors.Select(SettlErrorText.Readable));
            foreach (var w in precreate.Warnings) _warnings.AddWarning(w);

            // Fejler et målepunkt her, er der ingen grund til at oprette kunder ovenpå — med
            // mindre kørslen udtrykkeligt er bedt om at fortsætte. Det er samme regel som i
            // kunde-løkken, bare rykket frem hvor den kan nå at gøre gavn.
            if (precreate.Failed > 0 && !options.ContinueOnError)
            {
                LogLine($"ERROR: {precreate.Failed} målepunkt(er) kunne ikke oprettes — kørslen "
                      + "stopper før nogen kunde oprettes (--force for at fortsætte alligevel)");
                report.MeteringPointsFailed += precreate.Failed;
                // Gennem opsummeringen som enhver anden udgang: springes den over, mister rapporten
                // advarsler, valideringsfund og collector-fejl — netop det man skal bruge for at
                // forstå hvorfor kørslen stoppede.
                MigrationSummaryWriter.WriteAndFinalize(report, _warnings, _log, LogLine);
                OnPhase?.Invoke(null);
                return (1, report);
            }

            // Målepunkts-kørsel: her er kørslen færdig. Fasens tal er kørslens tal — de holdes
            // ellers ude af rapporten, fordi kunde-løkken tæller de samme målepunkter, og den
            // begrundelse gælder ikke når løkken aldrig kører.
            if (options.MeteringPointsOnly)
            {
                report.MeteringPointsCreated = precreate.Created;
                report.MeteringPointsExisting = precreate.AlreadyExisting;
                report.MeteringPointsFailed += precreate.Failed;

                // The same run, folded up to the account somebody has to book. The customer loop
                // never runs here, so no customer outcomes come out of it — and without them a
                // caller keeping score can only see whether the WHOLE run went through. One refused
                // metering point then costs every other account in the batch its bookkeeping, and
                // they are all run again next time.
                //
                // Settl's refusal is carried per account too — journalled, and read there by a
                // person looking at one account. Same treatment as the run-level lines: the reason,
                // not the envelope it arrived in.
                report.MeteringPointAccountOutcomes =
                [
                    .. MeteringPointAccountOutcomeFolder
                        .Fold(orderedCustomers, mpsByIdentification, precreate.MeteringPoints)
                        .Select(o => o.Error == null ? o : o with { Error = SettlErrorText.Readable(o.Error) })
                ];

                LogLine();
                // "Oprettet" betyder her at Settl kvitterede med 201. Kørslen læser dem ikke
                // tilbage — den skal ikke bruge deres id'er til noget — så den kan ikke påstå at
                // en søgning har fundet dem igen. Genoptagelsen søger dem op alligevel, og
                // opretter dem ikke to gange.
                LogLine($"Kun målepunkter: {precreate.Created} oprettet (kvitteret af Settl, ikke "
                      + $"læst tilbage), {precreate.AlreadyExisting} fandtes i forvejen, "
                      + $"{precreate.Failed} fejlede. Kunder, betalere og koblinger mangler — "
                      + "genoptag kørslen for at få dem med.");

                // Samme kørsel talt op per konto. Tallene her går ikke op med målepunkts-tallene
                // ovenfor, og det er meningen: en konto med fem målepunkter tæller som én, og et
                // enkelt afvist målepunkt gør hele kontoen ufærdig.
                if (report.MeteringPointAccountOutcomes.Count > 0)
                {
                    var landedAccounts = report.MeteringPointAccountOutcomes
                        .Count(o => o.Result == Services.MeteringPointAccountResult.Landed);
                    var failedAccounts = report.MeteringPointAccountOutcomes
                        .Count(o => o.Result == Services.MeteringPointAccountResult.Failed);
                    var openAccounts = report.MeteringPointAccountOutcomes.Count - landedAccounts - failedAccounts;
                    LogLine($"Konti: {landedAccounts} har alle deres målepunkter i Settl, "
                          + $"{failedAccounts} har mindst ét afvist målepunkt, {openAccounts} mangler "
                          + "stadig svar på mindst ét målepunkt.");
                }

                var mpOnlyFailed = MigrationSummaryWriter.WriteAndFinalize(report, _warnings, _log, LogLine);
                OnPhase?.Invoke(null);
                return (mpOnlyFailed ? 1 : 0, report);
            }
        }

        // Herfra tælles der kunder, ikke målepunkter. Fasen skifter derfor navn i samme åndedrag
        // som tælleren nulstilles — ellers ville en viser læse de to faser som én stigende række.
        OnPhase?.Invoke(MigrationPhases.MigratingCustomers);
        OnProgress?.Invoke(0, orderedCustomers.Count);
        var done = 0;

        foreach (var origCustomer in orderedCustomers)
        {
            LogLine($"--- Customer {origCustomer.AccountNumber} ({done + 1}/{orderedCustomers.Count}) ---");

            // Apply env-specific pad-9 normalisation to AccountNumber + each Payer.Identifier
            // just before MigrateAsync, so SettlClient's CustomerTranslator/PayerTranslator
            // produce the paddet form Settl expects (production = 6 cifre). CustomerNumber-pad
            // er allerede logget via customerDtos-løkken ovenfor — undgår duplikat-warning.
            var paddedAccountNumber = CustomerNumberPolicy.PadForEnv(origCustomer.AccountNumber, options.Env, out _);
            var paddedPayers = new List<Payer>(origCustomer.Payers.Count);
            foreach (var p in origCustomer.Payers)
            {
                if (string.IsNullOrEmpty(p.Identifier))
                {
                    paddedPayers.Add(p);
                    continue;
                }
                // Multi-payer {AccountNumber}-{n}: pad the AccountNumber part, keep suffix.
                var multiSuffix = MultiPayerSuffix(p.Identifier, origCustomer.AccountNumber);
                if (multiSuffix != null)
                {
                    var paddedMultiId = $"{paddedAccountNumber}-{multiSuffix}";
                    if (paddedMultiId != p.Identifier)
                    {
                        var msg = $"PayerIdentification {p.Identifier} padded to {paddedMultiId} for target environment";
                        LogLine($"  INFO: {msg}");
                        _warnings.AddInfo(msg);
                    }
                    paddedPayers.Add(p with { Identifier = paddedMultiId });
                    continue;
                }
                var paddedId = CustomerNumberPolicy.PadForEnv(p.Identifier, options.Env, out var wasPayerPadded);
                if (wasPayerPadded)
                {
                    var msg = $"PayerIdentification {p.Identifier} padded to {paddedId} for target environment";
                    LogLine($"  INFO: {msg}");
                    _warnings.AddInfo(msg);
                }
                paddedPayers.Add(p with { Identifier = paddedId });
            }
            var customer = origCustomer with { AccountNumber = paddedAccountNumber, Payers = paddedPayers };

            // Capture-mode (reconcile): identical prep + padded customer + context as create,
            // but capture the intended payload instead of POSTing it. No writes, no aggregation.
            // CapturePlanAsync is golden-wire-proven equal to MigrateAsync's payload, so this
            // path cannot drift from create.
            if (capturePlans != null)
            {
                capturePlans.Add(await settlClient.CapturePlanAsync(customer, mpsByIdentification, migrationContext, cancellationToken));
                done++;
                OnProgress?.Invoke(done, orderedCustomers.Count);
                continue;
            }

            var result = await settlClient.MigrateAsync(customer, mpsByIdentification, migrationContext, cancellationToken);

            // Aggregate counters
            if (result.CustomerId == Guid.Empty)
            {
                report.CustomersFailed++;
            }
            else if (result.CustomerCreated)
            {
                report.CustomersCreated++;
            }
            else
            {
                report.CustomersExisting++;
                // Brug PADDED form — report.Customers[].CustomerNumber er padded (jf. customerDtos-loop
                // omkring linje 281), så DTO-laget kan match'e direkte mod DryRunCustomerDto.AccountNum.
                report.ExistingCustomerAccounts.Add(customer.AccountNumber);
            }

            report.PayersCreated += result.PayersCreated;
            report.PayersExisting += result.PayersExisting;
            report.MeteringPointsCreated += result.MeteringPointsCreated;
            report.MeteringPointsExisting += result.MeteringPointsExisting;
            if (result.ExistingPayerIdentifications is { } payerIds)
                report.ExistingPayerIdentifications.UnionWith(payerIds);
            if (result.ExistingMeteringPointIdentifications is { } mpIds)
                report.ExistingMeteringPointIdentifications.UnionWith(mpIds);
            report.CustomerMpsLinked += result.CustomerLinks;
            report.PayerMpsLinked += result.PayerLinks;
            report.ContractMpsLinked += result.ContractLinks;
            report.MeteringPointsFailed += result.MeteringPointsFailed;
            report.CustomerMpsFailed += result.CustomerMpsFailed;
            report.PayerMpsFailed += result.PayerMpsFailed;
            report.ContractMpsFailed += result.ContractMpsFailed;
            report.BillingExemptionsApplied += result.BillingExemptionsApplied;
            report.BillingExemptionsSkipped += result.BillingExemptionsSkipped;
            report.BillingExemptionsFailed += result.BillingExemptionsFailed;
            report.SettlementGroupsAdded += result.SettlementGroupsAdded;
            report.SettlementGroupsSkippedExisting += result.SettlementGroupsSkippedExisting;
            report.SettlementGroupsFailed += result.SettlementGroupsFailed;

            foreach (var error in result.Errors)
                report.Errors.Add(error);

            // An account whose contract Settl does not have was created without its contract link
            // (see the block above the metering-point phase). It is booked as Failed with an error
            // naming the contract, so a retry brings it back for the link — but the error is kept
            // out of result.Errors on purpose: it must not stop a run without --force, exactly as
            // the old refusal never did. Only Settl's own errors on the account decide the stop.
            var outcomeErrors = new List<string>(result.Errors);
            if (missingContractByAccount.TryGetValue(origCustomer.AccountNumber, out var missingContracts))
            {
                var msg = $"Account {origCustomer.AccountNumber} created without its contract link: "
                        + $"{MissingContractSubject(missingContracts)} in Settl. Customer, payer and "
                        + "metering points are in Settl — map the contract (or create it in Settl) and "
                        + "run the account again to add the link.";
                LogLine($"  ERROR: {msg}");
                report.Errors.Add(msg);
                outcomeErrors.Add(msg);
            }

            // Report the outcome before acting on it, so the stop-at-first-error path below
            // still journals the customer that failed.
            OnCustomerCompleted?.Invoke(new CustomerOutcome(
                customer.AccountNumber,
                result.CustomerId == Guid.Empty || outcomeErrors.Count > 0
                    ? CustomerOutcomeKind.Failed
                    : result.CustomerCreated ? CustomerOutcomeKind.Created : CustomerOutcomeKind.Existing,
                outcomeErrors));

            if (result.Errors.Count > 0)
            {
                if (!options.ContinueOnError)
                {
                    LogLine($"=== Migration stopped at customer {customer.AccountNumber} ===");
                    // Finalize on this path too: an aborted run is the one most in need of the
                    // summary block, and skipping it dropped CollectorErrors/ValidationEntries.
                    MigrationSummaryWriter.WriteAndFinalize(report, _warnings, _log, LogLine);
                    OnPhase?.Invoke(null);
                    report.Log = [.. _log];
                    return (1, report);
                }
                LogLine($"=== Customer {customer.AccountNumber} fejlede — fortsaetter (--force) ===");
            }

            done++;
            OnProgress?.Invoke(done, orderedCustomers.Count);
        }

        if (bulkStats != null)
            LogLine(bulkStats.Summary());

        var hasFailed = MigrationSummaryWriter.WriteAndFinalize(report, _warnings, _log, LogLine);
        OnPhase?.Invoke(null);
        return (hasFailed ? 1 : 0, report);
    }

    /// <summary>
    /// Hvor mange målepunkter målepunkts-fasen sætter sig for at klare — samme regel som fasen
    /// selv bruger: hvert målepunkt tælles én gang (den første konto der har det, vinder), og
    /// kun el-målepunkter, for vand og varme lægges ind udefra og bliver kun slået op.
    /// Tallet er fasens nævner; går de to regler fra hinanden, tæller søjlen mod et forkert mål.
    /// </summary>
    private static int CountPrecreatableMeteringPoints(
        IReadOnlyList<Customer> customers,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var customer in customers)
            foreach (var contract in customer.Contracts)
                foreach (var assignment in contract.MeteringPoints)
                {
                    if (!meteringPoints.TryGetValue(assignment.MeteringPointId, out var mp)) continue;
                    if (mp.Grid == null) continue;
                    seen.Add(assignment.MeteringPointId);
                }
        return seen.Count;
    }

    /// <summary>
    /// Delegates customer-slicing to <see cref="Services.Extraction.CustomerFilter.Apply"/>
    /// and emits diagnostic log lines for the slice + any unknown SelectedAccounts.
    /// </summary>
    private XellentLoadResult ApplyCustomerFilter(XellentLoadResult loadResult, CreateOptions options, MigrationReport report)
    {
        var result = Services.Extraction.CustomerFilter.Apply(
            loadResult, options.AccountLimit, options.SelectedAccounts, options.Env);

        if (result.MissingAccounts.Count > 0)
        {
            // Structural, not just a log line: a resume caller needs to distinguish "some
            // accounts left the scope since the run started" (drift, tolerable) from
            // "the whole selection is stale" (handled as an error below).
            report.MissingSelectedAccounts = [.. result.MissingAccounts];
            LogLine($"WARN: {result.MissingAccounts.Count} SelectedAccounts not found in extract: " +
                $"{string.Join(", ", result.MissingAccounts.Take(10))}");
        }

        if (result.WasFiltered)
        {
            LogLine($"Pipeline limited to {result.FilteredCount}/{result.OriginalCount} customers " +
                "(full extract preserved)");
            LogLine();
        }

        return result.LoadResult;
    }

    /// <summary>
    /// Sorterer kunder uden hverken CVR eller CPR fra og lægger dem i rapportens egen sektion.
    /// El-ekstern only: kunder der genuint intet har (hverken CVR, CPR eller noget brugbart i
    /// alternativ-kundenummeret) sorteres i stedet ikke fra — de får det fiktive CPR
    /// "1111110000" og migreres som en almindelig Person-kunde. Se
    /// <see cref="Services.Extraction.MissingIdentityFilter"/> for hvorfor det er sikkert i
    /// forhold til repræsentant-reglen, og hvorfor kun den ene årsag får den behandling.
    /// </summary>
    private XellentLoadResult ApplyMissingIdentityFilter(XellentLoadResult loadResult, MigrationReport report, CreateOptions options)
    {
        var assignFictiveCpr = options.AssignFictiveCprForNoIdentity
            && options.DeliveryCategory == MigrationConstants.DeliveryCategoryElectricity;
        var result = Services.Extraction.MissingIdentityFilter.Apply(loadResult, assignFictiveCpr);
        if (result.Excluded.Count == 0 && result.FictiveCprAssigned.Count == 0)
            return loadResult;

        report.CustomersWithoutIdentity = [.. result.Excluded];
        report.CustomersWithFictiveCpr = [.. result.FictiveCprAssigned];

        if (result.FictiveCprAssigned.Count > 0)
        {
            LogLine($"{result.FictiveCprAssigned.Count} kunder uden CVR og CPR fik det fiktive CPR " +
                    $"{Services.Extraction.MissingIdentityFilter.FictiveCprForNothingCause} og migreres som normalt");
            foreach (var c in result.FictiveCprAssigned.Take(3))
                LogLine($"          {c.AccountNumber} {c.Name}");
            if (result.FictiveCprAssigned.Count > 3)
                LogLine($"          ... og {result.FictiveCprAssigned.Count - 3} flere");
        }

        if (result.Excluded.Count > 0)
        {
            LogLine($"{result.Excluded.Count} kunder uden CVR og CPR sorteret fra — de migreres ikke " +
                    "(se rapportens egen sektion)");

            // Grupperet efter hvorfor: 1.400 kontonumre siger ingenting, men "381 har et CVR
            // liggende i alternativ-kundenummeret" er en opgave nogen kan tage fat på.
            foreach (var group in result.Excluded
                .GroupBy(c => c.Cause)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key))
            {
                LogLine($"  {group.Count(),6}  {Services.Extraction.MissingIdentityClassifier.Describe(group.Key)}");
                foreach (var c in group.Take(3))
                    LogLine($"          {c.AccountNumber} {c.Name}");
                if (group.Count() > 3)
                    LogLine($"          ... og {group.Count() - 3} flere");
            }
        }
        LogLine();

        return result.LoadResult;
    }

    /// <summary>
    /// Preflight: verify every mapped settlement-group name exists in Settl. Missing groups
    /// emit a collector-error before the migrate-loop starts — without this, the error
    /// surfaces mid-loop after several customers are already created.
    /// Single bulk ListAllAsync call + in-memory name lookup (per ISettlSettlementGroupClient
    /// docs: "build a name→GUID map once instead of one FindByNameAsync per target group").
    /// </summary>
    private async Task ValidateSettlementGroupsAsync(
        Dictionary<string, string> settlementGroupMap,
        CancellationToken cancellationToken)
    {
        const string countryCode = "DK";
        LogLine("--- Settlement Group preflight ---");

        var distinctNames = settlementGroupMap.Values
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctNames.Count == 0)
        {
            LogLine("  (no mapped groups)");
            LogLine();
            return;
        }

        var (allGroups, listError) = await _settlementGroupClient.ListAllAsync(countryCode, cancellationToken);
        if (allGroups == null)
        {
            _warnings.AddError($"Settlement group preflight failed (ListAllAsync): {listError}");
            LogLine($"  ERROR: {listError}");
            LogLine();
            return;
        }

        var byName = allGroups
            .Where(g => !string.IsNullOrEmpty(g.GroupName))
            .GroupBy(g => g.GroupName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in distinctNames)
        {
            if (byName.TryGetValue(name, out var group))
                LogLine($"  OK: {name} ({group.Id})");
            else
            {
                _warnings.AddError($"Settlement group '{name}' not found in Settl");
                LogLine($"  MISSING: {name}");
            }
        }
        LogLine();
    }

    /// <summary>
    /// Converts ContractNameResolver output to pre-resolved SettlMpContractLink list.
    /// Groups by (mpId, contractId) to dedup history rows, taking max startdate and
    /// merging enddate (EndDateNull dominates concrete dates).
    /// </summary>
    private static List<SettlMpContractLink> BuildMpContractLinks(
        ContractNameResolver.Result contractResolution,
        bool openEndedContractLinks)
    {
        return contractResolution.Links
            .Where(l => contractResolution.ContractsByName.ContainsKey(l.ContractName))
            .GroupBy(l => (l.MeteringPointId, ContractId: contractResolution.ContractsByName[l.ContractName].Id))
            .Select(g =>
            {
                var maxEnd = g.Any(l => l.ContractPartEnddate == MigrationConstants.EndDateNull)
                    ? MigrationConstants.EndDateNull
                    : g.Max(l => l.ContractPartEnddate);
                return new SettlMpContractLink(
                    g.Key.MeteringPointId,
                    g.Key.ContractId,
                    g.Max(l => l.ContractPartStartdate),
                    maxEnd,
                    g.Any(l => l.IsJoined),
                    openEndedContractLinks);
            })
            .ToList();
    }

    /// <summary>Buffer text without newline. Flushed on next LogLine call.</summary>
    private void Log(string text)
    {
        _lineBuffer.Append(text);
    }

    /// <summary>
    /// Names the contract(s) an account needs and Settl does not have, in singular or plural.
    /// Shared by the per-account error on a full run and the warning on a metering-points-only
    /// run so the two say the same thing about the same account and only differ in what follows.
    /// The phrase "contract 'X' does not exist in Settl" is what the dashboard groups failures by
    /// — keep it verbatim.
    /// </summary>
    private static string MissingContractSubject(ContractNameResolver.AccountMissingContracts blocked)
    {
        var names = string.Join("', '", blocked.ContractNames);
        return blocked.ContractNames.Count == 1
            ? $"contract '{names}' does not exist"
            : $"contracts '{names}' do not exist";
    }

    /// <summary>
    /// Returns the numeric suffix if <paramref name="identifier"/> matches the multi-payer
    /// pattern <c>{accountNumber}-{n}</c> (e.g. "12345-1"), otherwise null.
    /// </summary>
    private static string? MultiPayerSuffix(string identifier, string accountNumber)
    {
        var prefix = accountNumber + "-";
        if (!identifier.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var suffix = identifier[prefix.Length..];
        return suffix.Length > 0 && suffix.All(char.IsDigit) ? suffix : null;
    }

    /// <summary>Flush buffered text as a complete line to _log list and _onLogLine callback.</summary>
    private void LogLine(string text = "")
    {
        _lineBuffer.Append(text);
        var line = _lineBuffer.ToString();
        _log.Add(line);
        _onLogLine?.Invoke(line);
        _lineBuffer.Clear();
    }
}
