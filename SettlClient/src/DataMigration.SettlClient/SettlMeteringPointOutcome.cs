namespace DataMigration.SettlClient;

/// <summary>
/// What the up-front phase managed for <b>one metering point</b> — the same unit Settl answers in,
/// and the same id the batch already carries.
///
/// <para>The phase otherwise collapses everything it knows into three counters, and a
/// metering-points-only run reports no customer outcomes at all. A caller that wants to record
/// progress finer than "the whole run went through" has nothing else to go on: one refused metering
/// point costs every other account in the same batch its bookkeeping, and they are all retried on
/// the next run. Folding these outcomes up to accounts is the caller's job — it is the one holding
/// the customers and their metering points, and it can do it without guessing.</para>
/// </summary>
/// <param name="MeteringPointIdentification">
/// The 18-digit national metering point id — exactly the id the create batch uses and the failure
/// log names. Not the Settl GUID, and nothing to do with an EAN.
/// </param>
/// <param name="Status">See <see cref="SettlMeteringPointStatus"/>.</param>
/// <param name="Error">
/// Settl's answer when the create was refused, verbatim — the same text the run's error list
/// carries. Null for every other status.
/// </param>
public sealed record SettlMeteringPointOutcome(
    string MeteringPointIdentification,
    SettlMeteringPointStatus Status,
    string? Error = null)
{
    /// <summary>
    /// The metering point is in Settl. Whether this run put it there or found it already there is
    /// kept in <see cref="Status"/>, but for "is anything missing on this metering point's behalf"
    /// the two answer the same.
    /// </summary>
    public bool Landed => Status is SettlMeteringPointStatus.Created or SettlMeteringPointStatus.AlreadyExisted;
}

/// <summary>
/// How one metering point came out of the phase. "Nothing failed" and "everything succeeded" are
/// not the same statement, so a metering point the phase never got to is its own answer rather than
/// an absence a caller has to interpret.
/// </summary>
public enum SettlMeteringPointStatus
{
    /// <summary>
    /// The phase never settled this metering point — it stopped before reaching it. Says nothing
    /// either way, so a caller must not read it as done.
    /// </summary>
    NotAttempted = 0,

    /// <summary>Settl accepted the create. Counted in <c>Created</c>, or in <c>Unresolved</c> when
    /// the read-back could not find it again yet — either way Settl has it.</summary>
    Created = 1,

    /// <summary>Already in Settl — found by the lookup, or refused as a duplicate by the create,
    /// which means the same thing. Counted in <c>AlreadyExisting</c>.</summary>
    AlreadyExisted = 2,

    /// <summary>Settl refused the create for a real reason. <see cref="SettlMeteringPointOutcome.Error"/>
    /// carries its answer. Counted in <c>Failed</c>.</summary>
    Failed = 3
}
