namespace DataMigration.OpsCli.Services;

/// <summary>
/// One resolved el metering point: the Settl MP joined to its Xellent contract and translated
/// to the representative account (= Settl CustomerNumber). Input to
/// <see cref="SettlementScheduleGrouper"/>.
/// </summary>
/// <param name="ContractTemplate">The same contract's <c>Contracttemplatenum</c> ("skabelon"), input
/// to <see cref="CustomerSegmentGrouper"/>. Null when the contract carries no template — the MP then
/// contributes no segment and is listed in <see cref="SettlementConfigDocument.SegmentUnresolved"/>,
/// but still counts as a schedule row.</param>
/// <param name="ProductNums">The <c>Productnum</c>s active on the same pinned contract. Paired with
/// <paramref name="ContractTemplate"/> per metering point rather than pooled per customer: a customer
/// can hold two templates and two products, and pooling them would invent template+product
/// combinations that exist on no metering point.</param>
/// <param name="SettlContracts">The Settl contract names the MP is currently attached to, read from
/// Settl. Empty when the MP hangs on no contract.</param>
/// <param name="HasExemption">Whether the MP carries a billing exemption in Settl. Null when
/// exemptions were not read (they cost one call per MP and are opt-in) — which is not the same as
/// "no exemption" and must not be reported as one.</param>
/// <param name="CustomerInXellent">Whether <paramref name="CustomerNumber"/> exists as an account in
/// Xellent at all. False means the customer was created directly in Settl, and then the metering
/// point's COS-1 contract belonged to whoever held the metering point <i>before</i> her — a new
/// tenant at an address the previous holder was billed for. Every Xellent-derived field on this entry
/// is therefore cleared when this is false: <c>BillingPlanNum</c> empty, no template, no products.
/// Reporting them would describe the wrong customer, and the row's schedule and segment would be the
/// previous holder's (see the Klinik L / Møllevænget case in
/// <c>plans/settlement-config-settl-native-customers.md</c>).</param>
public sealed record SettlementConfigEntry(
    string MeteringPointIdentification,
    string BillingPlanNum,
    string CustomerNumber,
    string? CustomerName,
    string? ContractTemplate = null,
    IReadOnlyList<string>? ProductNums = null,
    IReadOnlyList<string>? SettlContracts = null,
    bool? HasExemption = null,
    bool CustomerInXellent = true,
    string? XellentAccount = null,
    DateOnly? ReferenceDate = null,
    IReadOnlyList<string>? ChargeProducts = null,
    string ChargeReadStatus = "NotRead",
    string? XellentContractNumber = null,
    string? MeterPointTypeCode = null, string MeterPointTypeReadStatus = "NotRead");

/// <summary>Why an in-delivery Settl MP produced no schedule row.</summary>
public enum SettlementConfigUnresolvedReason
{
    /// <summary>No el-ekstern delpoint with this identification in Xellent at all — the MP has no
    /// Xellent origin (created directly in Settl).</summary>
    NotFoundInXellent,
    /// <summary>Delpoint exists in Xellent, but no contract ends at COS-1 — the migrated contract
    /// was deleted or changed after migration.</summary>
    NoContractAtChangeOfSupplier,
    /// <summary>Contract found but <c>Billingplannum</c> is empty.</summary>
    NoBillingPlan,
    /// <summary>Account could not be translated to a representative (customer never migrated).</summary>
    NoRepresentative,
    DeliveryHistoryReadFailed,
    DeliveryHistoryMissing,
    DeliveryHistoryAmbiguous,
    HistoricalCustomerReadFailed,
    HistoricalCustomerMissing,
    HistoricalCustomerAmbiguous,
    HistoricalPortfolioIncomplete,
    XellentCustomerMismatch,
    NotFoundInSettl,
    NoDeliveryPeriod,
    NotInDelivery,
    ChildMeteringPoint,
    AmbiguousHistoricalContract,
    AmbiguousSettlRecord,
    FirstDeliveryIsMoveIn,
    FirstDeliveryStartReasonUnknown,
    FirstDeliveryNotChangeOfSupplier
}

