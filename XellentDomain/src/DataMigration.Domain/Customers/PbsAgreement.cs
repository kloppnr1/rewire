namespace DataMigration.Domain.Customers;

/// <summary>
/// Active direct-debit agreement with BetalingsService (PBS) — Danish national direct-debit
/// system. Only present when the agreement is active; cancelled agreements are not included
/// in the model.
/// </summary>
public sealed record PbsAgreement(int Number);
