namespace DataMigration.OpsCli.Runners;

/// <summary>
/// Outcome of comparing one PaymentConfiguration field's current Settl value against the
/// value Xellent expects (the per-Payer/-MP override).
/// </summary>
public enum PaymentConfigDiffAction
{
    /// <summary>Values match, or Xellent has no expectation (blank wanted) → no action.</summary>
    None,
    /// <summary>Settl is blank, Xellent has a value → should be written.</summary>
    Backfill,
    /// <summary>Settl has a different non-blank value than Xellent → manual review.</summary>
    Mismatch
}

/// <summary>
/// Single source of truth for the PaymentConfiguration field-comparison rule shared by the
/// <c>fix-payment-config</c> mutation runner (which acts on the result) and the
/// <c>report-drift</c> / <c>report-meteringpoints</c> scans (which report it). Keeping the
/// classification here guarantees the read-only drift detection and the write path never diverge.
/// </summary>
public static class PaymentConfigDiff
{
    /// <param name="existing">Value currently in Settl.</param>
    /// <param name="wanted">Value Xellent expects (the override). Blank ⇒ no expectation.</param>
    public static PaymentConfigDiffAction Classify(string? existing, string? wanted)
    {
        if (string.IsNullOrEmpty(wanted)) return PaymentConfigDiffAction.None;
        if (string.IsNullOrEmpty(existing)) return PaymentConfigDiffAction.Backfill;
        return string.Equals(existing, wanted, StringComparison.Ordinal)
            ? PaymentConfigDiffAction.None
            : PaymentConfigDiffAction.Mismatch;
    }
}
