using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class ForInvoiceScopeTests
{
    [Fact]
    public void Excluded_invoice_report_distinguishes_meh_and_missing_contracts()
    {
        var csv = ForInvoiceScope.BuildCsv(
        [
            new ExcludedCompanyInvoice("399574", "2", "10394934", "meh"),
            new ExcludedCompanyInvoice("399574", "1", "", "")
        ]);

        Assert.Contains("399574;1;;;NoXellentContract", csv);
        Assert.Contains("399574;2;10394934;meh;NotForCompany", csv);
    }

    [Fact]
    public void Report_is_written_beside_the_state()
    {
        Assert.Equal(Path.Combine("data", "state-excluded-invoices.csv"),
            ForInvoiceScope.ReportPath(Path.Combine("data", "state.json")));
    }
}
