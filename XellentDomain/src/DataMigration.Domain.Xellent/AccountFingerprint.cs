namespace DataMigration.Domain.Xellent;

/// <summary>
/// Components used to compute the merge-fingerprint for one Xellent customer, plus
/// the resulting canonical fingerprint key. Surfaces *what* makes one variation
/// different from another under the same CVR — diagnostic input for
/// <c>FingerprintSplits</c> reporting.
///
/// <para>Mirrors the inputs to <see cref="FingerprintCalculator.Compute"/>.</para>
/// </summary>
public sealed record AccountFingerprint(
    string Fingerprint,
    string? Name,
    string? AdditionalName,
    string? Street,
    string? PostalCode,
    string? City,
    string? Email = null);
