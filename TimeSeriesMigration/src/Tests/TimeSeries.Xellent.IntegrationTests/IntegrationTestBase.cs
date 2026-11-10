using System.Globalization;
using System.Text.Json.Nodes;
using DataMigration.XellentData;
using TimeSeries.Cli.Tests.Fixtures;

namespace TimeSeries.Xellent.IntegrationTests;

/// <summary>
/// Base class for the end-to-end extraction tests. Xellent is a SQLite in-memory copy seeded by
/// <see cref="XellentTimeSeriesSeeder"/>, so the tests run anywhere: no VPN, no testsettings.json.
///
/// <para>The seed points come from the committed goldens, which were recorded from AXDB50_TEST.
/// Each test also seeds points just outside its window, so a wrong window bound shows up as
/// golden drift.</para>
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly XellentTimeSeriesSeeder Seeder = new();
    private XellentDbContext? _context;

    /// <summary>The seeded Xellent copy. Created on first use, after the test has seeded it.</summary>
    protected XellentDbContext Context => _context ??= Seeder.CreateContext();

    /// <summary>The (UTC time, quantity) points of a golden payload, as Xellent stores them.</summary>
    protected static List<(DateTime Utc, decimal Value)> GoldenPoints(string goldenPath) =>
        JsonNode.Parse(File.ReadAllText(goldenPath))!["points"]!.AsArray()
            .Select(p => (
                DateTime.Parse(p!["time"]!.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                decimal.Parse(p["quantity"]!.ToJsonString(), CultureInfo.InvariantCulture)))
            .OrderBy(p => p.Item1)
            .ToList();

    public virtual void Dispose()
    {
        _context?.Dispose();
        Seeder.Dispose();
        GC.SuppressFinalize(this);
    }
}
