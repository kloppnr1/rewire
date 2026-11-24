using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class Consumer2ReportInputTests
{
    private static string Row(params string[] cells) => string.Join(';', cells.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));

    private static string[] Cells(Guid id, string duplicate, string name = "Fixture")
    {
        var cells = Enumerable.Repeat("", ReportConsumer2DuplicateRunner.Header.Length).ToArray();
        cells[1] = id.ToString();
        cells[9] = name;
        cells[13] = duplicate;
        return cells;
    }

    [Fact]
    public void Reads_each_yes_point_once_and_ignores_other_rows()
    {
        var yes = Guid.NewGuid();
        var no = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var csv = string.Join(Environment.NewLine,
            Row(ReportConsumer2DuplicateRunner.Header),
            Row(Cells(yes, "No")), Row(Cells(yes, "Yes", "Name; with \"quotes\"")), Row(Cells(yes, "Yes")),
            Row(Cells(no, "No")), Row(Cells(unknown, "Unknown")), "");
        Assert.Equal([yes], Consumer2ReportInput.ReadDuplicateSettlIds(new StringReader(csv), "test.csv"));
    }

    [Fact]
    public void A_different_header_stops_the_run()
    {
        var oldHeader = ReportConsumer2DuplicateRunner.Header[..^1];
        var ex = Assert.Throws<InvalidDataException>(() =>
            Consumer2ReportInput.ReadDuplicateSettlIds(new StringReader(Row(oldHeader)), "old.csv"));
        Assert.Contains("header does not match", ex.Message);
    }

    [Fact]
    public async Task Reads_a_file_written_by_the_report_including_its_byte_order_mark()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.ConsumerPoints[id] = new MeteringPointV4Dto
        {
            Id = id,
            Consumer = new()
            {
                Start = new DateTime(2026, 1, 1), CustomerType = "Person",
                FirstConsumerParty = new() { Name = new() { FullName = "Fixture" } },
                SecondConsumerParty = new() { Name = new() { FullName = "Fixture" } }
            }
        };
        var path = Path.Combine(Path.GetTempPath(), $"consumer2-{Guid.NewGuid():N}.csv");
        try
        {
            await using (var output = new StreamWriter(path, false, new System.Text.UTF8Encoding(true)))
                Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
                    .RunAsync("featuretest", [id.ToString()], null, output));
            Assert.Equal([id], Consumer2ReportInput.ReadDuplicateSettlIds(path));
        }
        finally { File.Delete(path); }
    }
}