/// <summary>
/// An in-delivery Settl metering point that produced no schedule row. It has no Xellent contract, so
/// it carries no template and no customer of its own — the customer below is read straight from Settl
/// (metering point → payer → customer) so the row can be acted on rather than just counted.
/// </summary>
/// <param name="CustomerNumber">The Settl customer holding the metering point. Null when the lookup
/// was not run or found nothing.</param>
/// <param name="Identity">CVR or CPR as registered on that Settl customer — the half of the segment
/// answer that separates a company from a private consumer. Null when the customer is unknown.</param>
/// <param name="Segment">The segment the customer should have: the config's standing answer for
/// <paramref name="Reason"/>, or — when the customer already sits in a segment through its other
/// metering points — that segment. Null when neither applies.</param>
/// <param name="CustomerHasOwnSegment">True when <paramref name="Segment"/> is the customer's own,
/// decided by its resolved metering points. The standing answer never overrules that: this metering
/// point is one of several, and the customer is already placed.</param>
public sealed record SettlementConfigUnresolved(
    string MeteringPointIdentification,
    SettlementConfigUnresolvedReason Reason,
    string? CustomerNumber = null,
    string? CustomerName = null,
    CustomerIdentityKind? Identity = null,
    string? Segment = null,
    bool CustomerHasOwnSegment = false);

public sealed record SettlementConfigCustomer(
    string CustomerNumber,
    string? Name,
    int MeteringPointCount);

/// <param name="Schedule">One schedule the customer's metering points point at.</param>
/// <param name="MeteringPointIdentifications">The metering points behind this candidate — the
/// starting point for deciding the schedule by hand, rather than a count to take on trust.</param>
public sealed record SettlementScheduleCandidate(
    string Schedule,
    bool Mapped,
    IReadOnlyList<string> BillingPlanNums,
    int MeteringPointCount,
    IReadOnlyList<string> MeteringPointIdentifications);

/// <summary>
/// A customer whose metering points land on more than one settlement schedule. She is invoiced on
/// one cadence, so this is a decision waiting to be made, not a customer with two schedules —
/// and she is deliberately absent from <see cref="SettlementConfigDocument.Schedules"/>.
/// </summary>
/// <param name="Candidates">Ordered by metering-point count, most first.</param>
public sealed record SettlementScheduleConflict(
    string CustomerNumber,
    string? Name,
    IReadOnlyList<SettlementScheduleCandidate> Candidates);

/// <summary>
/// A standing answer for one exact set of disagreeing schedules — Janni/Lone, 2026-09-08. Matched
/// only when a conflicted customer's candidate schedules are exactly this set (same members, same
/// count): a customer whose disagreement also touches a schedule outside the set is not covered and
/// stays a conflict, because the decision was never made for that combination.
///
/// <para>Not a priority ranking. Rows do not reduce to a single order of the four schedules — e.g.
/// <c>HalvårBagud</c> beats <c>KvartalAC</c> and <c>Md+MdAC</c> head-to-head and beats
/// <c>Måned.</c>, but <c>KvartalAC</c> wins when all four are present — so each combination is its
/// own rule rather than a rank lookup.</para>
/// </summary>
/// <param name="Schedules">The exact set of schedules this rule answers for. Matched
/// case-insensitively and independent of order.</param>
/// <param name="Winner">The schedule the customer is placed under when her candidates match
/// <paramref name="Schedules"/> exactly.</param>
public sealed record ScheduleConflictRule(
    IReadOnlyList<string> Schedules,
    string Winner);

public sealed record SettlementScheduleGroup(
    string Schedule,
    bool Mapped,
    IReadOnlyList<string> BillingPlanNums,
    int CustomerCount,
    int MeteringPointCount,
    IReadOnlyList<SettlementConfigCustomer> Customers);

