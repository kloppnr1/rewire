namespace DataMigration.OpsCli.Services;

/// <summary>
/// The Settl-driven segment rule, read entirely off what is in Settl today plus the customer's
/// identification:
///
/// <list type="number">
/// <item>No CVR → no segment. The rule only speaks about companies.</item>
/// <item>Any metering point on one of the SME contracts → the SME segment.</item>
/// <item>Any metering point with a billing exemption in Settl → the exempt business segment.</item>
/// <item>Otherwise → the plain business segment.</item>
/// </list>
///
/// <para>SME is checked before exemption because an SME customer with an exemption is still SME;
/// the reverse order would silently reclassify them.</para>
///
/// <para><b>Unread is not absent.</b> Both inputs this rule needs can be missing for reasons that
/// have nothing to do with the customer: exemptions cost one Settl call per metering point and are
/// opt-in, and the contract listing can simply fail. In either case the rule refuses to answer
/// instead of falling through to the plain business segment. A default there would write customers
/// into a segment the data never supported — and, worse, the comparison would count that invented
/// value as evidence when tallying agreement.</para>
/// </summary>
public static class SettlDrivenSegmentResolver
{
    /// <param name="Segment">The segment, or null when the rule does not apply or cannot decide.</param>
    /// <param name="Sme">Whether the customer sits on an SME contract — kept separately because it is
    /// evidence a human will want even when the segment is undecided. False when contracts were not
    /// read, which <paramref name="Note"/> then explains.</param>
    public sealed record Result(string? Segment, bool Sme, string? Note);

    /// <param name="settlContracts">Settl contract names across the customer's metering points, or
    /// null when the contract listing was never read. Empty means "read, and on no contract" — the
    /// two must not collapse, or a failed listing silently turns every SME customer into Erhverv.</param>
    /// <param name="hasExemption">True/false when exemptions were read, null when they were not.</param>
    /// <summary>
    /// The customer-level view of what the rule needs, gathered from her metering points. Shared by
    /// every caller so the two inputs are aggregated the same way wherever the rule is asked.
    /// </summary>
    /// <param name="settlContractsRead">False when the contract listing was never read, which makes
    /// the contract list null rather than empty — "not read" and "on no contract" must not collapse.</param>
    public static (IReadOnlyList<string>? SettlContracts, bool? HasExemption) Gather(
        IReadOnlyCollection<SettlementConfigEntry> customerEntries,
        bool settlContractsRead)
    {
        IReadOnlyList<string>? settlContracts = settlContractsRead
            ? [.. customerEntries
                .SelectMany(e => e.SettlContracts ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.Ordinal)]
            : null;

        // Any-of, not all-of: one exempt metering point is what the rule turns on. Unknown as soon as
        // a single metering point went unread, unless another one already answered "yes".
        var flags = customerEntries.Select(e => e.HasExemption).ToList();
        bool? hasExemption = flags.Any(f => f == true) ? true
            : flags.Any(f => f is null) ? null
            : false;

        return (settlContracts, hasExemption);
    }

    public static Result Resolve(
        CustomerIdentityKind identity,
        IReadOnlyList<string>? settlContracts,
        bool? hasExemption,
        SettlSegmentRules? rules)
    {
        if (rules is null) return new Result(null, false, "settlSegments not configured");
        if (settlContracts is null)
            return new Result(null, false, "Settl contracts not read — cannot test for an SME contract");

        var sme = settlContracts.Any(c => rules.SmeContracts.Contains(c, StringComparer.OrdinalIgnoreCase));

        // Identification is checked after the SME flag so the evidence survives for non-CVR
        // customers too — an SME contract on a private customer is worth seeing, not hiding.
        if (identity != CustomerIdentityKind.Cvr)
            return new Result(null, sme, "no CVR — the Settl rule only covers companies");

        if (sme) return new Result(rules.Sme, true, null);

        return hasExemption switch
        {
            true => new Result(rules.BusinessExempt, false, null),
            false => new Result(rules.Business, false, null),
            null => new Result(null, false, "exemptions not read — cannot tell "
                + $"{rules.Business} from {rules.BusinessExempt} (use --with-exemptions)")
        };
    }
}
