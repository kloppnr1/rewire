using DataMigration.OpsCli.Reporting;

namespace DataMigration.OpsCli.Tests.Reporting;

public class SettlCsvParserTests
{
    private static readonly string ValidHeader =
        "Last Updated,Payment Agreement Number,Payment Method,External Customer Number," +
        "Customer Name,Payer Name,Currency,Agreement Type,Metering Point Id,Joint Invoice number";

    private static readonly string ValidDataRow =
        "\"08.06.2026, 09.51.01\",B12853,FI-Kort,E12853,Gribskov Kommune," +
        "Gribskov Kommune,DKK,Metering Point Payment Agreement,571313179102390039,";

    private static string Csv(string header, string dataRow) => header + "\n" + dataRow;

    private readonly SettlCsvParser _parser = new();

    [Fact]
    public void Parses_valid_csv_with_metering_point()
    {
        var records = _parser.ParseContent(Csv(ValidHeader, ValidDataRow));

        Assert.Single(records);
        Assert.Equal("B12853", records[0].PaymentAgreementNumber);
        Assert.Equal("E12853", records[0].ExternalCustomerNumber);
        Assert.Equal("571313179102390039", records[0].MeteringpointId);
        Assert.Equal("Metering Point Payment Agreement", records[0].AgreementType);
    }

    [Fact]
    public void Parses_alphanumeric_metering_point_id()
    {
        // Water metering points are prefixed with a letter (e.g. "V105254701"), unlike
        // electricity MPs which are purely numeric.
        var dataRow =
            "\"08.06.2026, 09.51.01\",B12853,FI-Kort,E12853,Gribskov Kommune," +
            "Gribskov Kommune,DKK,Metering Point Payment Agreement,V105254701,";

        var records = _parser.ParseContent(Csv(ValidHeader, dataRow));

        Assert.Single(records);
        Assert.Equal("V105254701", records[0].MeteringpointId);
    }

    [Fact]
    public void Throws_when_payment_agreement_number_column_renamed()
    {
        var renamedHeader = ValidHeader.Replace("Payment Agreement Number", "Agreement Number");

        var ex = Assert.Throws<InvalidOperationException>(
            () => _parser.ParseContent(Csv(renamedHeader, ValidDataRow)));

        Assert.Contains("payment agreement number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throws_when_external_customer_number_column_renamed()
    {
        var renamedHeader = ValidHeader.Replace("External Customer Number", "Customer Number");

        var ex = Assert.Throws<InvalidOperationException>(
            () => _parser.ParseContent(Csv(renamedHeader, ValidDataRow)));

        Assert.Contains("external customer number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Payment Method",          "payment method")]
    [InlineData("Customer Name",           "customer name")]
    [InlineData("Payer Name",              "payer name")]
    [InlineData("Agreement Type",          "agreement type")]
    [InlineData("Metering Point Id",       "metering point id")]
    [InlineData("Joint Invoice number",    "joint invoice number")]
    public void Throws_when_required_column_renamed(string original, string expectedInMessage)
    {
        var renamedHeader = ValidHeader.Replace(original, original + "_renamed");

        var ex = Assert.Throws<InvalidOperationException>(
            () => _parser.ParseContent(Csv(renamedHeader, ValidDataRow)));

        Assert.Contains(expectedInMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throws_on_old_csv_format_with_meteringpoint_id_no_space()
    {
        // Old Settl export used "Meteringpoint Id" (no space). Current format is "Metering Point Id".
        var oldHeader =
            "Last Updated,Payment Agreement Number,Payment Method,External Customer Number," +
            "Customer Name,Payer Name,Currency,Agreement Type,Meteringpoint Id,Joint Invoice number";
        var oldDataRow =
            "\"18.06.2026, 08.20.09\",B14448,FI-Kort,E14449,Ejerforeningen Fiktivvejeqw 8," +
            "Ejerforeningen Fiktivvejeqw 8,DKK,Metering Point Payment Agreement,571313115200357628,";

        var ex = Assert.Throws<InvalidOperationException>(
            () => _parser.ParseContent(Csv(oldHeader, oldDataRow)));

        Assert.Contains("metering point id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throws_when_meteringpoint_id_column_contains_non_alphanumeric_value()
    {
        // A column shift (e.g. unstripped currency) would land a text value in the MP column.
        // The parser must throw rather than silently nullify, so the format change is caught.
        var rowWithTextInMpColumn =
            "\"18.06.2026, 08.20.09\",B14448,FI-Kort,E14449,Ejerforeningen Fiktivvejeqw 8," +
            "Ejerforeningen Fiktivvejeqw 8,DKK,Metering Point Payment Agreement,NOT-A-NUMBER,";

        var ex = Assert.Throws<InvalidOperationException>(
            () => _parser.ParseContent(Csv(ValidHeader, rowWithTextInMpColumn)));

        Assert.Contains("NOT-A-NUMBER", ex.Message);
    }
}
