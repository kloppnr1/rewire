using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlClient;

namespace DataMigration.Migration.Services;

/// <summary>How one account came out of the metering-point phase.</summary>
public enum MeteringPointAccountResult
{
    /// <summary>
    /// The phase has no answer for at least one of the account's metering points — either it
    /// stopped before reaching it, or the metering point does not appear in its answer at all.
    /// Says nothing either way, so the account has to come round again.
    /// </summary>
    NotAttempted = 0,

    /// <summary>Every one of the account's metering points is in Settl — created now or found
    /// already there. The two mean the same thing here: nothing is missing on the account's
    /// behalf.</summary>
    Landed = 1,

    /// <summary>At least one of the account's metering points was refused by Settl for a real
    /// reason. The account will not finish on its own — somebody has to look at it.</summary>
    Failed = 2
}

/// <summary>
/// One of the account's metering points, and how the phase answered about it. The three answers are
/// the same three an account can get, one level down: it landed, it was refused, or nothing came
/// back about it.
/// </summary>
/// <param name="MeteringPointIdentification">The 18-digit national metering point id — the same one
/// Settl is searched by, so a caller can look the metering point up without knowing the customer.
/// That matters on a metering-points-only run, where there is no Settl customer to ask.</param>
/// <param name="Result">
/// <see cref="MeteringPointAccountResult.Landed"/> when the metering point is in Settl — created now
/// or found already there. <see cref="MeteringPointAccountResult.Failed"/> when Settl refused it.
/// <see cref="MeteringPointAccountResult.NotAttempted"/> when the phase never reached it, or never
/// mentioned it at all.
/// </param>
public sealed record MeteringPointAccountLine(
    string MeteringPointIdentification,
    MeteringPointAccountResult Result);

/// <summary>
/// The metering-point phase's answers, folded up to the account somebody has to book. The phase
/// answers per metering point, and a metering-points-only run reports no customer outcomes at all
/// — so a caller keeping score has only "did the whole run go through" to go on, and one refused
/// metering point costs every other account in the batch its bookkeeping.
/// </summary>
/// <param name="Account">
/// The account number in the RAW Xellent form the domain carries. Note the difference from
/// <c>CustomerOutcome.RepresentativeAccount</c>, which is the padded form Settl uses.
/// </param>
/// <param name="MeteringPointCount">The account's metering points within this run's scope.</param>
/// <param name="LandedCount">How many of them are in Settl.</param>
/// <param name="FailedMeteringPointIdentification">
/// The first refused metering point, so a failure can be shown together with what it is about.
/// Null when the account did not fail.
/// </param>
/// <param name="Error">Settl's answer to the refusal, verbatim. Null when the account did not
/// fail.</param>
/// <param name="MeteringPoints">
/// Which metering points the counts are about, in the order the fold walked them — contract order,
/// first mention of a metering point wins. Counts alone are enough to book the account, but not to
/// go and look at it: a metering-points-only run creates no Settl customer, so there is nothing to
/// ask for the account's metering points afterwards. The list is that answer, kept from the moment
/// it was known. Never null out of the fold; null from an older caller that constructs the outcome
/// without it, and then it means the same as empty.
/// </param>
public sealed record MeteringPointAccountOutcome(
    string Account,
    MeteringPointAccountResult Result,
    int MeteringPointCount,
    int LandedCount,
    string? FailedMeteringPointIdentification = null,
    string? Error = null,
    IReadOnlyList<MeteringPointAccountLine>? MeteringPoints = null);

