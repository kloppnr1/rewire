using System.Text.Json;
using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Runners;
using MigrationAccountGrouping = DataMigration.Migration.Runners.AccountGrouping;

namespace DataMigration.OpsCli.Tests.Runners;

public class GenerateStateRunnerTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProfilesDir => Path.Combine(_tmpDir, "profiles");
    private string OutputPath => Path.Combine(_tmpDir, "migration-state.json");

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Dictionary<string, MigrationAccountGrouping> _groupings = new()
    {
        ["100001"] = new MigrationAccountGrouping("100001", ["100001"], null, null, null,
            new Dictionary<string, string> { ["100001"] = "Test Kunde" }),
        ["100002"] = new MigrationAccountGrouping("100002", ["100002"], null, null, null,
            new Dictionary<string, string> { ["100002"] = "Anden Kunde" }),
        ["40017"] = new MigrationAccountGrouping("40017", ["40017"], null, null, null,
            new Dictionary<string, string> { ["40017"] = "Femcifret Kunde" }),
    };

    public GenerateStateRunnerTests()
    {
        Directory.CreateDirectory(ProfilesDir);

        File.WriteAllText(
            Path.Combine(ProfilesDir, "test.json"),
            """{"name":"test","accounts":["100001"],"extraction":{"deliveryCategory":"El-ekstern"}}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    private GenerateStateRunner MakeRunner() =>
        new((accounts, _, _, _) => Task.FromResult(
            accounts
                .Select(a => _groupings.GetValueOrDefault(a))
                .OfType<MigrationAccountGrouping>()
                .DistinctBy(g => g.RepresentativeAccount)
                .ToList()));

    private GenerateStateRunOptions MakeOptions(string profileName = "test", bool overwrite = false) => new()
    {
        ProfilesDirectory = ProfilesDir,
        ProfileNames = [profileName],
        ConnectionString = "unused",
        OutputPath = OutputPath,
        ReferenceDate = new DateTime(2026, 1, 1),
        NoInvoices = true,
        Overwrite = overwrite,
    };

    [Fact]
    public async Task First_run_creates_file_with_correct_shape()
    {
        var result = await MakeRunner().RunAsync(MakeOptions());

        Assert.Equal(0, result);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        var account = Assert.Single(state.Accounts);
        Assert.Equal("100001", account.SettlAccountNumber);
        Assert.Equal("Test Kunde", account.CustomerName);
        Assert.Contains("100001", account.SourceAccounts);
        Assert.Contains("El-ekstern", account.DeliveryCategories);
        Assert.Empty(account.Invoices);
    }

    [Fact]
    public async Task Merge_preserves_existing_invoices_on_rerun()
    {
        var runner = MakeRunner();
        var options = MakeOptions();

        await runner.RunAsync(options);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;
        state.Accounts[0].Invoices.Add(new InvoiceManifestItem { InvoiceId = "INV001", SourceAccount = "100001" });
        await File.WriteAllTextAsync(OutputPath, JsonSerializer.Serialize(state, WriteOptions));

        var result = await runner.RunAsync(options);
        Assert.Equal(0, result);

        var merged = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        var account = Assert.Single(merged.Accounts);
        var invoice = Assert.Single(account.Invoices);
        Assert.Equal("INV001", invoice.InvoiceId);
    }

    [Fact]
    public async Task Second_profile_adds_account_without_removing_existing()
    {
        File.WriteAllText(
            Path.Combine(ProfilesDir, "test2.json"),
            """{"name":"test2","accounts":["100002"],"extraction":{"deliveryCategory":"Vand"}}""");

        var runner = MakeRunner();
        await runner.RunAsync(MakeOptions("test"));
        var result = await runner.RunAsync(MakeOptions("test2"));
        Assert.Equal(0, result);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        Assert.Equal(2, state.Accounts.Count);
        Assert.Contains(state.Accounts, a => a.SettlAccountNumber == "100001");
        Assert.Contains(state.Accounts, a => a.SettlAccountNumber == "100002" && a.DeliveryCategories.Contains("Vand"));
    }

    [Fact]
    public async Task Multiple_profiles_in_one_run_produces_all_accounts()
    {
        File.WriteAllText(
            Path.Combine(ProfilesDir, "test2.json"),
            """{"name":"test2","accounts":["100002"],"extraction":{"deliveryCategory":"Vand"}}""");

        var runner = MakeRunner();
        var options = new GenerateStateRunOptions
        {
            ProfilesDirectory = ProfilesDir,
            ProfileNames = ["test", "test2"],
            ConnectionString = "unused",
            OutputPath = OutputPath,
            ReferenceDate = new DateTime(2026, 1, 1),
            NoInvoices = true,
        };

        var result = await runner.RunAsync(options);
        Assert.Equal(0, result);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        Assert.Equal(2, state.Accounts.Count);
        Assert.Contains(state.Accounts, a => a.SettlAccountNumber == "100001");
        Assert.Contains(state.Accounts, a => a.SettlAccountNumber == "100002");
    }

    [Fact]
    public async Task SettlAccountNumber_padded_with_9_when_env_is_production()
    {
        // Tripwire: hvis padding-pipeline brydes (fx Env glemmes, eller PadForEnv-kaldet
        // fjernes), så skal denne test fange det.
        File.WriteAllText(
            Path.Combine(ProfilesDir, "small.json"),
            """{"name":"small","accounts":["40017"],"extraction":{"deliveryCategory":"El-ekstern"}}""");

        var options = new GenerateStateRunOptions
        {
            ProfilesDirectory = ProfilesDir,
            ProfileNames = ["small"],
            ConnectionString = "unused",
            OutputPath = OutputPath,
            ReferenceDate = new DateTime(2026, 1, 1),
            NoInvoices = true,
            Env = "production",
        };

        var result = await MakeRunner().RunAsync(options);
        Assert.Equal(0, result);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        var account = Assert.Single(state.Accounts);
        Assert.Equal("940017", account.SettlAccountNumber);          // padded form
        Assert.Equal(6, account.SettlAccountNumber.Length);
        Assert.Contains("40017", account.SourceAccounts);            // rå Xellent bevares
        Assert.DoesNotContain("940017", account.SourceAccounts);
    }

    [Fact]
    public async Task SettlAccountNumber_unchanged_when_env_not_production()
    {
        // Featuretest/production-hev/production-vhel: ingen padding.
        File.WriteAllText(
            Path.Combine(ProfilesDir, "small.json"),
            """{"name":"small","accounts":["40017"],"extraction":{"deliveryCategory":"El-ekstern"}}""");

        var options = MakeOptions("small");
        options.Env = "featuretest";

        var result = await MakeRunner().RunAsync(options);
        Assert.Equal(0, result);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        var account = Assert.Single(state.Accounts);
        Assert.Equal("40017", account.SettlAccountNumber);            // ingen padding
        Assert.Contains("40017", account.SourceAccounts);
    }

    [Fact]
    public async Task Overwrite_clears_existing_accounts_before_run()
    {
        var runner = MakeRunner();

        // First run: both profiles
        File.WriteAllText(
            Path.Combine(ProfilesDir, "test2.json"),
            """{"name":"test2","accounts":["100002"],"extraction":{"deliveryCategory":"Vand"}}""");

        await runner.RunAsync(new GenerateStateRunOptions
        {
            ProfilesDirectory = ProfilesDir,
            ProfileNames = ["test", "test2"],
            ConnectionString = "unused",
            OutputPath = OutputPath,
            ReferenceDate = new DateTime(2026, 1, 1),
            NoInvoices = true,
        });

        // Second run: only "test" with --overwrite — should not retain "100002"
        var result = await runner.RunAsync(MakeOptions("test", overwrite: true));
        Assert.Equal(0, result);

        var state = JsonSerializer.Deserialize<MigrationStateDocument>(
            await File.ReadAllTextAsync(OutputPath), ReadOptions)!;

        var account = Assert.Single(state.Accounts);
        Assert.Equal("100001", account.SettlAccountNumber);
    }
}