/// <param name="SegmentsAssigned">Customers that landed in exactly one segment. Customers in
/// <see cref="SettlementConfigDocument.SegmentConflicts"/> are not counted here.</param>
/// <param name="CustomersWithCvr">Customers carrying a CVR, i.e. the ones Settl refuses a private
/// segment on. With <paramref name="CustomersWithCpr"/> and <paramref name="CustomersWithoutIdentifier"/>
/// this covers every customer in the export — the split the segment rule turns on.</param>
/// <param name="UnresolvedWithSegment">Of <paramref name="Unresolved"/>, those that got a segment
/// answer anyway — from the config's standing answer, or from their customer already being placed by
/// its other metering points. The rest still need a decision.</param>
/// <param name="SegmentsFromMultiple">Of <paramref name="SegmentsAssigned"/>, those placed by the
/// config's <c>multipleSegments</c> answer because their metering points pointed at different
/// segments — the ones that used to be conflicts, kept countable so the number can be watched.</param>
/// <param name="SchedulesFromSettlementGroup">Customers whose metering points named several schedules and who were placed by the schedule their Settl settlement group names. They are counted inside the schedule groups, not in <paramref name="ScheduleConflicts"/>.</param>
/// <param name="SchedulesFromConflictTable">Of the customers whose settlement group named no schedule, those placed by the config's <c>scheduleConflicts</c> table because their exact set of candidate schedules matched one of its rules. Counted inside the schedule groups, not in <paramref name="ScheduleConflicts"/>.</param>
/// <param name="MeteringPointsWithSettlCustomer">Of <paramref name="MeteringPointsResolved"/>, those
/// whose customer number was read from Settl. The rest fell back to the Xellent representative.</param>
/// <param name="CustomersFromXellentFallback">Customers named by the Xellent representative because
/// Settl gave no answer for any of their metering points. Their number is a Xellent Accountnum and
/// may well not exist in Settl — the rows to treat with suspicion.</param>
/// <param name="CustomersMovedBySettlLookup">Customers where Settl's answer differs from the Xellent
/// representative, i.e. the rows that would have carried a wrong customer number before. Zero means
/// the two sources agree everywhere, not that the lookup was skipped.</param>
/// <param name="CustomersNotInXellent">Customers whose number is no Xellent account — created directly
/// in Settl. Their metering points still have a Xellent history, but it belongs to the previous
/// holder, so no plan, template or product is reported for them and their schedule can only come from
/// their settlement group. See <see cref="SettlementConfigEntry.CustomerInXellent"/>.</param>
/// <param name="SegmentsFromSettlRule">Of <paramref name="SegmentsAssigned"/>, the customers placed by
/// the Settl-driven rule because they have no Xellent template of their own. Their segment rests on
/// what Settl holds today, not on a contract — worth telling apart from the rest.</param>
/// <param name="CustomersWithoutSchedule">Customers left in no schedule group and in no conflict —
/// nothing named a schedule for them at all. Today this can only be a
/// <paramref name="CustomersNotInXellent"/> customer who is in no settlement group either. Counted so
/// that the schedule groups' customer count plus the conflicts adds up to the population.</param>
public sealed record SettlementConfigTotals(
    int MeteringPointsInSettl,
    int MeteringPointsResolved,
    int UniqueCustomers,
    int Schedules,
    int Unresolved,
    int ScheduleConflicts = 0,
    int SchedulesFromSettlementGroup = 0,
    int SchedulesFromConflictTable = 0,
    int Segments = 0,
    int SegmentsAssigned = 0,
    int SegmentConflicts = 0,
    int SegmentUnresolved = 0,
    int CustomersWithCvr = 0,
    int CustomersWithCpr = 0,
    int CustomersWithoutIdentifier = 0,
    int SegmentsFromMultiple = 0,
    int UnresolvedWithSegment = 0,
    int MeteringPointsWithSettlCustomer = 0,
    int CustomersFromXellentFallback = 0,
    int CustomersMovedBySettlLookup = 0,
    int CustomersNotInXellent = 0,
    int CustomersWithoutSchedule = 0,
    int SegmentsFromSettlRule = 0);

// ---- Customer segments (Xellent Contracttemplatenum → Settl Customer Segment) ----

/// <summary>
/// One segment group. Unlike schedules, a customer appears under at most one segment: the Settl
/// segment is a customer-level property, so a customer whose metering points disagree is a conflict,
/// not a member of several groups.
/// </summary>
public sealed record CustomerSegmentGroup(
    string Segment,
    bool Mapped,
    IReadOnlyList<string> ContractTemplates,
    int CustomerCount,
    int MeteringPointCount,
    IReadOnlyList<SettlementConfigCustomer> Customers);

/// <summary>Why a customer could not be placed in a single segment.</summary>
public enum CustomerSegmentConflictReason
{
    /// <summary>Metering points map to more than one segment — the customer must be split or a
    /// segment chosen manually.</summary>
    MultipleSegments,
    /// <summary>At least one template is mapped to several candidate segments (e.g. V_LadePriv →
    /// Privat_Opladning_Eje / _Leje), which this export cannot tell apart.</summary>
    AmbiguousTemplate,
    /// <summary>Both of the above.</summary>
    Both
}

