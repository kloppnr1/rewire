namespace DataMigration.SettlApi.Aggregates;

/// <summary>
/// Aggregated view of a Settl contract line with resolved catalog names.
/// Numeric price fields use <c>double?</c> matching <see cref="Models.PriceUnit.Value"/>.
/// </summary>
public record ContractLineView(
    string? ProductName,
    string? ProductElementCode,
    string? PriceListName,
    string? Category,
    string? Start,
    string? End,
    string? LastUpdated,
    string? Text,
    double? UnitPrice,
    string? Currency,
    string? QuantityUnit,
    string? QuantityKind,
    double? FixedNumber,
    string? FixedNumberUnit,
    string? FixedNumberKind,
    double? Coverage,
    double? MinimumPrice,
    double? MaximumPrice,
    double? ProfitSplit,
    double? VolumeFlexibility,
    string? ReportingType,
    ContractLineStandard? Standard);

/// <summary>
/// Standard (catalog) definition of the product element behind a contract line.
/// Sourced from <see cref="Models.ProductElementDto"/>.
/// </summary>
public record ContractLineStandard(
    string? Description,
    string? AverageUnitPriceType,
    string? ExternalIdentifier,
    string? PriceModelType,
    string? QuantityModelType,
    string? CalculationModelType,
    string? RoundingModelType,
    string? VatModelType);
