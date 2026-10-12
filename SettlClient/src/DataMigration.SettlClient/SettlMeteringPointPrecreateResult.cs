namespace DataMigration.SettlClient;

/// <summary>
/// Outcome of <see cref="ISettlClient.PrecreateMeteringPointsAsync"/> — the up-front batch
/// create of every electricity metering point in the run.
///
/// <para><b>These numbers are for logging, not for the migration report.</b> Each metering
/// point is still counted once by the customer that owns it, inside
/// <see cref="ISettlClient.MigrateAsync"/>, exactly as before the phase existed — adding these
/// counters to the report as well would count every metering point twice.</para>
/// </summary>
/// <param name="Considered">Distinct electricity MPs the phase looked at (first owner wins).</param>
/// <param name="AlreadyExisting">
/// Already in Settl — either found by the lookup before the phase created anything, or turned up
/// by the create itself, which Settl answers with "already exist" for a metering point the lookup
/// could not see. Both mean the same thing to the run, so they are counted the same.
/// </param>
/// <param name="Created">
/// Created and read back with a Settl GUID — or, when the run turned the read-back off
/// (<c>SettlMigrationContext.ResolveCreatedMeteringPointIds</c> false), simply the MPs Settl
/// accepted with a 201.
/// </param>
/// <param name="Failed">
/// Create rejected by Settl for a real reason — an address it will not accept, say — isolated down
/// to the individual MP. A duplicate is not a failure; it lands in <paramref name="AlreadyExisting"/>.
/// </param>
/// <param name="Unresolved">
/// Created, but Settl had still not surfaced them when the read-back gave up. They are NOT
/// re-created by the customer loop — it only searches again. Always 0 when the read-back is
/// off, because nothing is awaiting anything.
/// </param>
/// <param name="Batches">Create calls at the configured batch size (splits not counted).</param>
/// <param name="MeteringPoints">
/// The same run, metering point by metering point instead of as four numbers — one entry per
/// metering point in <paramref name="Considered"/>, in the order the phase took them, so a caller
/// can record what actually landed rather than trusting or discarding the whole run. The counters
/// are the tally of this list: <c>AlreadyExisting</c> and <c>Failed</c> are its
/// <c>AlreadyExisted</c> and <c>Failed</c> entries, and its <c>Created</c> entries are
/// <c>Created + Unresolved</c> — a metering point Settl accepted but had not surfaced again by the
/// time the read-back gave up is still a metering point Settl has.
/// <para>Empty when the phase did nothing at all: a dry run, or a run with no electricity metering
/// points. That is an absence of information, not a run in which nothing landed.</para>
/// </param>
public sealed record SettlMeteringPointPrecreateResult(
    int Considered,
    int AlreadyExisting,
    int Created,
    int Failed,
    int Unresolved,
    int Batches,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SettlMeteringPointOutcome> MeteringPoints)
{
    public static SettlMeteringPointPrecreateResult Empty { get; } =
        new(0, 0, 0, 0, 0, 0, [], [], []);
}
