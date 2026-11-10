using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// The one definition of "is this contract part in the run's date window". The mappers push the same
/// rule into SQL, and the restore path has to re-apply it in memory after it rewrites end dates —
/// two places that must not drift, so the rule lives here and both sides call it.
///
/// <para>The commodity decides one thing only: whether a future start date disqualifies the part.
/// Electricity has no start cap (Settl holds future electricity contracts too); installation-based
/// commodities — water, heat, EV charging — must be active on or before the reference date, because
/// a future part has no pre-populated Settl metering point to link to.</para>
/// </summary>
internal static class ContractPartInclusion
{
    private static readonly DateTime EndDateNull = MigrationConstants.EndDateNull;

    /// <summary>Whether a future start date disqualifies a part for this commodity.</summary>
    internal static bool CapsStartDate(Commodity commodity) => commodity != Commodity.Electricity;

    /// <summary>
    /// The end-date half of the rule: open-ended, still running at <paramref name="today"/>, or ended
    /// inside the include-ended window.
    /// </summary>
    internal static bool EndDateIncluded(DateTime enddate, DateTime today, DateTime? includeEndedCutoff)
        => enddate == EndDateNull
            || enddate >= today
            || (includeEndedCutoff.HasValue && enddate >= includeEndedCutoff.Value);

    /// <summary>
    /// The whole rule for a part of <paramref name="commodity"/>.
    /// <paramref name="includeEndedCutoff"/> is the <b>header-level</b> cutoff
    /// (<see cref="MapperOptions.IncludeEndedCutoff"/>), never the widened part cutoff — this is the
    /// predicate a restored part has to survive to stay in the run.
    /// </summary>
    internal static bool Included(
        Commodity commodity, DateTime startdate, DateTime enddate, DateTime today, DateTime? includeEndedCutoff)
        => (!CapsStartDate(commodity) || startdate <= today)
            && EndDateIncluded(enddate, today, includeEndedCutoff);
}
