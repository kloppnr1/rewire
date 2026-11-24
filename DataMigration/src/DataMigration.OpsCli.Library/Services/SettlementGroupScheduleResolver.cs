namespace DataMigration.OpsCli.Services;

/// <summary>
/// Derives a Settlement Schedule from the Settlement Groups a customer actually belongs to in Settl.
/// Pure — the group membership is passed in, already read from Settl — so the rule is unit-testable
/// on its own.
///
/// <para><b>Membership is a fact, not an expectation.</b> The caller reads the real member list per
/// group from Settl; this resolver must never be fed the profile's <c>Mappings.SettlementGroup</c> or
/// the computed <c>SettlementGroupExpectationResolver</c>. The whole point of the comparison is to see
/// what Settl says, not what the migration intended.</para>
///
/// <para>A customer in several groups that each name a schedule is a conflict, not a pick: the two
/// schedules are equally real and choosing one would be indistinguishable in the output from a
/// customer that only ever had one. Groups the map says nothing about are ignored for the schedule
/// but reported, so a missing entry cannot look like a customer with no group.</para>
/// </summary>
public static class SettlementGroupScheduleResolver
{
    /// <param name="Schedule">The schedule, or null when no group named one.</param>
    /// <param name="Note">Why the schedule is null, or why it is ambiguous. Null when the answer
    /// needs no explanation.</param>
    public sealed record Result(string? Schedule, string? Note);

    /// <param name="settlementGroups">The groups this customer is in, as named in Settl, or null when
    /// membership was never read. Empty means "read, and a member of nothing" — collapsing the two
    /// would make a failed Settl call print a positive claim about Settl on every single customer.</param>
    /// <param name="groupSchedules">Group name → schedule, from <c>settlement-config.json</c>.</param>
    public static Result Resolve(
        IReadOnlyList<string>? settlementGroups,
        IReadOnlyDictionary<string, string>? groupSchedules)
    {
        if (groupSchedules is null || groupSchedules.Count == 0)
            return new Result(null, "groupSchedules not configured");
        if (settlementGroups is null)
            return new Result(null, "settlement-group membership not read from Settl");
        if (settlementGroups.Count == 0)
            return new Result(null, "customer is in no settlement group in Settl");

        var named = settlementGroups
            .Where(g => groupSchedules.ContainsKey(g))
            .ToList();
        if (named.Count == 0)
        {
            return new Result(
                null,
                "settlement group(s) not in groupSchedules: " + string.Join(", ", settlementGroups));
        }

        var schedules = named
            .Select(g => groupSchedules[g])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Two groups naming the same schedule is not a conflict — the answer is unambiguous even
        // though the membership is not.
        return schedules.Count == 1
            ? new Result(schedules[0], null)
            : new Result(null, "customer is in groups naming several schedules: " + string.Join(", ", schedules));
    }
}
