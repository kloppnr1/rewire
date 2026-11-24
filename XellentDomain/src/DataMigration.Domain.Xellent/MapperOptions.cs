using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Configuration for <see cref="XellentDomainMapper"/>.
/// </summary>
/// <param name="ReferenceDate">Point-in-time the extraction is for. Periods are filtered against this date.</param>
/// <param name="Commodity">Which commodity to extract for. Each call returns one commodity-slice.</param>
/// <param name="IncludeEndedCutoff">When set, contracts that ended after this date are also included even if past <paramref name="ReferenceDate"/>.</param>
/// <param name="ProductCodeFilter">When set, only contract lines with this product code are included.</param>
/// <param name="IgnoredGroupNumbers">TechnicGroup4 values that should NOT trigger a representative-merge (treated as ungrouped).</param>
/// <param name="AdditionalAccounts">Account numbers that bypass the <paramref name="ProductCodeFilter"/>.</param>
/// <param name="Anonymize">When true, contact info (phone, email, CPR) is replaced with placeholder values.</param>
/// <param name="OverrideEmail">When set, every customer's email is replaced with this value.</param>
/// <param name="LegacyTg4Merge">When true, representative-merge uses the legacy CVR+TG4 rule
/// instead of CVR+Fingerprint. Required for branch-style profiles (PPA, Spar Nord) where
/// addresses differ across merged accounts but they should still consolidate.</param>
/// <param name="DisableRepresentativeMerge">When true, no CVR+fingerprint merge happens: every
/// Xellent Accountnum becomes its own Settl customer. Intended for whole-category runs that migrate
/// the account set in independent batches, where a merge group split across batches would produce
/// separate Settl customers anyway — making a per-batch merge depend on the batch layout. Profile-based
/// runs must leave this false. Overrides <paramref name="LegacyTg4Merge"/>.</param>
/// <param name="BillingExemptionTemplateSuffix">When set, a metering point whose active contract
/// template (<c>Contracttemplatenum</c>) ends with this suffix is marked as billing-fee-exempt via
/// <see cref="DataMigration.Domain.MeteringPoints.MeteringPoint.BillingExemptionTemplate"/>. Null/empty
/// disables the marking. The Settl-specific application (which product element, what percentage)
/// stays with the migration consumer — only the qualify-decision lives here.</param>
/// <param name="RestorePartsChangedFrom">Start of the restore window (inclusive). See
/// <paramref name="RestorePartsChangedBefore"/>.</param>
/// <param name="RestorePartsChangedBefore">End of the restore window (exclusive). Set together with
/// <paramref name="RestorePartsChangedFrom"/>, contract parts are read as they were before the
/// unattended Xellent batch that ran on 2026-08-31: every <c>EXU_CONTRACTPARTLOG</c> change logged
/// inside the window by a blank user is undone at extraction time. The batch set an end date on
/// ~49.300 parts in dataarea <c>hol</c> (typically open-ended 1900-01-01 → 2026-08-31) and deleted
/// 2.097 future parts outright, so a run sees contracts that are already over: it either migrates dead
/// contract links or refuses the account at the missing-contract guard.
///
/// <para>Both null (the default) is off — nothing is read from the log and the extraction behaves
/// exactly as before. Setting only one of them is a configuration error and throws when the window is
/// read.</para>
///
/// <para><b>The window is bounded on purpose, and it is always the caller's.</b> It pins the restore
/// to exactly one known batch run: an open-ended "everything since" would also undo a legitimate
/// change made later the same day, or a week later. Nothing about a particular incident is baked into
/// this library. The 2026-08-31 incident ran as a ~46 minute burst whose first logged row is
/// 09:13:49.000 and last is 09:59:32.000, so its window is
/// <c>[2026-08-31 09:13:49.000, 2026-08-31 09:59:33.000)</c> — that is the shape to supply, with the
/// upper bound one tick past the last row so it is taken in.</para>
///
/// <para><b>This is a bridge, not a repair.</b> The data in Xellent stays wrong while the option is
/// on; only what the extraction sees is corrected. Xellent must be fixed at source, and this option
/// removed once it is.</para>
///
/// <para>Changes logged by a named user are never undone — a person's same-day decision stands. Only
/// end-date changes and deletions are restored; a start-date change moves the key itself and is
/// reported as a warning instead.</para></param>
public sealed record MapperOptions(
    DateOnly ReferenceDate,
    Commodity Commodity,
    DateOnly? IncludeEndedCutoff = null,
    string? ProductCodeFilter = null,
    IReadOnlySet<string>? IgnoredGroupNumbers = null,
    IReadOnlySet<string>? AdditionalAccounts = null,
    bool Anonymize = false,
    string? OverrideEmail = null,
    bool LegacyTg4Merge = false,
    string? BillingExemptionTemplateSuffix = null,
    bool DisableRepresentativeMerge = false,
    DateTime? RestorePartsChangedFrom = null,
    DateTime? RestorePartsChangedBefore = null)
{
    /// <summary>
    /// The half-open restore window <c>[From, Before)</c>, or null when the restore is off.
    /// Throws when only one bound is set, or when the bounds are not in order — a half-configured
    /// window has no safe reading.
    /// </summary>
    public (DateTime From, DateTime Before)? RestorePartsWindow
    {
        get
        {
            if (RestorePartsChangedFrom is null && RestorePartsChangedBefore is null) return null;
            if (RestorePartsChangedFrom is not { } from || RestorePartsChangedBefore is not { } before)
                throw new InvalidOperationException(
                    "RestorePartsChangedFrom and RestorePartsChangedBefore must be set together: " +
                    "the contract-part restore runs over a bounded window, never open-ended.");
            if (from >= before)
                throw new InvalidOperationException(
                    $"Restore window is empty or inverted: from {from:O} is not before {before:O}.");
            return (from, before);
        }
    }

    /// <summary>
    /// The end-date cutoff for <b>contract parts</b>: the earlier of <see cref="IncludeEndedCutoff"/>
    /// and the start of <see cref="RestorePartsWindow"/>, or null when neither is set.
    ///
    /// <para>Restoring a part only works if the part row reaches memory in the first place. The batch
    /// wrote end dates in the past, so the ordinary part predicate filters those rows out in SQL before
    /// anything can be put back. Widening the <i>part</i> cutoff back to the batch date lets them
    /// through; the restored end date is then applied and the ordinary predicate re-run in memory
    /// against <see cref="IncludeEndedCutoff"/>, so a part that was genuinely expired drops out again.</para>
    ///
    /// <para><b>Only part-level end-date predicates may read this. Header predicates
    /// (<c>Contractenddate</c>) keep reading <see cref="IncludeEndedCutoff"/>.</b> The two properties
    /// are named apart on purpose: a header filter widened by this value would drag genuinely
    /// terminated contracts into the run and create real customers in Settl for them. If you are
    /// writing <c>Contractenddate &gt;= …</c>, the answer is <see cref="IncludeEndedCutoff"/>.</para>
    /// </summary>
    public DateOnly? IncludeEndedCutoffForParts
    {
        get
        {
            var restoreCutoff = RestorePartsWindow is { } window ? DateOnly.FromDateTime(window.From) : (DateOnly?)null;
            if (IncludeEndedCutoff is null) return restoreCutoff;
            if (restoreCutoff is null) return IncludeEndedCutoff;
            return IncludeEndedCutoff.Value < restoreCutoff.Value ? IncludeEndedCutoff : restoreCutoff;
        }
    }

    /// <summary>
    /// False (the default) leaves the expected yearly consumption behind
    /// <see cref="DataMigration.Domain.MeteringPoints.MeteringPoint.EstimatedAnnualVolumeKwh"/>
    /// unread: the query is never issued and the value comes out null. True reads it.
    ///
    /// <para>Off by default because the value does not survive. Once delivery activates, the market
    /// overwrites <c>EstimatedAnnualVolume</c> in Settl, so a migrated value is replaced anyway —
    /// and it is not cheap to produce. The lookup is the most expensive read in the metering-point
    /// map: its only filter is the delpoint number, which is a sequence number under an attachment
    /// rather than a key, so it scans millions of reading rows to bring back a value for a few dozen
    /// metering points — measured at 11–13 s of a 17 s load. Paying that on every load, for every
    /// consumer, to produce something the market discards, is the wrong default.</para>
    ///
    /// <para>Turn it on where the value is the point in itself rather than something being migrated.
    /// The known case is the EAV Excel export in DataMigration (<c>ExportEavExcelRunner</c>), whose
    /// whole job is to report the expected yearly consumption — for it, null columns are the bug.
    /// Any new caller that reads <c>EstimatedAnnualVolumeKwh</c> and cares about the answer belongs
    /// in the same category and must ask for it explicitly.</para>
    ///
    /// <para><b>This flag covers that one lookup, and it stays that way.</b> The two other
    /// delpoint-keyed lookups in the same map — the billing-method row behind the settlement method,
    /// and the reading interval — are not skippable. Settlement method falls back to deriving from
    /// the meter type code when the billing-method row is missing, so skipping that query would not
    /// leave the value empty; it would silently produce a <i>different</i> value on every metering
    /// point. Reading interval is the same shape of trap.</para>
    /// </summary>
    public bool IncludeEstimatedAnnualVolume { get; init; }
}
