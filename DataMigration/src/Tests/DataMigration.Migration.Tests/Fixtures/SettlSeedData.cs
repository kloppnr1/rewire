using DataMigration.Migration.Runners;
using System.Text.Json;

namespace DataMigration.Migration.Tests.Fixtures;

public static class SettlSeedData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static SettlAccountSeedData Load(string accountNumber)
    {
        var seedPath = Path.Combine(
            Path.GetDirectoryName(typeof(SettlSeedData).Assembly.Location)!,
            "Fixtures", "SeedData", accountNumber, "settl.json");

        if (!File.Exists(seedPath))
            throw new FileNotFoundException($"Settl seed data not found: {seedPath}. Run 'generate-seed {accountNumber}' first.");

        var json = File.ReadAllText(seedPath);
        return JsonSerializer.Deserialize<SettlAccountSeedData>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize seed data from {seedPath}");
    }
}
