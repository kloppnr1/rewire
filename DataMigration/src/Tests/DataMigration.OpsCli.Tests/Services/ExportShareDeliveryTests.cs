using System.Text.Json.Nodes;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class ExportShareDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"share-{Guid.NewGuid():N}");
    private readonly List<string> _log = [];
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 1, 30, 0, TimeSpan.Zero);
    private DateTimeOffset _now = Start;

    private string EnvDir => Path.Combine(_root, "production");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ExportShareDelivery Delivery() => new(() => _now, _log.Add);

    private ExportShareDeliveryOptions Options(int keep = 14, string? csv = null) => new()
    {
        Root = _root, Environment = "production", Keep = keep, PaymentAgreementsCsv = csv, Shape = "test",
    };

    /// <summary>An export that writes the given customers as JSON Lines and returns <paramref name="exitCode"/>.</summary>
    private static Func<string, CancellationToken, Task<int>> Export(int exitCode, params (string Number, string Value)[] customers) =>
        async (path, ct) =>
        {
            await File.WriteAllLinesAsync(path,
                customers.Select(c => new JsonObject { ["customerNumber"] = c.Number, ["value"] = c.Value }.ToJsonString()), ct);
            return exitCode;
        };

    private async Task<int> RunAsync(Func<string, CancellationToken, Task<int>> export, ExportShareDeliveryOptions? options = null)
    {
        var code = await Delivery().RunAsync(options ?? Options(), export);
        _now = _now.AddDays(1);
        return code;
    }

    /// <summary>The folder of the run started <paramref name="day"/> days after the first (one run a day).</summary>
    private string Run(int day) => Path.Combine(EnvDir, Start.AddDays(day).UtcDateTime.ToString("yyyy-MM-dd'T'HHmmss'Z'"));

    private static List<JsonObject> Lines(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    private static JsonObject Manifest(string run) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(run, ExportShareDelivery.ManifestFile)))!.AsObject();

    [Fact]
    public async Task First_run_publishes_every_customer_as_added()
    {
        Assert.Equal(0, await RunAsync(Export(0, ("100", "a"), ("200", "b"))));

        var run = Run(0);
        Assert.True(File.Exists(Path.Combine(run, ExportShareDelivery.ReadyFile)));
        Assert.Equal(2, Lines(Path.Combine(run, ExportShareDelivery.CustomersFile)).Count);
        var delta = Lines(Path.Combine(run, ExportShareDelivery.DeltaFile));
        Assert.Equal(["added", "added"], delta.Select(d => (string)d["op"]!).ToArray());
        Assert.Equal("a", (string?)delta[0]["customer"]?["value"]);

        var manifest = Manifest(run);
        Assert.Equal(2, (int)manifest["customers"]!);
        Assert.Equal(2, (int)manifest["added"]!);
        Assert.Null(manifest["previousRun"]);
        Assert.Equal(2, manifest["files"]!.AsArray().Count);
        Assert.False(File.Exists(Path.Combine(EnvDir, ExportShareDelivery.LockFile)));
        Assert.DoesNotContain(Directory.GetDirectories(EnvDir), d => Path.GetFileName(d).StartsWith(".tmp-"));
    }

    [Fact]
    public async Task Next_run_delta_holds_added_changed_and_removed_only()
    {
        await RunAsync(Export(0, ("100", "a"), ("200", "b"), ("300", "c")));

        Assert.Equal(0, await RunAsync(Export(0, ("100", "a"), ("200", "B"), ("400", "d"))));

        var run = Run(1);
        var delta = Lines(Path.Combine(run, ExportShareDelivery.DeltaFile));
        Assert.Equal(
            ["changed:200", "added:400", "removed:300"],
            delta.Select(d => $"{d["op"]}:{d["customerNumber"]}").ToArray());
        Assert.Null(delta[2]["customer"]);
        var manifest = Manifest(run);
        Assert.Equal((1, 1, 1), ((int)manifest["added"]!, (int)manifest["changed"]!, (int)manifest["removed"]!));
        Assert.Equal(Path.GetFileName(Run(0)), (string?)manifest["previousRun"]);
    }

    [Fact]
    public async Task Failed_export_publishes_nothing_and_next_delta_is_against_the_last_published_run()
    {
        await RunAsync(Export(0, ("100", "a")));
        Assert.Equal(2, await RunAsync(Export(2, ("100", "changed in failed run"))));

        var failed = Run(1) + ".failed";
        Assert.True(Directory.Exists(failed));
        Assert.False(File.Exists(Path.Combine(failed, ExportShareDelivery.ReadyFile)));
        Assert.False(Directory.Exists(Run(1)));

        await RunAsync(Export(0, ("100", "a"), ("200", "b")));
        var delta = Lines(Path.Combine(Run(2), ExportShareDelivery.DeltaFile));
        Assert.Equal(["added:200"], delta.Select(d => $"{d["op"]}:{d["customerNumber"]}").ToArray());
    }

    [Fact]
    public async Task Existing_lock_stops_the_run_before_the_export()
    {
        Directory.CreateDirectory(EnvDir);
        await File.WriteAllTextAsync(Path.Combine(EnvDir, ExportShareDelivery.LockFile), "crashed run");
        var called = false;

        var code = await RunAsync((_, _) => { called = true; return Task.FromResult(0); });

        Assert.Equal(1, code);
        Assert.False(called);
        Assert.True(File.Exists(Path.Combine(EnvDir, ExportShareDelivery.LockFile)));
    }

    [Fact]
    public async Task Payment_agreement_file_must_exist_and_be_fresh()
    {
        var csv = Path.Combine(Path.GetTempPath(), $"agreements-{Guid.NewGuid():N}.csv");
        try
        {
            Assert.Equal(1, await RunAsync(Export(0, ("100", "a")), Options(csv: csv)));

            await File.WriteAllTextAsync(csv, "header\n");
            _now = new DateTimeOffset(File.GetLastWriteTimeUtc(csv), TimeSpan.Zero).AddHours(25);
            Assert.Equal(1, await Delivery().RunAsync(Options(csv: csv), Export(0, ("100", "a"))));

            _now = new DateTimeOffset(File.GetLastWriteTimeUtc(csv), TimeSpan.Zero).AddHours(1);
            Assert.Equal(0, await Delivery().RunAsync(Options(csv: csv), Export(0, ("100", "a"))));
            var run = Path.Combine(EnvDir, _now.UtcDateTime.ToString("yyyy-MM-dd'T'HHmmss'Z'"));
            Assert.Equal(Path.GetFileName(csv), (string?)Manifest(run)["paymentAgreements"]?["file"]);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task Keeps_the_newest_runs_only()
    {
        for (var i = 0; i < 4; i++) Assert.Equal(0, await RunAsync(Export(0, ("100", $"v{i}")), Options(keep: 2)));

        var runs = Directory.GetDirectories(EnvDir).Select(d => Path.GetFileName(d)!).Where(n => n[0] != '.' && n != "state").Order().ToArray();
        Assert.Equal([Path.GetFileName(Run(2))!, Path.GetFileName(Run(3))!], runs);
    }

    [Fact]
    public async Task Customer_twice_in_the_export_fails_the_run()
    {
        Assert.Equal(1, await RunAsync(Export(0, ("100", "a"), ("100", "b"))));

        Assert.True(Directory.Exists(Run(0) + ".failed"));
        Assert.False(File.Exists(Path.Combine(EnvDir, "state", "customers.json")));
    }
}