/// <param name="Segments">Candidate segments for this template: one entry = determined,
/// several = ambiguous.</param>
/// <param name="MeteringPointIdentifications">The MPs behind the candidate — the starting point for
/// deciding the segment manually (for V_LadePriv: look up the charging installation's contract).</param>
public sealed record CustomerSegmentCandidate(
    string ContractTemplate,
    IReadOnlyList<string> Segments,
    bool Mapped,
    int MeteringPointCount,
    IReadOnlyList<string> MeteringPointIdentifications);

/// <param name="Identity">The customer's Xellent identification, which is half of the segment rule —
/// a conflict cannot be judged without knowing whether the customer is a company.</param>
public sealed record CustomerSegmentConflict(
    string CustomerNumber,
    string? Name,
    CustomerSegmentConflictReason Reason,
    IReadOnlyList<CustomerSegmentCandidate> Candidates,
    CustomerIdentityKind Identity = CustomerIdentityKind.Blank);

/// <summary>
/// A customer's metering points whose COS-1 contract carries no <c>Contracttemplatenum</c>, so they
/// contribute no segment. Reported rather than silently ignored, since they can hide a segment
/// disagreement.
///
/// <para>One row per customer, not per metering point: the segment is a customer-level decision, and
/// a customer repeated once per metering point makes the list read as many more open questions than
/// there are. A customer whose other metering points do carry a template is still here — the missing
/// one can hide a disagreement — but a customer the Settl-driven rule placed is not: nothing about her
/// is open.</para>
/// </summary>
public sealed record CustomerSegmentUnresolved(
    string CustomerNumber,
    IReadOnlyList<string> MeteringPointIdentifications);

/// <summary>
/// En kunde som intet navngav et skema for. Ikke en konflikt — der er ikke to kilder der er
/// uenige, der er ingen kilde — og derfor et sted hvor nogen skal beslutte noget.
///
/// <para><see cref="Reason"/> er ordret den forklaring
/// <see cref="SettlementGroupScheduleResolver"/> selv giver, og de tre svar kræver tre
/// forskellige handlinger: er kunden i ingen gruppe, skal nogen vælge et skema til hende;
/// er hun i en gruppe der ikke står i <c>groupSchedules</c>, er det en rettelse i config og
/// ikke i data; peger hendes grupper på flere skemaer, skal én af dem vælges fra.</para>
/// </summary>
public sealed record CustomerWithoutSchedule(
    string CustomerNumber,
    string? Name,
    int MeteringPointCount,
    IReadOnlyList<string> MeteringPointIdentifications,
    string Reason);

// ---- Candidate-rule comparison (decision material, decides nothing) ----

/// <summary>How the candidate sources for one value relate for a single customer.</summary>
public enum SettlementConfigAgreement
{
    /// <summary>Every source that produced a value produced the same one.</summary>
    Agree,
    /// <summary>At least two sources produced different values — the row a human has to look at.</summary>
    Disagree,
    /// <summary>Fewer than two sources produced a value, so there is nothing to compare. Distinct
    /// from <see cref="Agree"/>: silence is not consensus.</summary>
    Undecided
}

/// <summary>
/// One customer with every candidate schedule and segment side by side. This is the row the choice
/// between the rule sets is made on, so no source is allowed to overwrite another — each keeps its
/// own field and the disagreements are counted rather than resolved.
/// </summary>
/// <param name="SettlementGroups">The groups the customer is in, as named in Settl. Null when
/// membership was not read — distinct from an empty list, which means the customer really is in no
/// group.</param>
/// <param name="ScheduleFromGroup">Schedule implied by the Settlement Group the customer is actually
/// in in Settl. Null when the customer is in no group, or in none that the map names.</param>
/// <param name="SettlContracts">Settl contracts across the customer's metering points. Null when the
/// contract listing was not read.</param>
/// <param name="ScheduleFromBillingPlan">Schedule implied by Xellent <c>Billingplannum</c> — the rule
/// in force today.</param>
/// <param name="SegmentFromTemplate">Segment from the template+identification rule in force today.</param>
/// <param name="SegmentFromSettl">Segment from the Settl-driven rule (CVR + SME contract + exemption).</param>
/// <param name="SegmentFromTemplateProduct">Segment from the template+product table.</param>
/// <param name="HasExemption">Null when exemptions were not read — see
/// <see cref="SettlementConfigEntry.HasExemption"/>.</param>
/// <param name="ExpectedContractFromTable">Settl contracts the template+product table expects, for
/// comparison with <paramref name="SettlContracts"/>. Rows the table leaves to the erhverv rule
/// contribute nothing here.</param>
/// <param name="Notes">Why a source stayed silent or produced a conflict, in words — so a blank
/// field is never mistaken for a missing rule.</param>
public sealed record SettlementConfigCustomerRow(
    string CustomerNumber,
    string? Name,
    CustomerIdentityKind Identity,
    int MeteringPointCount,
    IReadOnlyList<string>? SettlementGroups,
    string? ScheduleFromGroup,
    IReadOnlyList<string> BillingPlanNums,
    string? ScheduleFromBillingPlan,
    SettlementConfigAgreement ScheduleAgreement,
    string? SegmentFromTemplate,
    string? SegmentFromSettl,
    string? SegmentFromTemplateProduct,
    SettlementConfigAgreement SegmentAgreement,
    bool Sme,
    bool? HasExemption,
    IReadOnlyList<string> ContractTemplates,
    IReadOnlyList<string> ProductNums,
    IReadOnlyList<string>? SettlContracts,
    IReadOnlyList<string> ExpectedContractFromTable,
    IReadOnlyList<string> Notes);

