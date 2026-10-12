namespace DataMigration.OpsCli.Services;

/// <summary>
/// One Xellent product that produced no Excel row because no <c>ProductElementMappings</c> rule
/// names it, grouped the way the log groups it: per (product, rate code, delivery-group rate code).
/// </summary>
/// <param name="Rows">Contract-part rows that hit this combination — not distinct customers.</param>
public sealed record CustomerPriceListUnmatchedProduct(
    string Product,
    string AmrCode,
    string DgRateCode,
    int Rows);

/// <summary>
/// One rate-code combination whose contracts found no price list in the Settl catalog. Deliberately
/// a different shape from <see cref="CustomerPriceListUnmatchedProduct"/>: the builder knows no
/// product here — the row was dropped before any product question was asked.
/// </summary>
/// <param name="Contracts">Contract-part rows that hit this combination.</param>
public sealed record CustomerPriceListUnmatchedContract(
    string AmrCode,
    string DgRateCode,
    int Contracts);

/// <summary>
/// The numbers that make the two lists readable: how much went into the Excel, and how much was
/// dropped for each reason. <paramref name="RowsWithoutProductElementCode"/> and
/// <paramref name="RowsWithoutPriceListMatch"/> are the row counts behind the two lists.
/// </summary>
public sealed record CustomerPriceListDiagnosticsTotals(
    int RowsWritten,
    int RowsBeforeGrouping,
    int Representatives,
    int GoForwardRows,
    int RepresentativesStillActiveOnCos,
    int RowsWithoutProductElementCode,
    int DistinctProductsWithoutProductElementCode,
    int ProductCombinations,
    int RowsWithoutPriceListMatch,
    int ContractCombinations);

/// <summary>
/// What <c>export-customer-pricelists</c> left out of the Excel, as a machine-readable document
/// beside the .xlsx. Same content as the run's diagnostics log lines — a host reading this never has
/// to parse the log, which on a full run is buried under tens of thousands of progress lines.
///
/// <para>A product listed here is not automatically a fault: <c>V ProdErhv</c> is unmapped on
/// purpose (a production metering point carries both <c>V ProdHand</c> and <c>V ProdErhv</c>, and
/// only the first should produce a row). The document reports, it does not judge.</para>
/// </summary>
public sealed record CustomerPriceListDiagnosticsDocument(
    DateTime GeneratedAt,
    string? Environment,
    string DataAreaId,
    DateTime ChangeOfSupplierDate,
    DateTime ReferenceDate,
    string ExcelOutputPath,
    CustomerPriceListDiagnosticsTotals Totals,
    IReadOnlyList<CustomerPriceListUnmatchedProduct> UnmatchedProducts,
    IReadOnlyList<CustomerPriceListUnmatchedContract> UnmatchedContracts);

/// <summary>
/// Turns a <see cref="CustomerPriceListAssociationResult"/> into the diagnostics document. Pure —
/// the runner also renders its log lines off the result of this, so the file and the log can never
/// disagree about the grouping or the counts.
/// </summary>
public static class CustomerPriceListDiagnostics
{
    public static CustomerPriceListDiagnosticsDocument Build(
        CustomerPriceListAssociationResult result,
        int representatives,
        int representativesStillActiveOnCos,
        string excelOutputPath,
        string dataAreaId,
        string? environment,
        DateTime changeOfSupplierDate,
        DateTime referenceDate,
        DateTime generatedAt)
    {
        var products = result.UnmatchedProducts
            .GroupBy(t => t)
            .Select(g => new CustomerPriceListUnmatchedProduct(
                g.Key.Product,
                g.Key.AmrCode ?? string.Empty,
                g.Key.DgRateCode ?? string.Empty,
                g.Count()))
            .OrderBy(p => p.Product, StringComparer.Ordinal)
            .ThenBy(p => p.AmrCode, StringComparer.Ordinal)
            .ThenBy(p => p.DgRateCode, StringComparer.Ordinal)
            .ToList();

        var contracts = result.UnmatchedContracts
            .GroupBy(t => t)
            .Select(g => new CustomerPriceListUnmatchedContract(
                g.Key.AmrCode, g.Key.DgRateCode, g.Count()))
            .OrderBy(c => c.AmrCode, StringComparer.Ordinal)
            .ThenBy(c => c.DgRateCode, StringComparer.Ordinal)
            .ToList();

        var totals = new CustomerPriceListDiagnosticsTotals(
            RowsWritten: result.Rows.Count,
            RowsBeforeGrouping: result.EmittedBeforeGrouping,
            Representatives: representatives,
            GoForwardRows: result.GoForwardEmitted,
            RepresentativesStillActiveOnCos: representativesStillActiveOnCos,
            RowsWithoutProductElementCode: result.UnmatchedProducts.Count,
            DistinctProductsWithoutProductElementCode: products
                .Select(p => p.Product).Distinct(StringComparer.Ordinal).Count(),
            ProductCombinations: products.Count,
            RowsWithoutPriceListMatch: result.UnmatchedContracts.Count,
            ContractCombinations: contracts.Count);

        return new CustomerPriceListDiagnosticsDocument(
            generatedAt,
            environment,
            dataAreaId,
            changeOfSupplierDate,
            referenceDate,
            excelOutputPath,
            totals,
            products,
            contracts);
    }
}
