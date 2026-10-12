namespace DataMigration.Domain.Common;

/// <summary>
/// A half-open date interval. <see cref="From"/> is inclusive; <see cref="To"/> is
/// exclusive. <c>null</c> on <see cref="To"/> means "still in effect" (open-ended).
///
/// <para>Used for time-varying domain concepts like Engros price components and
/// subscription fees, where rates change over time. Customer/Contract/MeteringPoint
/// are snapshot-style and do NOT use Period — they only have a single Start date.</para>
/// </summary>
public sealed record Period(DateOnly From, DateOnly? To)
{
    /// <summary>True when <paramref name="date"/> falls within the half-open interval.</summary>
    public bool Contains(DateOnly date) =>
        date >= From && (To is null || date < To);
}
