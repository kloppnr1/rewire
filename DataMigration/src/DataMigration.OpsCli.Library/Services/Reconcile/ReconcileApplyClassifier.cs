namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>Whether a reconcile drift row can be written back to Settl by <c>reconcile-apply</c>,
/// and a human-readable reason. <see cref="Applicable"/> is the single source of truth the dashboard
/// uses to show "can apply / cannot apply" per row — it MUST stay in lock-step with the dispatch switch
/// in <c>ReconcileApplier.ApplyTargetAsync</c> (the default/Skipped branch sources its reason from here).</summary>
public sealed record ReconcileApplicability(bool Applicable, string Reason);

/// <summary>
/// Pure classifier mapping a drift row's coordinates (Entity, SubType, Kind, Field) to whether
/// <c>reconcile-apply</c> has a Settl write path for it. Shared by the applier (skip-reason text) and the
/// dashboard (per-row "applicable" flag) so the two never drift — the dashboard previously hard-coded an
/// <c>isActionable()</c> rule that went stale when new drift classes were wired.
///
/// <para>"Applicable" means there is a phase-applicable write path; the actual outcome at run time can
/// still be NoLongerDrifts / NotFound / Skipped (e.g. a SettlementGroup whose group is missing in Settl),
/// since those depend on live Settl state the classifier can't see.</para>
/// </summary>
public static class ReconcileApplyClassifier
{
    public static ReconcileApplicability Classify(string entity, string subType, string kind, string field)
    {
        // Match rows carry intended == actual — nothing to write.
        if (kind == "Match")
            return new(false, "Already matches Settl — nothing to write");

        // Extra-in-Settl drift needs a delete; Settl exposes no delete for these entities — with one
        // exception: a contract→MP link CAN be removed (RemoveContractMeteringPoint), which is what makes
        // "metering point on the wrong contract" repairable. Checked before the blanket rule below.
        if (kind == "Anomaly" && (entity, subType) == ("MeteringPoint", "ContractLink"))
            return new(true, "Contract→MP link not intended — RemoveContractMeteringPoint (delete only)");
        if (kind == "Anomaly")
            return new(false, "Anomaly (extra in Settl) — no delete API; manual cleanup");
        if (kind == "MissingInXellent")
            return new(false, "Settl entity with no Xellent origin — no delete API; manual review");

        return (entity, subType, kind) switch
        {
            // --- live Settl writes ---
            ("Payer", "Billing", "FieldDiff")
                => new(true, "Payer billing — UpdatePayerBilling (flat, link-safe)"),
            ("MeteringPoint", "Billing", _)
                => new(true, "MP billing/payment config — UpdateBilling"),
            ("MeteringPoint", "Exemption", "MissingInSettl")
                => new(true, "MP exemption — AddExemptions (additive)"),
            ("Customer", "SettlementGroup", "MissingInSettl")
                => new(true, "Settlement-group membership — AddCustomers (additive, if group exists in Settl)"),
            ("MeteringPoint", "CustomerLink", "MissingInSettl")
                => new(true, "Customer→MP link — AddMeteringPoints (additive)"),

            // Annual volume is the only MP master field with a safe single-field write.
            ("MeteringPoint", "", "FieldDiff") when field == "EstimatedAnnualVolume"
                => new(true, "MP annual volume — UpdateAnnualConsumptions (volume only)"),

            // Contract→MP link drift: wrong end date (delete + re-link) or missing link (additive POST).
            ("MeteringPoint", "ContractLink", "FieldDiff")
                => new(true, "Contract→MP link end-date — delete + re-link with open-ended sentinel"),
            ("MeteringPoint", "ContractLink", "MissingInSettl")
                => new(true, "Contract→MP link missing — AddContractMeteringPoints (additive)"),

            // Customer / payer CORE fields ARE writable via a full-echo master PUT (verified live):
            // customer = wrapped v3 PUT, payer = flat v1 PUT (echoes CustomerId → link-safe). A row whose
            // field path has no counterpart on the live entity is Skipped at run time, not here.
            ("Customer", "", "FieldDiff")
                => new(true, "Customer core — full-echo v3 customer PUT"),
            ("Payer", "", "FieldDiff")
                => new(true, "Payer core — full-echo flat payer PUT (link-safe)"),

            // Whole entity absent → scoped create write-run reconciles it (idempotent).
            ("Customer" or "Payer" or "MeteringPoint", "", "MissingInSettl")
                => new(true, "Missing entity — scoped create re-run (idempotent search-or-create)"),

            // PriceList has no live write API — apply emits a re-import file instead.
            (_, "PriceList", _)
                => new(true, "PriceList → re-import file (not a live Settl write)"),

            // --- no write path ---
            ("MeteringPoint", "", "FieldDiff")
                => new(false, "MP master field has no safe single-field API"),

            _ => new(false, "Outside reconcile-apply scope"),
        };
    }

    public static ReconcileApplicability Classify(ReconcileApplyRow row)
        => Classify(row.Entity, row.SubType, row.Kind, row.Field);
}
