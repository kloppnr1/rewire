namespace DataMigration.SettlClient;

/// <summary>
/// One pre-resolved metering-point → Settl contract link. Built by <c>CreateRunner</c>
/// from <c>ContractNameResolver</c> output before the per-customer loop starts.
/// </summary>
public sealed record SettlMpContractLink(
    string MeteringPointId,
    Guid ContractId,
    DateTime ContractPartStartdate,
    DateTime ContractPartEnddate,
    bool IsJoined,
    bool OpenEnded = false);

/// <summary>
/// Per-batch context for <see cref="ISettlClient.MigrateAsync"/>. Caller resolves
/// these once before iterating customers (Settl contract-template lookup, billing-
/// exemption config, owner assignments) and passes the same instance per call.
/// </summary>
public sealed record SettlMigrationContext(
    bool DryRun,
    DateTime ChangeOfSupplierDate,
    IReadOnlyList<SettlMpContractLink> MpContractLinks,
    SettlBillingExemptionContext? ExemptionContext,
    IReadOnlyDictionary<string, string>? SettlementGroupMap,
    IReadOnlyDictionary<string, Guid>? OwnerByCustomerNumber,
    /// <summary>
    /// MP identification → CONTACTPERSON.NAME. Used for OioUbl use case 2:
    /// when payer has no uniform OrderIdentifier, each MP gets its own
    /// PaymentConfiguration.OrderIdentifier = NAME from the MP's contract contact person.
    /// Null disables the use-case-2 check entirely.
    /// </summary>
    IReadOnlyDictionary<string, string?>? MpOrderIdentifiers = null,
    /// <summary>
    /// Padded CustomerNumber → original un-padded customer number. Used by Step 1 of
    /// <see cref="ISettlClient.MigrateAsync"/> as a fallback: when the primary
    /// <c>SearchByCustomerNumberAsync</c> on the padded form returns null, the
    /// search is retried with the un-padded alias. This handles legacy customers that
    /// were created in Settl before the pad-9 rule (<c>CustomerNumberPolicy.PadForEnv</c>)
    /// was introduced — without the fallback, padded lookups would miss them and
    /// CreateRunner would attempt to create duplicates.
    /// Null disables the fallback entirely.
    /// </summary>
    IReadOnlyDictionary<string, string>? LegacyCustomerNumbers = null,
    /// <summary>
    /// Padded PayerIdentifier → original un-padded identifier. Used by Step 2 of
    /// <see cref="ISettlClient.MigrateAsync"/> as a fallback: when the primary
    /// <c>SearchByPayerIdentificationAsync</c> on the padded form returns null, the
    /// search is retried with the un-padded alias. This handles legacy payers that
    /// were created in Settl before the pad-9 rule (<c>CustomerNumberPolicy.PadForEnv</c>)
    /// was introduced — without the fallback, padded lookups would miss them and
    /// CreateRunner would attempt to create duplicates.
    /// Null disables the fallback entirely.
    /// </summary>
    IReadOnlyDictionary<string, string>? LegacyPayerIdentifiers = null,
    /// <summary>
    /// Offline dry-run: the caller has swapped in no-op Settl clients so no API calls are made
    /// and every search returns not-found. This flag tells <see cref="ISettlClient.MigrateAsync"/>
    /// that a not-found water/heat MP must be reported as would-create rather than as an error —
    /// the usual "must be pre-populated" error is only meaningful when real Settl is reachable.
    /// Electricity and normal (online) dry-run are unaffected: a genuinely missing water MP in an
    /// online dry-run still surfaces the error. Implies <see cref="DryRun"/>.
    /// </summary>
    bool Offline = false,
    /// <summary>
    /// DAR address enrichment on electricity MP create (v4): Settl fills in municipality code,
    /// street code and DAR identifier when they can be derived from the address we send.
    /// On by default — a run only turns it off to send the address exactly as extracted.
    /// </summary>
    bool EnrichAddress = true,
    /// <summary>
    /// Let an electricity MP be created even when no DAR entry matches the address exactly.
    /// Off by default, so a mismatched address fails loudly instead of landing unvalidated.
    /// Settl only validates in featuretest today; production accepts either setting.
    /// </summary>
    bool SkipAddressValidation = false,
    /// <summary>
    /// How many electricity metering points go into one v4 create call in
    /// <see cref="ISettlClient.PrecreateMeteringPointsAsync"/>. The whole point of the batch
    /// is that its members share ONE read-back pause instead of paying a second each, so a
    /// bigger batch is faster — until Settl rejects it for size. A rejected batch is halved
    /// and retried down to single MPs, so an over-large value costs time, not correctness.
    /// The default is a deliberately safe starting point; measure on featuretest and set the
    /// number that actually holds. Values below 1 are treated as 1.
    /// </summary>
    int MeteringPointCreateBatchSize = 100,
    /// <summary>
    /// How many metering-point lookups may be in flight at once — the existence check before a
    /// create and the read-back after one. Both are pure reads with no order between them, so
    /// running them side by side only shortens the wall clock; the results are folded back in the
    /// original order, so counts and ordering are identical to the sequential run. Turn it down if
    /// Settl answers 429; set it to 1 for the old strictly sequential behaviour. Values below 1 are
    /// treated as 1.
    /// </summary>
    int SearchConcurrency = 8,
    /// <summary>
    /// Whether <see cref="ISettlClient.PrecreateMeteringPointsAsync"/> reads its freshly created
    /// metering points back to learn their Settl GUIDs. On by default, because everything the
    /// per-customer loop does afterwards — customer link, payer link, contract link, billing,
    /// exemptions — addresses the metering point by that id, and the v4 create answers 201 with
    /// no body.
    ///
    /// <para>Turn it off when the run stops after the pre-create phase and links nothing: then the
    /// ids are never used, and the searches and the retry pauses buy nothing. With it off the phase
    /// posts the batches and stops — <c>Created</c> counts what Settl accepted (the 201) rather than
    /// what a search could confirm, and <c>Unresolved</c> is 0 because nothing is left awaiting
    /// Settl. The existence check <b>before</b> the create is unaffected: it decides create-or-not
    /// and always runs.</para>
    /// </summary>
    bool ResolveCreatedMeteringPointIds = true);

/// <summary>
/// Configuration for the optional billing-exemption step. Null on the migration context
/// disables the step entirely. The qualify-decision (which MPs are exempt) is made upstream in
/// the domain (<c>MeteringPoint.BillingExemptionTemplate</c>); this only carries the Settl-side
/// application detail.
/// </summary>
public sealed record SettlBillingExemptionContext(
    Guid ProductElementId,
    string ProductElementCode,
    double Percentage);