/// <param name="SettlementGroupsWithoutSchedule">Group names present in Settl that the map says
/// nothing about — the gap that would silently produce no schedule.</param>
/// <param name="ScheduleNamesNotInSettl">Group names in the map that match no active group in Settl.
/// Null when the Settl group listing was not read: an empty list would claim every entry matches.</param>
/// <param name="SettlementGroupMembershipRead">False when the group membership could not be read
/// from Settl. Every group-derived value is then absent for reasons that have nothing to do with the
/// customers, and <paramref name="CustomersInSettlementGroup"/> is a zero that means "unknown".</param>
/// <param name="SettlContractsRead">False when the Settl contract listing could not be read, so the
/// SME test never ran.</param>
public sealed record SettlementConfigComparisonSummary(
    int ScheduleAgree,
    int ScheduleDisagree,
    int ScheduleUndecided,
    int SegmentAgree,
    int SegmentDisagree,
    int SegmentUndecided,
    int CustomersInSettlementGroup,
    int CustomersWithExemption,
    IReadOnlyList<string> SettlementGroupsWithoutSchedule,
    IReadOnlyList<string>? ScheduleNamesNotInSettl,
    bool SettlementGroupMembershipRead = true,
    bool SettlContractsRead = true);

/// <summary>
/// What Settl says about one customer, with no rule applied to it. Separate from
/// <see cref="SettlementConfigCustomerRow"/> on purpose: a host that only wants the facts —
/// settlement-group membership and billing exemptions — should not have to run the candidate-rule
/// comparison to get them, and should not be handed rule answers it did not ask for.
/// </summary>
/// <param name="SettlementGroups">The groups the customer is in, as named in Settl. Null when
/// membership was not read — distinct from an empty list, which means the customer really is in no
/// group.</param>
/// <param name="MeteringPointsWithExemption">How many of the customer's metering points carry a
/// billing exemption in Settl. Null when exemptions were not read.</param>
/// <param name="ScheduleFromSettlementGroup">What the customer's Settlement Group says her
/// settlement schedule should be. Null when she is in no group, when the group is not in the map,
/// or when membership was not read — <paramref name="SettlementGroups"/> tells the three apart.</param>
/// <param name="SegmentFromSettlRule">What the Settl-driven rule says her segment should be: SME on
/// CVR plus an SME contract, otherwise the business segment, or the exempt one when a metering point
/// carries a billing exemption. Null when the rule cannot answer — she has no CVR, or an input was
/// not read.</param>
public sealed record SettlementConfigCustomerFacts(
    string CustomerNumber,
    string? Name,
    CustomerIdentityKind Identity,
    int MeteringPointCount,
    IReadOnlyList<string>? SettlementGroups,
    int? MeteringPointsWithExemption,
    IReadOnlyList<string> ContractTemplates,
    IReadOnlyList<string> ProductNums,
    string? ScheduleFromSettlementGroup = null,
    string? SegmentFromSettlRule = null);

// ---- Change-of-supplier cohort (only present when the run was narrowed to one date) ----

