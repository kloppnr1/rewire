using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;

namespace DataMigration.Migration.Tests;

/// <summary>
/// What the operator reads when Settl refuses a metering point. Settl answers with its own JSON
/// envelope, and the log used to carry the whole thing — identifier, message type, params and a
/// GUID — around the one sentence that says what was wrong.
/// </summary>
public class SettlErrorTextTests
{
    /// <summary>The exact body from a production run, shortened only in the trailing id.</summary>
    private const string ProductionBody =
        "HTTP 400: {\"Errors\":[{\"Message\":{\"Identifier\":\"BioWebService.BioDomValueValidationException\","
        + "\"Text\":\"PostalCode: Must not be empty\\nCity: Must not be empty\\nPostalCode: Must not be empty\\n"
        + "City: Must not be empty\",\"MessageType\":\"Error\",\"Params\":[]},"
        + "\"Id\":\"5cc56ce7-0000-0000-0000-000000000000\"}]}";

    [Fact]
    public void The_reason_survives_and_the_envelope_does_not()
    {
        Assert.Equal(
            "HTTP 400: PostalCode: Must not be empty; City: Must not be empty",
            SettlErrorText.Readable(ProductionBody));
    }

    [Fact]
    public void A_repeated_line_is_said_once()
    {
        // Settl repeats the same validation per field group. Four copies is not four problems.
        var readable = SettlErrorText.Readable(ProductionBody);
        Assert.Equal(1, readable.Split("PostalCode: Must not be empty").Length - 1);
    }

    [Theory]
    [InlineData("HTTP 500: Internal Server Error")]
    [InlineData("boom")]
    [InlineData("HTTP 400: {not json at all")]
    [InlineData("HTTP 400: {\"Errors\":[]}")]
    public void An_answer_that_is_not_that_shape_comes_back_untouched(string raw)
    {
        // A half-read message is worse than a raw one: the raw one can still be looked up.
        Assert.Equal(raw, SettlErrorText.Readable(raw));
    }

    [Fact]
    public void A_bare_message_object_is_read_too()
    {
        Assert.Equal(
            "HTTP 400: Consumer: Must not be empty",
            SettlErrorText.Readable(
                "HTTP 400: {\"Message\":{\"Text\":\"Consumer: Must not be empty\",\"MessageType\":\"Error\"}}"));
    }

    [Fact]
    public void Nothing_at_all_stays_nothing()
    {
        Assert.Equal("", SettlErrorText.Readable(null));
        Assert.Equal("", SettlErrorText.Readable(""));
    }

    /// <summary>
    /// The same failure on forty-two metering points is one problem to act on. Printed one by one
    /// it also pushed everything else out of the twenty lines the summary had room for.
    /// </summary>
    [Fact]
    public void The_summary_says_a_repeated_failure_once_with_a_count()
    {
        var report = new MigrationReport();
        for (var i = 0; i < 42; i++)
            report.Errors.Add($"MP 5713131741100902{i:D2} create failed: "
                            + "HTTP 400: PostalCode: Must not be empty; City: Must not be empty");
        report.Errors.Add("Contract 'V LadVariP' not found in Settl");

        var log = new List<string>();
        MigrationSummaryWriter.WriteAndFinalize(report, new WarningCollector(), log, log.Add);

        Assert.Contains(log, l => l.Contains("Errors (43 in 2 groups)"));
        Assert.Contains(log, l => l.Contains("42x") && l.Contains("PostalCode: Must not be empty"));
        // A message that stands alone keeps its own text — the grouped form masks the ids, which
        // is a step backwards when there is only one of them.
        Assert.Contains(log, l => l == "    - Contract 'V LadVariP' not found in Settl");
        Assert.DoesNotContain(log, l => l.Contains("... and"));
    }
}
