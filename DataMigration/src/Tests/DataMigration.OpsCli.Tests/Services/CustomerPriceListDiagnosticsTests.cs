using System.Text.Json;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class CustomerPriceListDiagnosticsTests
{
    private static readonly DateTime Cos = new(2026, 1, 1);
    private static readonly DateTime RefDate = new(2025, 12, 31);

    private static CustomerPriceListAssociationResult Result(
        IReadOnlyList<(string, string)>? contracts = null,
        IReadOnlyList<(string, string?, string?)>? products = null,
        int goForward = 0,
        int emittedBeforeGrouping = 0,
        List<CustomerPriceListAssociationRow>? rows = null)
        => new(rows ?? [], contracts ?? [], products ?? [], goForward, emittedBeforeGrouping);

    private static CustomerPriceListDiagnosticsDocument Build(CustomerPriceListAssociationResult result) =>
        CustomerPriceListDiagnostics.Build(
            result,
            representatives: 3,
            representativesStillActiveOnCos: 1,
            excelOutputPath: @"C:\tmp\pricelists.xlsx",
            dataAreaId: "hol",
            environment: "featuretest",
            changeOfSupplierDate: Cos,
            referenceDate: RefDate,
            generatedAt: new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Groups_unmatched_products_by_product_amrcode_and_rate_code_with_row_counts()
    {
        var doc = Build(Result(products:
        [
            ("V ProdErhv", "KLI 0500", null),
            ("V ProdErhv", "KLI 0500", null),
            ("V ProdErhv", "KLI 0600", null),
            ("V Ukendt", "KLI 0500", "DG1"),
        ]));

        Assert.Collection(doc.UnmatchedProducts,
            p => { Assert.Equal("V ProdErhv", p.Product); Assert.Equal("KLI 0500", p.AmrCode); Assert.Equal("", p.DgRateCode); Assert.Equal(2, p.Rows); },
            p => { Assert.Equal("V ProdErhv", p.Product); Assert.Equal("KLI 0600", p.AmrCode); Assert.Equal(1, p.Rows); },
            p => { Assert.Equal("V Ukendt", p.Product); Assert.Equal("DG1", p.DgRateCode); Assert.Equal(1, p.Rows); });

        Assert.Equal(4, doc.Totals.RowsWithoutProductElementCode);
        Assert.Equal(2, doc.Totals.DistinctProductsWithoutProductElementCode);
        Assert.Equal(3, doc.Totals.ProductCombinations);
    }

    [Fact]
    public void Groups_unmatched_contracts_separately_from_products()
    {
        var doc = Build(Result(contracts:
        [
            ("KLI 9999", ""),
            ("KLI 9999", ""),
            ("KLI 8888", "DG2"),
        ]));

        Assert.Collection(doc.UnmatchedContracts,
            c => { Assert.Equal("KLI 8888", c.AmrCode); Assert.Equal("DG2", c.DgRateCode); Assert.Equal(1, c.Contracts); },
            c => { Assert.Equal("KLI 9999", c.AmrCode); Assert.Equal(2, c.Contracts); });

        Assert.Equal(3, doc.Totals.RowsWithoutPriceListMatch);
        Assert.Equal(2, doc.Totals.ContractCombinations);
        Assert.Empty(doc.UnmatchedProducts);
    }

    [Fact]
    public void Carries_the_run_totals_and_context()
    {
        var rows = new List<CustomerPriceListAssociationRow>
        {
            new("100001", "PE1", "pl1", Cos, new DateTime(2026, 12, 31)),
        };
        var doc = Build(Result(rows: rows, goForward: 2, emittedBeforeGrouping: 7));

        Assert.Equal(1, doc.Totals.RowsWritten);
        Assert.Equal(7, doc.Totals.RowsBeforeGrouping);
        Assert.Equal(3, doc.Totals.Representatives);
        Assert.Equal(2, doc.Totals.GoForwardRows);
        Assert.Equal(1, doc.Totals.RepresentativesStillActiveOnCos);
        Assert.Equal("featuretest", doc.Environment);
        Assert.Equal("hol", doc.DataAreaId);
        Assert.Equal(Cos, doc.ChangeOfSupplierDate);
        Assert.Equal(RefDate, doc.ReferenceDate);
        Assert.EndsWith("pricelists.xlsx", doc.ExcelOutputPath);
    }

    [Fact]
    public void Serialises_to_camel_case_json_a_host_can_deserialise()
    {
        var doc = Build(Result(
            products: [("V ProdErhv", "KLI 0500", null)],
            contracts: [("KLI 9999", "")]));

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JsonOutput.HumanReadable
        };
        var json = JsonSerializer.Serialize(doc, options);

        Assert.Contains("\"unmatchedProducts\"", json);
        Assert.Contains("\"unmatchedContracts\"", json);
        Assert.Contains("\"rowsWithoutProductElementCode\"", json);
        Assert.Contains("V ProdErhv", json);

        var round = JsonSerializer.Deserialize<CustomerPriceListDiagnosticsDocument>(json, options)!;
        Assert.Equal("V ProdErhv", Assert.Single(round.UnmatchedProducts).Product);
        Assert.Equal("KLI 9999", Assert.Single(round.UnmatchedContracts).AmrCode);
        Assert.Equal(doc.Totals, round.Totals);
    }
}