/// <summary>
/// One customer in the cohort, with her metering points split by why they are in the export.
/// </summary>
/// <param name="MeteringPointsOnDate">Her metering points whose Settl change-of-supplier date is the
/// filter date. At least one, or she would not be here.</param>
/// <param name="MeteringPointsFromSharedCustomer">Her other metering points, migrated on some other
/// date and kept only because she holds them. They are in the export because her schedule and segment
/// are decided by all her metering points together — leaving them out would judge her on part of her
/// estate. Empty for a customer whose whole estate moved on the one date.</param>
public sealed record SettlementConfigChangeOfSupplierFilterCustomer(
    string CustomerNumber,
    string? Name,
    IReadOnlyList<string> MeteringPointsOnDate,
    IReadOnlyList<string> MeteringPointsFromSharedCustomer);

/// <summary>
/// What the change-of-supplier filter selected. Absent from the document entirely when the run was
/// not narrowed — every other number in the file then covers the whole environment.
/// </summary>
/// <param name="MeteringPointsOnDate">Metering points carrying the date, including those with no
/// customer.</param>
/// <param name="MeteringPointsFromSharedCustomer">Metering points on some other date, kept because
/// they share a customer with one that carries it.</param>
/// <param name="MeteringPointsOnDateWithoutCustomer">Metering points on the date that neither Settl
/// nor Xellent could name a customer for. They are kept on their own date, since there is no customer
/// to keep them whole with.</param>
public sealed record SettlementConfigChangeOfSupplierFilter(
    DateOnly Date,
    int Customers,
    int MeteringPointsOnDate,
    int MeteringPointsFromSharedCustomer,
    IReadOnlyList<SettlementConfigChangeOfSupplierFilterCustomer> CustomerRows,
    IReadOnlyList<string> MeteringPointsOnDateWithoutCustomer);

/// <param name="Segments">Customers with exactly one determined segment.</param>
/// <param name="SegmentConflicts">Customers left out of <paramref name="Segments"/> — see
/// <see cref="CustomerSegmentConflictReason"/>.</param>
/// <param name="Customers">Per-customer comparison of every candidate rule; see
/// <see cref="SettlementConfigCustomerRow"/>. Null when the comparison did not run.</param>
public sealed record SettlementConfigDocument(
    DateTime GeneratedAt,
    string Environment,
    string DataAreaId,
    SettlementConfigTotals Totals,
    IReadOnlyList<string> UnmappedBillingPlanNums,
    IReadOnlyList<SettlementScheduleGroup> Schedules,
    IReadOnlyList<SettlementConfigUnresolved> Unresolved,
    IReadOnlyList<SettlementScheduleConflict>? ScheduleConflicts = null,
    IReadOnlyList<string>? UnmappedContractTemplates = null,
    IReadOnlyList<CustomerSegmentGroup>? Segments = null,
    IReadOnlyList<CustomerSegmentConflict>? SegmentConflicts = null,
    IReadOnlyList<CustomerSegmentUnresolved>? SegmentUnresolved = null,
    SettlementConfigComparisonSummary? Comparison = null,
    IReadOnlyList<SettlementConfigCustomerRow>? Customers = null,
    IReadOnlyList<SettlementConfigCustomerFacts>? CustomerFacts = null,
    IReadOnlyList<CustomerWithoutSchedule>? CustomersWithoutSchedule = null,
    // Omitted from the JSON when null, so a run without the filter produces exactly the file it
    // produced before this option existed.
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SettlementConfigChangeOfSupplierFilter? ChangeOfSupplierFilter = null,
    IReadOnlyList<SettlementConfigReviewRow>? Review = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SettlementConfigHistoricalSelection? HistoricalSelection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SettlementConfigExemptionReview? ExemptionReview = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SettlementConfigE18Review? E18Review = null,
    SettlementConfigMeteringPointSelection? MeteringPointSelection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SettlementConfigMeteringPointCoverage>? MeteringPointCoverage = null,
    IReadOnlyList<SettlementConfigContractPartEvidence>? ContractPartEvidence = null,
    IReadOnlyList<SettlementConfigChargingPartEvidence>? ChargingContractPartEvidence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SettlementConfigCustomerResult>? CustomerResults = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SettlementConfigNamedCustomerExceptionEvidence>? NamedCustomerExceptionEvidence = null);

public sealed record SettlementConfigCustomerResult(string? CustomerNumber,string? CustomerName,
    string? ProposedSegment,string? ProposedSchedule,IReadOnlyList<string> Problems,
    IReadOnlyList<SettlementConfigMeteringPointResult> MeteringPoints);
