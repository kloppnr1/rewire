namespace DataMigration.OpsCli.Services;

/// <summary>
/// Gives the metering points that produced no schedule row a segment answer anyway. Pure — the Settl
/// lookups happen in the runner — so the rule itself is unit-testable.
///
/// <para>These metering points never reach the template rule: there is no Xellent contract at
/// change-of-supplier to read a <c>Contracttemplatenum</c> from, which is exactly why they are
/// unresolved. The answer therefore comes from the config, keyed by the reason
/// (<see cref="SettlementConfigUnresolvedReason"/>) and by what Settl says the customer is — a
/// company or a private consumer.</para>
///
/// <para><b>The customer's own segment wins.</b> An unresolved metering point can belong to a customer
/// that is already placed by its other, resolved metering points. Letting the standing answer overrule
/// that would move a customer out of a segment its own data decided, on the strength of one metering
/// point we know nothing about — so the existing segment is reported instead, marked as the customer's
/// own.</para>
/// </summary>
public static class UnresolvedSegmentResolver
{
    /// <summary>What Settl says about the customer behind an unresolved metering point.</summary>
    public sealed record UnresolvedCustomer(
        string CustomerNumber,
        string? Name,
        CustomerIdentityKind Identity);

    /// <param name="customerByMpId">Metering point identification → the Settl customer holding it.
    /// Null or missing means the lookup did not run or found nothing; the row then keeps whatever the
    /// reason alone can answer.</param>
    /// <param name="segmentByCustomer">Customer number → the segment the customer already landed in.</param>
    /// <param name="rules">Standing answers per reason, from <c>settlement-config.json</c>. Null leaves
    /// every row exactly as it was.</param>
    public static IReadOnlyList<SettlementConfigUnresolved> Apply(
        IReadOnlyList<SettlementConfigUnresolved> unresolved,
        IReadOnlyDictionary<string, UnresolvedCustomer>? customerByMpId,
        IReadOnlyDictionary<string, string>? segmentByCustomer,
        IReadOnlyDictionary<SettlementConfigUnresolvedReason, SegmentRule>? rules)
    {
        if (customerByMpId is null && rules is null) return unresolved;

        return [.. unresolved.Select(row =>
        {
            var customer = customerByMpId is not null
                && customerByMpId.TryGetValue(row.MeteringPointIdentification, out var found)
                ? found
                : null;

            if (customer is not null
                && segmentByCustomer is not null
                && segmentByCustomer.TryGetValue(customer.CustomerNumber, out var own))
            {
                return row with
                {
                    CustomerNumber = customer.CustomerNumber,
                    CustomerName = customer.Name,
                    Identity = customer.Identity,
                    Segment = own,
                    CustomerHasOwnSegment = true
                };
            }

            // Unknown customer falls to the blank column rather than to no answer at all: the file
            // decides what an unidentified consumer gets, the same way it does for a customer Xellent
            // holds no CVR or CPR for.
            var identity = customer?.Identity ?? CustomerIdentityKind.Blank;
            var candidates = rules is not null && rules.TryGetValue(row.Reason, out var rule)
                ? rule.For(identity)
                : [];

            return row with
            {
                CustomerNumber = customer?.CustomerNumber,
                CustomerName = customer?.Name,
                Identity = customer?.Identity,
                // Several candidates is no answer — same as everywhere else in this export, an
                // ambiguous rule is reported as undecided rather than resolved by picking the first.
                Segment = candidates.Count == 1 ? candidates[0] : null
            };
        })];
    }
}
