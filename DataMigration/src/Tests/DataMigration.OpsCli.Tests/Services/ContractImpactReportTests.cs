using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class ContractImpactReportTests
{
    [Fact]
    public void Counts_unique_source_points_and_actual_named_links()
    {
        var start = new DateTime(2026, 8, 1);
        var from = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new ContractSourceSnapshot(
            [new("MP1", "A1", "C1", "G1", "V AboRabat", start, start, "Live", null)],
            [new("MP1", "A1", "C1", "G1", "V AboRabat", start, start, "Log", start),
             new("MP2", "A2", "C2", "G2", "V AboRabat", start, start, "Log", start)],
            [new("MP1", Guid.NewGuid(), "V AboRabat", from, to),
             new("MP1", Guid.NewGuid(), "Lade_EjeVariabelUA", from, to),
             new("MP3", Guid.NewGuid(), "V AboRabat", from, to)]);

        var report = ContractImpactReport.Build(snapshot, "V AboRabat");

        Assert.Equal(2, report.XellentCandidates);
        Assert.Equal(2, report.NamedSettlLinks);
        Assert.Equal(1, report.InBoth);
        Assert.Contains("MP1;Yes;1", report.Csv);
        Assert.Contains("MP2;Yes;0", report.Csv);
        Assert.Contains("MP3;No;1", report.Csv);
    }
}