public sealed record SettlementConfigMeteringPointResult(string MeteringPointIdentification,DateOnly? ChangeOfSupplierDate,
    string? ProposedSegment,string? ProposedSchedule,IReadOnlyList<string> Problems);
public sealed record SettlementConfigNamedCustomerExceptionEvidence(string CustomerNumber,string CustomerName,string Rule,
    string MeteringPointIdentification,string? OriginalProposedSegment,string? OriginalProposedSchedule,
    string? ProposedSegment,string? ProposedSchedule,bool SegmentApplied,bool ScheduleApplied,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? MatchedPattern = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? MatchMode = null);

/// <summary>Historical scope evidence; issues outside the known cohort mean its population is incomplete.</summary>
public sealed record SettlementConfigHistoricalSelection(
    DateOnly AsOfDate, string DeliverySource, string CustomerSource,
    IReadOnlyList<SettlementConfigHistoricalIssue> Issues);

public sealed record SettlementConfigHistoricalIssue(
    string MeteringPointIdentification, SettlementConfigUnresolvedReason Reason);

/// <summary>Dated, non-personal Xellent basis for the proposed assignment.</summary>
public sealed record SettlementConfigXellentBasis(
    string MeteringPointIdentification, string? Account, DateOnly? ReferenceDate,
    string BillingPlanNum, string? ContractTemplate, IReadOnlyList<string>? ChargeProducts,
    string ChargeReadStatus, IReadOnlyList<string>? ProductNums = null, string? ContractNumber = null,
    string? MeterPointTypeCode = null, string MeterPointTypeReadStatus = "NotRead");

/// <summary>Actual Settl assignment status. Null values alone must never imply a successful read.</summary>
public sealed record SettlementConfigCurrentSegment(string? Segment, string Status, Guid? SegmentId = null);

/// <summary>Review of the same proposals written to the primary JSON groups.</summary>
public sealed record SettlementConfigReviewRow(
    string CustomerNumber, string? ProposedSegment, string? ProposedSchedule,
    string? CurrentSegment, string? CurrentSchedule, string CurrentSegmentStatus,
    string CurrentScheduleStatus,
    [property: System.Text.Json.Serialization.JsonIgnore] bool? SegmentDiffers,
    [property: System.Text.Json.Serialization.JsonIgnore] bool? ScheduleDiffers,
    [property: System.Text.Json.Serialization.JsonIgnore] bool? ChargingOwnershipDiffers,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string>? SettlChargingSegments,
    IReadOnlyList<SettlementConfigXellentBasis> Xellent, IReadOnlyList<string> UnresolvedReasons,
    Guid? CurrentSegmentId = null);

public sealed record SettlementConfigMeteringPointSelection(int Supplied, int Unique, int Duplicates)
{
    public string DateSource { get; init; } = "SuppliedChangeOfSupplierDate";
    public string CustomerSource { get; init; } = "XellentContractAtChangeOfSupplierMinusOne";
    public DateOnly? ChangeOfSupplierDate { get; init; }
}
public sealed record SettlementConfigMeteringPointCoverage(string MeteringPointIdentification,
    DateOnly? ChangeOfSupplierDate, string? CustomerNumber, string? ProposedSegment,
    string? ProposedSchedule, IReadOnlyList<string> Problems);
public sealed record SettlementConfigContractPartEvidence(string MeteringPointIdentification,
    string Account, string ContractNumber, string ProductNum, DateOnly ReferenceDate,
    DateTime PartStart, DateTime PartEnd, string Source, DateTime? LoggedAt = null, long? LogRecordId = null,
    string? Company = null, string? Agreement = null, string? DeliveryCategory = null, string? DelpointNumber = null);

public sealed record SettlementConfigChargingPartEvidence(string Account,DateOnly ReferenceDate,
    string ContractNumber,string Company,string Agreement,string ProductNum,DateTime PartStart,
    DateTime PartEnd,string Source,DateTime? LoggedAt,long? LogRecordId,bool Ambiguous,
    string? ChangeType = null,string? LogEmployeeId = null,string? RestorationStatus = null,
    DateTime? RestoredPartEnd = null,long? RestorationLogRecordId = null,DateTime? RestorationLoggedAt = null,
    bool? ActiveAtReferenceDate = null,string? RestorationProblem = null);
