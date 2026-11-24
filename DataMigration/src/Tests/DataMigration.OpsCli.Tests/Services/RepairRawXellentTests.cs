using DataMigration.OpsCli.Services;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DataMigration.OpsCli.Tests.Services;

public class RepairRawXellentTests
{
    [Fact]
    public async Task Capture_preserves_invalid_values_and_closed_parts_without_mapping_or_findings()
    {
        var root = Path.Combine(Path.GetTempPath(), "repair-raw-test-" + Guid.NewGuid());
        try
        {
            await using var db = new XellentDbContext(new DbContextOptionsBuilder<XellentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString(), o => o.EnableNullChecks(false)).Options);
            var customer = Blank<CustTable>(); customer.Dataareaid = "for"; customer.Accountnum = "12345";
            customer.Email = "invalid email retained"; customer.Partyid = "party";
            var contract = Blank<ExuContractTable>(); contract.Dataareaid = "for"; contract.Contractnum = "contract";
            contract.Custaccount = "12345"; contract.Companyid = "for";
            var part = Blank<ExuContractPartTable>(); part.Dataareaid = "for"; part.Contractnum = "contract";
            part.Instagreenum = "installation"; part.Companyid = "for"; part.Productnum = "charger";
            part.Deliverycategory = "Charge_Abo"; part.Startdate = new(2024, 1, 1); part.Enddate = new(2026, 8, 1);
            var installation = Blank<ExuInstallation>(); installation.Dataareaid = "for";
            installation.Instnum = "installation"; installation.Addressid = "invalid address id retained";
            db.AddRange(customer, contract, part, installation); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var scope = new StatePullScope(["571313113143520215"], ["571313113143520215"], ["12345"], ["12345"]);
            var store = StateSourceSnapshotStore.Open(root, "production", scope); await store.BeginAsync(default);
            await RepairRawXellent.CaptureRowsAsync(store, db, new(1, "for", new(2026, 8, 31), [], null, null, null), default);
            await store.CompleteAsync(default);
            Assert.Equal("invalid email retained", Assert.Single(await store.ReadSectionAsync<CustTable>(RepairRawXellent.Section(typeof(CustTable)), default)).Email);
            Assert.Equal("invalid address id retained", Assert.Single(await store.ReadSectionAsync<ExuInstallation>(RepairRawXellent.Section(typeof(ExuInstallation)), default)).Addressid);
            Assert.Equal(new DateTime(2026, 8, 1), Assert.Single(await store.ReadSectionAsync<ExuContractPartTable>(RepairRawXellent.Section(typeof(ExuContractPartTable)), default)).Enddate);
            Assert.False(store.HasSection("repair-xellent"));
            Assert.DoesNotContain(Directory.GetFiles(root), f => f.EndsWith(".csv"));
            part.Contractnum = "CONTRACT ";
            await store.WriteAsync(RepairRawXellent.Section(typeof(ExuContractPartTable)), new[] { part }, default);
            await using var copy = await RepairRawXellent.OpenOfflineAsync(store, default);
            Assert.Equal(customer.Email, (await copy.CustTables.SingleAsync()).Email);
            Assert.Equal(1, await copy.CustTables.CountAsync(c => c.Dataareaid == "FOR"));
            Assert.Equal(1, await copy.ExuContractPartTables.CountAsync(p => new[] { "contract" }.Contains(p.Contractnum)));
            Assert.Equal(1, await (from c in copy.ExuContractTables join p in copy.ExuContractPartTables
                on new { c.Dataareaid, c.Contractnum } equals new { p.Dataareaid, p.Contractnum } select p).CountAsync());
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json")));
            Assert.True(manifest.RootElement.GetProperty("Complete").GetBoolean());
            var duplicate = JsonSerializer.Deserialize<CustTable>(JsonSerializer.Serialize(customer))!;
            duplicate.Email = "second original value";
            await store.WriteAsync(RepairRawXellent.Section(typeof(CustTable)), new[] { customer, duplicate }, default);
            await using var duplicateCopy = await RepairRawXellent.OpenOfflineAsync(store, default);
            Assert.Equal(2, await duplicateCopy.CustTables.CountAsync());
            Assert.Contains(await duplicateCopy.CustTables.Select(c => c.Email).ToListAsync(), email => email == duplicate.Email);
            await File.AppendAllTextAsync(Path.Combine(root, RepairRawXellent.Section(typeof(CustTable)) + ".jsonl"), " ");
            await Assert.ThrowsAsync<InvalidOperationException>(() => RepairRawXellent.OpenOfflineAsync(store, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Interrupted_copy_remains_incomplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "repair-raw-test-" + Guid.NewGuid());
        try
        {
            await using var db = new XellentDbContext(new DbContextOptionsBuilder<XellentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString(), o => o.EnableNullChecks(false)).Options);
            var store = StateSourceSnapshotStore.Open(root, "production", new(["12345"], [], ["12345"], ["12345"]));
            await store.BeginAsync(default);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepairRawXellent.CaptureRowsAsync(store, db,
                new(1, "for", new(2026, 8, 31), [], null, null, null), cancelled.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadManifestAsync(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Blank<T>() where T : new()
    {
        var row = new T();
        foreach (var p in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite)) p.SetValue(row, "");
        return row;
    }
}