/// <summary>
/// Folds the metering-point phase's outcomes up to accounts. A pure function — it only reads what
/// the phase has already answered, and changes nothing about how metering points are extracted,
/// created, batched or ordered.
/// </summary>
public static class MeteringPointAccountOutcomeFolder
{
    /// <summary>
    /// One outcome per account, in the order the customers came.
    ///
    /// <para>The rule, in the order it is decided: the account <b>failed</b> if at least one of
    /// its metering points was refused — that reason has to surface, even if the phase stayed
    /// silent about one of the others. Otherwise it <b>landed</b>, if every single metering point
    /// is in Settl. Otherwise it is <b>not attempted</b>: a metering point the phase did not reach,
    /// or does not mention at all, is not a metering point that made it.</para>
    ///
    /// <para>"No answer" and "landed" must never collapse into one another — that collapse is the
    /// whole bug being fixed here. When it cannot be told, the answer is not attempted: the caller
    /// then leaves the account alone and it comes round again.</para>
    /// </summary>
    /// <param name="customers">The customers the phase got, in the order it took them.</param>
    /// <param name="meteringPoints">The run's metering points, keyed by identification.</param>
    /// <param name="outcomes">
    /// <c>SettlMeteringPointPrecreateResult.MeteringPoints</c>. Empty means the phase created and
    /// found nothing — a dry run, or a run with no electricity metering points at all. Every
    /// account still gets an outcome: one with metering points in view comes out not attempted
    /// with a count, so nobody books it, and one with nothing in view comes out not attempted with
    /// a count of zero, which is the account saying there was nothing here to do.
    /// </param>
    public static IReadOnlyList<MeteringPointAccountOutcome> Fold(
        IReadOnlyList<Customer> customers,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        IReadOnlyList<SettlMeteringPointOutcome> outcomes)
    {
        // First owner wins — the same rule the phase itself uses, so a metering point hanging under
        // more than one account has one answer, and each of those accounts depends on that answer.
        var byId = new Dictionary<string, SettlMeteringPointOutcome>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
            byId.TryAdd(outcome.MeteringPointIdentification, outcome);

        var results = new List<MeteringPointAccountOutcome>(customers.Count);
        var mpIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var customer in customers)
        {
            mpIds.Clear();
            seen.Clear();

            foreach (var contract in customer.Contracts)
            {
                foreach (var assignment in contract.MeteringPoints)
                {
                    var mpId = assignment.MeteringPointId;
                    if (!seen.Add(mpId)) continue;
                    // The same two exclusions the phase makes: a metering point the extract does not
                    // carry is not part of the run, and water/heat are put in externally and never
                    // created here. Without them the account would wait forever for an answer that
                    // is never coming.
                    if (!meteringPoints.TryGetValue(mpId, out var mp)) continue;
                    if (mp.Grid == null) continue;
                    mpIds.Add(mpId);
                }
            }

            // No metering points in the phase's field of view: then the phase said nothing about
            // this account either. The empty set is technically covered in full, but "everything
            // succeeded" is not a claim to build on nothing.
            if (mpIds.Count == 0)
            {
                results.Add(new MeteringPointAccountOutcome(
                    customer.AccountNumber, MeteringPointAccountResult.NotAttempted, 0, 0,
                    MeteringPoints: []));
                continue;
            }

            var landed = 0;
            var unanswered = false;
            string? failedMp = null;
            string? error = null;
            // Same walk, same order: the line list is written as the answers are read, so two runs
            // of the same account hand back the same list in the same sequence.
            var lines = new List<MeteringPointAccountLine>(mpIds.Count);

            foreach (var mpId in mpIds)
            {
                if (!byId.TryGetValue(mpId, out var outcome)
                    || outcome.Status == SettlMeteringPointStatus.NotAttempted)
                {
                    unanswered = true;
                    lines.Add(new MeteringPointAccountLine(
                        mpId, MeteringPointAccountResult.NotAttempted));
                    continue;
                }

                if (outcome.Landed)
                {
                    landed++;
                    lines.Add(new MeteringPointAccountLine(mpId, MeteringPointAccountResult.Landed));
                    continue;
                }

                lines.Add(new MeteringPointAccountLine(mpId, MeteringPointAccountResult.Failed));

                // The first refusal is the one shown. More refused metering points on the same
                // account do not change the answer, and the first one is enough to act on.
                failedMp ??= mpId;
                error ??= outcome.Error;
            }

            var result = failedMp != null ? MeteringPointAccountResult.Failed
                : unanswered ? MeteringPointAccountResult.NotAttempted
                : MeteringPointAccountResult.Landed;

            results.Add(new MeteringPointAccountOutcome(
                customer.AccountNumber, result, mpIds.Count, landed, failedMp, error, lines));
        }

        return results;
    }
}
