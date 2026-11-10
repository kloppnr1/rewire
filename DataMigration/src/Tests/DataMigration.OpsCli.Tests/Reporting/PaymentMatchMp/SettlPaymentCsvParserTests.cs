using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using FluentAssertions;
using Xunit;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class SettlPaymentCsvParserTests
{
    internal const string BundledHeader = "Last Updated,Payment Method,External Customer Number,Customer Name,Payer Name,Currency,Agreement Type,Metering Point Id,Joint Invoice number";
    internal const string BundledRow = "\"02.10.2026, 09.51.01\",PBS,E1,\"Example, Customer\",Example payer,DKK,Metering Point Payment Agreement,571313100000000001,";

    [Fact]
    public void Missing_agreement_number_does_not_drop_bundled_export_rows()
    {
        var rows = SettlPaymentCsvParser.ParseContent(BundledHeader + "\n" + BundledRow);
        var row = rows.Should().ContainSingle().Which;
        row.PaymentAgreementNumber.Should().BeEmpty();
        row.ExternalCustomerNumber.Should().Be("E1");
        row.MeteringpointId.Should().Be(PaymentMatchMpTests.Mp1);
        row.CustomerName.Should().Be("Example, Customer");
        row.AgreementType.Should().Be("Metering Point Payment Agreement");
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("")]
    public void Present_agreement_number_is_preserved_and_blank_values_are_allowed(string number)
    {
        var header = "Payment Agreement Number;Payment Method;External Customer Number;Customer Name;Payer Name;Agreement Type;Metering Point Id;Joint Invoice number";
        var row = SettlPaymentCsvParser.ParseContent(header + $"\n{number};PBS;E1;;;Joint Invoice Payment Agreement;;MAIN").Single();
        row.PaymentAgreementNumber.Should().Be(number);
        row.JointInvoiceNumber.Should().Be("MAIN");
        row.MeteringpointId.Should().BeNull();
    }

    [Fact]
    public void Standard_quoted_fields_and_multiline_names_stay_in_their_columns()
    {
        var row = BundledRow.Replace("\"Example, Customer\"", "\"Example, \"\"Customer\"\"\nCompany\"");
        var record = SettlPaymentCsvParser.ParseContent("\uFEFF" + BundledHeader + "\r\n" + row).Single();
        record.CustomerName.Should().Be("Example, \"Customer\"\nCompany");
        record.ExternalCustomerNumber.Should().Be("E1");
        record.MeteringpointId.Should().Be(PaymentMatchMpTests.Mp1);
    }

    [Fact]
    public void Legacy_whole_row_wrapping_and_unquoted_timestamps_remain_supported()
    {
        var wrapped = "\"" + BundledRow.Replace("\"", "\"\"") + "\"";
        var unquoted = BundledRow.Replace("\"02.10.2026, 09.51.01\"", "02.10.2026, 09.51.01");
        foreach (var row in new[] { wrapped, unquoted })
            SettlPaymentCsvParser.ParseContent(BundledHeader + "\n" + row).Single().ExternalCustomerNumber.Should().Be("E1");
    }

    [Theory]
    [InlineData("External Customer Number")]
    [InlineData("Metering Point Id")]
    [InlineData("Joint Invoice number")]
    public void Actual_matching_keys_remain_required(string column)
    {
        Action parse = () => SettlPaymentCsvParser.ParseContent(BundledHeader.Replace(column, column + "_renamed") + "\n" + BundledRow);
        parse.Should().Throw<InvalidOperationException>().WithMessage("*required column*");
    }

    [Fact]
    public void Unrelated_meter_references_do_not_reject_a_mixed_utility_export()
    {
        foreach (var id in new[] { "00000000-0000-0000-0000-000000000000", "District meter 42", "V105254701" })
            SettlPaymentCsvParser.ParseContent(BundledHeader + "\n" + BundledRow.Replace(PaymentMatchMpTests.Mp1, id))
                .Single().MeteringpointId.Should().Be(id);
    }

    [Fact]
    public void Shifted_columns_are_rejected_without_exposing_customer_values()
    {
        Action parse = () => SettlPaymentCsvParser.ParseContent(BundledHeader + "\n" + BundledRow + ",extra");
        parse.Should().Throw<InvalidOperationException>().WithMessage("*10 felter; forventede 9*");
    }
}
