namespace DataMigration.OpsCli.Services;

/// <summary>Actual work in the current export stage. Null totals mean the source has no known total.
/// Done includes completed reads that report missing data or failures; it is not a success count.</summary>
public sealed record SettlementConfigProgress(string Stage, int? Done = null, int? Total = null, string? Unit = null);
