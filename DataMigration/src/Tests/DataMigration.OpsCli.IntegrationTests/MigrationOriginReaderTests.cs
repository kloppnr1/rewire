using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// export-missing-eav-excel leaves out metering points that were part of the migration: Xellent has an El-ekstern
/// contract for them, in any company, running on the day before Settl's first delivery start. Running on, not ending
/// on: the 2026-08-31 batch restamped some contract ends.
/// </summary>
public sealed class MigrationOriginReaderTests : IDisposable
{
    private static readonly DateOnly Cos = new(2026, 9, 1);
    private static readonly DateTime DayBefore = new(2026, 8, 31);
    private readonly FakeXellent _xellent = new();

    public void Dispose() => _xellent.Dispose();

    private async Task<string[]> MigratedAsync(params string[] meteringPoints)
    {
        var (migrated, error) = await new XellentMigrationOriginReader(_xellent.ConnectionString)
            .WithAccountAtCosMinusOneAsync(meteringPoints.ToDictionary(mp => mp, _ => Cos), CancellationToken.None);
        Assert.Null(error);
        return [.. migrated!.Order()];
    }

    [Fact]
    public async Task A_contract_ending_the_day_before_counts_in_any_company()
    {
        _xellent.Add(XellentRows.For(new("410001", "20000001", "571313100000000101", new(2020, 1, 1), DayBefore, Company: "for")));
        _xellent.Add(XellentRows.For(new("410002", "20000002", "570715000000000102", new(2020, 1, 1), DayBefore, Company: "meh")));
        _xellent.Add(XellentRows.For(new("410003", "20000003", "571313100000000103", new(2020, 1, 1), DayBefore, Company: "")));

        Assert.Equal(["570715000000000102", "571313100000000101", "571313100000000103"],
            await MigratedAsync("571313100000000101", "570715000000000102", "571313100000000103"));
    }

    [Fact]
    public async Task A_contract_running_on_the_day_before_counts_even_when_its_end_was_restamped_or_is_open()
    {
        _xellent.Add(XellentRows.For(new("410011", "20000011", "571313100000000111", new(2020, 1, 1), new(2026, 9, 30))));
        _xellent.Add(XellentRows.For(new("410012", "20000012", "571313100000000112", new(2020, 1, 1), XellentRows.Open)));

        Assert.Equal(["571313100000000111", "571313100000000112"],
            await MigratedAsync("571313100000000111", "571313100000000112"));
    }

    [Fact]
    public async Task No_contract_on_the_day_before_is_not_part_of_the_migration()
    {
        // Ended months before (a later move-in after the migration), or started on the delivery day itself.
        _xellent.Add(XellentRows.For(new("410021", "20000021", "571313100000000121", new(2020, 1, 1), new(2026, 6, 30))));
        _xellent.Add(XellentRows.For(new("410022", "20000022", "571313100000000122", new(2026, 9, 1), XellentRows.Open)));

        Assert.Empty(await MigratedAsync("571313100000000121", "571313100000000122", "571313100000000123"));
    }
}
