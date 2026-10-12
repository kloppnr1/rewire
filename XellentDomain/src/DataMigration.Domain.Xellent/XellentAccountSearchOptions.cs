namespace DataMigration.Domain.Xellent;

/// <summary>
/// Configuration for <see cref="XellentAccountSearch"/>. Mirrors the static knobs
/// shared across consumers; per-call search inputs go on <see cref="XellentAccountSearchCriteria"/>.
/// </summary>
/// <param name="DataAreaId">Xellent data area to scope all queries to.</param>
/// <param name="CommandTimeoutSeconds">Per-query timeout. Search joins can be expensive on large
/// tables; the Bifrost implementation uses 60 seconds.</param>
public sealed record XellentAccountSearchOptions(
    string DataAreaId = "hol",
    int CommandTimeoutSeconds = 60);
