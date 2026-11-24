namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// One drift row the caller has selected to apply, identified by its natural coordinates (the same
/// fields a <see cref="ReconcileDriftRow"/> carries). No GUIDs — the applier re-resolves Settl ids
/// from these natural keys, and matches the row against a freshly-captured create-true payload by
/// (<see cref="Entity"/>, <see cref="SubType"/>, <see cref="EntityKey"/>, <see cref="Field"/>).
/// </summary>
public sealed record ReconcileApplyRow(
    string Entity,      // Customer | Payer | MeteringPoint
    string SubType,     // "" | Billing | Exemption | PriceList | ...
    string EntityKey,   // CustomerNumber / PayerIdentifier / MP identification
    string Field,       // property path, e.g. PayerBilling.OrderIdentifier / PaymentConfiguration.ElectronicNumber
    string Kind,        // FieldDiff | MissingInSettl | ...
    // Structured create-true intended for this row's write unit, carried verbatim from the reconcile
    // scan (ReconcileDriftRow.IntendedJson). When present the applier writes it directly — no scoped
    // capture. Null → applier falls back to the per-target capture (PriceList/SettlementGroup rows,
    // or a pre-IntendedJson cache/CSV).
    string? IntendedJson = null);

/// <summary>
/// All selected rows under a single representative, plus the context needed to rebuild that
/// representative's create-true intended payload <i>without</i> an env-wide scan: the merge-group's
/// Xellent accounts and the representative's change-of-supplier date.
/// <para>When every selected row carries its own <see cref="ReconcileApplyRow.IntendedJson"/> (the
/// scan already serialized the intended write unit), the applier writes straight from those and skips
/// the capture entirely. The scoped capture (<c>refDate = Cos - 1</c> over <see cref="Accounts"/>) is
/// the fallback for rows without carried intended (PriceList/SettlementGroup, or a pre-IntendedJson
/// cache); it reproduces the same representative + intended values the env-wide reconcile produced.</para>
/// </summary>
public sealed record ReconcileApplyTarget(
    string RepresentativeAccount,
    IReadOnlyList<string> Accounts,   // the merge-group's Xellent accountnums incl. the representative (= row.MemberAccounts)
    DateOnly Cos,                     // representative's min COS; refDate = Cos.AddDays(-1)
    IReadOnlyList<ReconcileApplyRow> Rows);

/// <summary>A reconcile-apply request: which representatives' selected rows to write, to which env.</summary>
public sealed record ReconcileApplyRequest(
    string? Env,
    bool DryRun,
    IReadOnlyList<ReconcileApplyTarget> Targets,
    // Where to write the PriceList re-import file when PriceList rows are selected. Null → the applier
    // writes a timestamped file in the current directory. PriceList associations have no live write
    // API, so this artifact is re-imported through the existing pipeline (not a Settl mutation).
    string? PriceListOutputPath = null);

/// <summary>Per-row apply outcome.</summary>
public enum ApplyOutcome
{
    /// <summary>Written to Settl.</summary>
    Applied,
    /// <summary>Dry-run (or production): would have written, but did not.</summary>
    WouldApply,
    /// <summary>Re-validation found the field already matches intended (Settl changed since the scan) — skipped.</summary>
    NoLongerDrifts,
    /// <summary>The entity could not be resolved in Settl (not migrated / wrong key).</summary>
    NotFound,
    /// <summary>Row is outside phase-1 apply scope (unsupported entity/subtype/kind) — skipped.</summary>
    Skipped,
    /// <summary>Write attempted but failed.</summary>
    Failed,
}

/// <summary>Outcome of one selected row.</summary>
public sealed record ReconcileApplyRowResult(
    string RepresentativeAccount,
    string Entity,
    string SubType,
    string EntityKey,
    string Field,
    ApplyOutcome Outcome,
    string? Detail = null);

/// <summary>
/// Result of a reconcile-apply run. <see cref="PriceListCsvPath"/> is set when PriceList rows were
/// in scope and an import file was emitted (PriceList associations have no live write API — they
/// re-import through the existing pipeline).
/// </summary>
public sealed record ReconcileApplyResult(
    IReadOnlyList<ReconcileApplyRowResult> Rows,
    string? PriceListCsvPath = null)
{
    public int Applied => Rows.Count(r => r.Outcome == ApplyOutcome.Applied);
    public int WouldApply => Rows.Count(r => r.Outcome == ApplyOutcome.WouldApply);
    public int NoLongerDrifts => Rows.Count(r => r.Outcome == ApplyOutcome.NoLongerDrifts);
    public int NotFound => Rows.Count(r => r.Outcome == ApplyOutcome.NotFound);
    public int Skipped => Rows.Count(r => r.Outcome == ApplyOutcome.Skipped);
    public int Failed => Rows.Count(r => r.Outcome == ApplyOutcome.Failed);
}
