using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;

namespace DataMigration.SettlClient;

/// <summary>
/// Settl destination — translates Domain types into Settl-API mutations and orchestrates
/// the customer/payer/metering-point/contract-link pipeline. Implementations are
/// idempotent: re-running for a customer that's already been migrated returns the same
/// Guids and skips create-steps that already succeeded.
///
/// <para>This interface is the only public surface; all DTO-construction and channel-
/// routing logic is internal to the implementation. Callers (e.g. <c>CreateRunner</c> in
/// DataMigration) should not see Settl DTOs — they pass <see cref="Customer"/> instances
/// produced by a loader (e.g. <c>XellentDomainMapper</c>).</para>
/// </summary>
public interface ISettlClient
{
    /// <summary>
    /// Migrate one customer bundle (customer + payer(s) + metering-points + contract-links)
    /// to Settl. The implementation performs search-or-create per entity, reusing existing
    /// Guids when possible.
    /// </summary>
    /// <param name="customer">The customer to migrate.</param>
    /// <param name="meteringPoints">
    /// All metering points for this batch, keyed by <see cref="MeteringPoint.Identification"/>.
    /// The implementation resolves which MPs belong to <paramref name="customer"/> via
    /// <see cref="Contract.MeteringPoints"/>.
    /// </param>
    /// <param name="context">Per-batch context (contract templates, billing exemptions, etc.).</param>
    Task<SettlMigrationResult> MigrateAsync(
        Customer customer,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        SettlMigrationContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates every electricity metering point in the run <b>up front, in batches</b>, before the
    /// caller starts its per-customer loop. Optional: skip it and <see cref="MigrateAsync"/>
    /// behaves exactly as it always has, creating each MP inside its customer's bundle.
    ///
    /// <para><b>Why.</b> Settl is not immediately consistent: the first search after a create never
    /// hits, so every single-MP create pays its own retry pause. Batched, all the MPs in one batch
    /// share a single pause. The second gain is timing — every metering-point failure is known
    /// before the first customer is written, instead of surfacing halfway through the portfolio.</para>
    ///
    /// <para>A batch Settl rejects is halved and retried down to single MPs, so the failure is
    /// pinned to the exact metering point — and an over-large batch size degrades to slower, never
    /// to wrong. The GUIDs are kept on the implementation instance and picked up by the following
    /// <see cref="MigrateAsync"/> calls; an MP the phase could not resolve simply falls back to the
    /// per-customer path, so a failure here never becomes a silent omission.</para>
    ///
    /// <para>Dry-run does nothing here (the read-caches already answer without calls) and
    /// water/heat is untouched — those are searched, never created.</para>
    ///
    /// <para>A run that stops after this phase and links nothing can set
    /// <c>SettlMigrationContext.ResolveCreatedMeteringPointIds</c> to false: the batches are posted
    /// and nothing is read back, since the GUIDs would only be thrown away. The existence check
    /// before the create still runs — it decides create-or-not.</para>
    /// </summary>
    /// <param name="customers">
    /// The customers about to be migrated, in the order they will be migrated, and in the <b>same
    /// shape</b> (e.g. already padded) they will be passed to <see cref="MigrateAsync"/> — the MP
    /// payload is built from its owning customer. An MP hanging under several accounts is built
    /// from the first one that claims it.
    /// </param>
    /// <param name="meteringPoints">All MPs for the batch, keyed by identification.</param>
    /// <param name="context">The same context instance the per-customer calls will get.</param>
    /// <param name="onBatchProgress">
    /// Optional progress callback — <c>(settled, total)</c>. <c>total</c> is the number of MPs the
    /// phase set out to handle (the same number it logs up front); <c>settled</c> is created +
    /// already existing + failed, i.e. exactly what the closing summary counts, so the bar and the
    /// log agree. Called once with <c>(0, total)</c> before the existence check, once when the
    /// existence check has settled the MPs that were already in Settl, and once after each batch.
    /// Never goes backwards. Not called at all when the phase does nothing (dry-run, or no
    /// electricity MPs). Purely observational: passing nothing leaves behaviour unchanged.
    /// </param>
    Task<SettlMeteringPointPrecreateResult> PrecreateMeteringPointsAsync(
        IReadOnlyList<Customer> customers,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        SettlMigrationContext context,
        CancellationToken cancellationToken,
        Action<int, int>? onBatchProgress = null);

    /// <summary>
    /// Capture-mode (read-only): builds the <b>full</b> intended create-payload for one
    /// customer bundle — customer + payer(s) + MP master + MP billing + exemptions +
    /// customer/payer/contract→MP links — <b>without any writes and independently of existing
    /// Settl state</b>. Reuses the same builder helpers as <see cref="MigrateAsync"/>, so the
    /// captured payload equals what create would POST for a fresh bundle. Grid UUIDs are
    /// resolved via a read-only lookup. Used by the DataMigration <c>reconcile</c> command to
    /// diff intended-vs-actual property-for-property.
    /// </summary>
    Task<SettlMigrationPlan> CapturePlanAsync(
        Customer customer,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        SettlMigrationContext context,
        CancellationToken cancellationToken);
}
