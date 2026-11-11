using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// export-missing-eav-excel leaves out metering points that were part of the migration. The migration read every
/// Xellent company, so a metering point whose contract ending at COS-1 is in another company than FOR was migrated
/// too and must not get an EstimatedAnnualVolume row.
/// </summary>
public sealed class MigrationOriginReaderTests : IDisposable
{
    private readonly FakeXellent _xellent = new();

    public void Dispose() => _xellent.Dispose();

    [Fact]
    public async Task A_contract_ending_at_cos_minus_one_counts_in_any_company()
    {
        var cos = new DateOnly(2026, 9, 1);
        var ended = new DateTime(2026, 8, 31);
        _xellent.Add(XellentRows.For(new("410001", "20000001", "571313100000000101", new(2020, 1, 1), ended, Company: "for")));
        _xellent.Add(XellentRows.For(new("410002", "20000002", "570715000000000102", new(2020, 1, 1), ended, Company: "meh")));
        _xellent.Add(XellentRows.For(new("410003", "20000003", "571313100000000103", new(2020, 1, 1), ended, Company: "")));
        // Contract ends on another day: not the account at COS-1.
        _xellent.Add(XellentRows.For(new("410004", "20000004", "571313100000000104", new(2020, 1, 1), new(2026, 6, 30))));

        var (migrated, error) = await new XellentMigrationOriginReader(_xellent.ConnectionString)
            .WithAccountAtCosMinusOneAsync(new Dictionary<string, DateOnly>
            {
                ["571313100000000101"] = cos,
                ["570715000000000102"] = cos,
                ["571313100000000103"] = cos,
                ["571313100000000104"] = cos,
            }, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(["570715000000000102", "571313100000000101", "571313100000000103"], migrated!.Order().ToArray());
    }
}
