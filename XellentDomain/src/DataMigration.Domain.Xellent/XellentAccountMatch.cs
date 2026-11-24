namespace DataMigration.Domain.Xellent;

/// <summary>
/// One matched account from <see cref="XellentAccountSearch"/>. Includes the CustTable
/// columns + active-contract metadata that callers typically need to render search results
/// without an additional DB round-trip. Relevance fields are 0 unless the caller supplied
/// <see cref="XellentAccountSearchCriteria.StreetRelevancePattern"/> or
/// <see cref="XellentAccountSearchCriteria.DarIdForRelevance"/>.
/// </summary>
public sealed record XellentAccountMatch(
    string DataAreaId,
    string AccountNumber,
    string? Name,
    string? NameAlias,
    string? Street,
    string? City,
    string? ZipCode,
    string? CountryRegionId,
    string? Phone,
    string? CellularPhone,
    string? Email,
    string? IdentificationNumber,
    string? CompanyRegNumber,
    string? CvrDisponent1,
    string? CprDisponent1,
    string? TechnicGroup4,
    bool HasActiveContract,
    DateTime? LatestElectricityContractEndDate,
    int StreetRelevance,
    int DarIdRelevance);
