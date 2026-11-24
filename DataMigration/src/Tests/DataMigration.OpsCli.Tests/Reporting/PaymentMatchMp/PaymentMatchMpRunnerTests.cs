using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using FluentAssertions;
using static DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp.PaymentMatchMpTests;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

/// <summary>The command wrapper: the files in, the three CSVs out, and bad input stopped before Xellent is read.</summary>
public class PaymentMatchMpRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "payment-match-mp-" + Guid.NewGuid().ToString("N"));
    private const string SettlCsv = "Payment Agreement Number;Payment Method;External Customer Number;Customer Name;Payer Name;Agreement Type;Metering Point Id;Joint Invoice number\n"
        + "S1;MobilePay;E1;Synthetic customer;Synthetic customer;Metering Point Payment Agreement;" + Mp1 + ";\n";

    public PaymentMatchMpRunnerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Writes_report_coverage_and_agreements_with_an_excel_bom()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1), Owner(Mp2, "200")], MobileContracts = [Mobile()] };
        var lines = new List<string>();
        var output = Path.Combine(_dir, "out");

        var exit = await new PaymentMatchMpRunner(reader, null, lines.Add).RunAsync(
            File("points.txt", $"{Mp1}\n{Mp2}"), File("payments.csv", SettlCsv), "mb", Cutover, "featuretest", output, CancellationToken.None);

        exit.Should().Be(0);
        var files = Directory.GetFiles(output).Select(Path.GetFileName).ToArray();
        files.Should().HaveCount(3);
        files.Should().OnlyContain(f => f!.StartsWith("payment-match-mb-mp-list-featuretest-2026-09-01-"));
        files.Should().Contain(f => f!.EndsWith("-report.csv")).And.Contain(f => f!.EndsWith("-coverage.csv"))
            .And.Contain(f => f!.EndsWith("-agreements.csv"));
        var coverage = System.IO.File.ReadAllBytes(Directory.GetFiles(output, "*-coverage.csv").Single());
        coverage.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        System.IO.File.ReadAllText(Directory.GetFiles(output, "*-coverage.csv").Single()).Should().Contain("'" + Mp2);
        lines.Should().Contain(l => l.StartsWith("MP-liste: 2 angivet, 2 unikke"));
    }

    [Fact]
    public async Task Invalid_ids_stop_the_run_before_xellent_is_read()
    {
        var reader = new StubMpReader();
        var run = () => new PaymentMatchMpRunner(reader, null, _ => { }).RunAsync(
            File("points.txt", $"{Mp1}\n5.713131E17"), File("payments.csv", SettlCsv), "pbs", Cutover, "featuretest", _dir, CancellationToken.None);

        (await run.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("1 ugyldige");
        reader.OwnerDate.Should().Be(default);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("PBS")]
    public async Task Only_pbs_and_mb_are_accepted(string type)
    {
        var run = () => new PaymentMatchMpRunner(new StubMpReader(), null, _ => { }).RunAsync(
            File("points.txt", Mp1), File("payments.csv", SettlCsv), type, Cutover, "featuretest", _dir, CancellationToken.None);

        await run.Should().ThrowAsync<ArgumentException>();
    }
}
