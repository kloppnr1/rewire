namespace DataMigration.Domain.Customers;

/// <summary>
/// When a customer is billed. <see cref="Frequency"/> is the classified concept
/// (consumers usually branch on this); <see cref="PlanCode"/> is the source-system's
/// stable plan identifier preserved for traceability and special-case handling.
/// </summary>
public sealed record BillingSchedule(BillingFrequency Frequency, string PlanCode);
