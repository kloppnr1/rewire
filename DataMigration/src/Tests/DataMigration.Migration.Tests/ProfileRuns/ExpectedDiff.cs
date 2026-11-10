namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// A known manual change made in Settl AFTER migration completed.
/// Declaring a diff here tells the test to skip it rather than fail.
/// This is NOT a workaround for bugs — it documents intentional Settl changes.
/// </summary>
/// <param name="Field">The field name as used in CompareRunner (e.g., "Email", "FullName").</param>
/// <param name="ExpectedSettlValue">The current value in Settl after the manual change.</param>
public record ExpectedDiff(string Field, string? ExpectedSettlValue);
