using System.Text.Json.Nodes;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// End-to-end reconcile flow for Customer core fields:
///   Mutate Address.PostalAddress.Name1.FullName via raw v3 customer PUT
///   → Reconcile (detect FieldDiff)
///   → Reconcile-apply (restore via ApplyCustomerCoreAsync full-echo v3 PUT)
///   → Reconcile (verify restored)
///
/// Uses FixTestAccounts.HappyPath (448928).
///
/// Verifies:
/// - GetRawByIdV3Async + UpdateV3RawAsync (wrapped { Customer: {...}, TransactionId }) can mutate a leaf
/// - ScanRepresentativeAsync detects Address.PostalAddress.Name1.FullName as FieldDiff with IntendedJson
/// - ReconcileApplier.ApplyAsync navigates the path and restores via the wrapped v3 echo-PUT
/// - A second scan shows no FieldDiff for this field
/// </summary>
public class CustomerCoreReconcileFlowTests(ProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ProvisioningFixture>
{
    private static readonly ReconcileMappingOptions Mapping = new()
    {
        DataAreaId = "hol",
        DeliveryCategory = "El-ekstern",
    };

    private static readonly DateOnly Cos = DateOnly.FromDateTime(DateTime.Today);
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 0d);

    private const string TargetField = "Address.PostalAddress.Name1.FullName";
    private const string MutatedValue = "TESTCUSTOMER_MUTATION";

    [Fact]
    public async Task CustomerCore_FieldDiff_FullName_RestoredByApply()
    {
        var settl   = fixture.Settl;
        var connStr = fixture.XellentConnectionString;
        var runner  = BuildRunner(settl, connStr);
        var applier = BuildApplier(settl, connStr);

        // === Discover customer for HappyPath ===

        var (custInfo, custErr) = await settl.CustomerClient.SearchByCustomerNumberAsync(FixTestAccounts.HappyPath);
        Assert.True(custInfo != null,
            $"Customer 448928 not found in the fake tenant: {custErr}. Run ProvisioningFixture first.");

        var custId = custInfo!.Id;
        output.WriteLine($"Customer: {custId}");

        // === Step 1: read current v3 raw JSON + extract current FullName ===
        // GetRawByIdV3Async returns the unwrapped customer object (the v3 PUT wraps it: { Customer: {...}, TransactionId }).

        var (rawBefore, rawErr) = await settl.CustomerClient.GetRawByIdV3Async(custId);
        Assert.True(rawBefore != null, $"GetRawByIdV3Async failed: {rawErr}");

        var objBefore = JsonNode.Parse(rawBefore!)!.AsObject();
        var originalFullName = objBefore["Address"]?["PostalAddress"]?["Name1"]?["FullName"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(originalFullName),
            $"Address.PostalAddress.Name1.FullName is null/empty in v3 customer JSON — " +
            "cannot mutate and restore. Check that HappyPath customer has a Name1.FullName.");
        output.WriteLine($"[Setup] Original FullName = {originalFullName}");

        // === Step 2: scan before — no FullName FieldDiff ===

        var rowsBefore = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var driftsBefore = FullNameDriftsFor(rowsBefore);
        Assert.Empty(driftsBefore);
        output.WriteLine($"[Reconcile] No {TargetField} FieldDiff before mutation — OK");

        // === Step 3: mutate FullName via wrapped v3 PUT ===
        // Mirrors what ApplyCustomerCoreAsync does: GET v3 raw → modify leaf → remove Uri/Links → wrap → PUT.

        var objMutated = JsonNode.Parse(rawBefore!)!.AsObject();
        var name1Node = objMutated["Address"]?["PostalAddress"]?["Name1"]?.AsObject();
        Assert.True(name1Node != null, "Address.PostalAddress.Name1 not found in v3 customer JSON");
        name1Node!["FullName"] = MutatedValue;
        objMutated.Remove("Uri");
        objMutated.Remove("Links");
        var body = new JsonObject { ["Customer"] = objMutated, ["TransactionId"] = Guid.NewGuid().ToString() };

        var (mutated, mutateErr) = await settl.CustomerClient.UpdateV3RawAsync(custId, body.ToJsonString());
        Assert.True(mutated, $"UpdateV3RawAsync (set FullName = '{MutatedValue}') failed: {mutateErr}");
        output.WriteLine($"[Mutation] FullName changed to '{MutatedValue}'");

        try
        {
            // === Step 4: scan — FieldDiff detected on Address.PostalAddress.Name1.FullName ===

            var rowsAfterMutation = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterMutation = FullNameDriftsFor(rowsAfterMutation);
            Assert.True(driftsAfterMutation.Count > 0,
                $"Expected FieldDiff on {TargetField} after mutation to '{MutatedValue}', but none found. " +
                $"Customer core rows: {string.Join("; ", rowsAfterMutation.Where(r => r.Entity == "Customer" && r.SubType == "").Select(r => r.Field))}");
            output.WriteLine($"[Reconcile] {TargetField} FieldDiff detected ({driftsAfterMutation.Count} rows) — OK");

            var fullNameRow = driftsAfterMutation[0];
            Assert.False(string.IsNullOrEmpty(fullNameRow.IntendedJson),
                $"IntendedJson must be set on {TargetField} row so the applier can restore without capture. " +
                "JsonValueDiff sets IntendedJson for customer core (subType=\"\", entity=\"Customer\").");

            // === Step 5: apply ===

            DateOnly repCos = driftsAfterMutation.Select(r => r.Cos).FirstOrDefault(c => c.HasValue)
                           ?? driftsAfterMutation.Select(r => r.CosFrom).FirstOrDefault(c => c.HasValue)
                           ?? Cos;

            var applyRows = driftsAfterMutation
                .Select(r => new ReconcileApplyRow(r.Entity, r.SubType, r.EntityKey, r.Field, r.Kind.ToString(), r.IntendedJson))
                .ToList();

            var firstRow = driftsAfterMutation[0];
            var memberAccounts = firstRow.MemberAccounts?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              ?? [firstRow.RepresentativeAccount];

            var applyTarget = new ReconcileApplyTarget(firstRow.RepresentativeAccount, memberAccounts, repCos, applyRows);
            // RowNeedsCapture for Customer core FieldDiff = false (IntendedJson is carried on the row).
            // Env: null is harmless (no capture runs).
            var applyResult = await applier.ApplyAsync(
                Mapping,
                new ReconcileApplyRequest(Env: null, DryRun: false, Targets: [applyTarget]));

            output.WriteLine($"[Reconcile-apply] Applied={applyResult.Applied} NoLongerDrifts={applyResult.NoLongerDrifts} Failed={applyResult.Failed} Skipped={applyResult.Skipped}");
            foreach (var row in applyResult.Rows)
                output.WriteLine($"  {row.Entity}/{row.SubType} {row.EntityKey} [{row.Field}] → {row.Outcome}{(row.Detail != null ? " " + row.Detail : "")}");

            Assert.Equal(0, applyResult.Failed);
            Assert.True(applyResult.Applied > 0,
                $"Expected at least one Applied row for {TargetField} FieldDiff.");

            // === Step 6: scan — no FieldDiff for FullName ===

            var rowsAfterApply = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterApply = FullNameDriftsFor(rowsAfterApply);
            Assert.Empty(driftsAfterApply);
            output.WriteLine($"[Reconcile] No {TargetField} FieldDiff after apply — OK");
        }
        catch
        {
            // Safety net from the live version: restore the original value if the flow fails midway.
            var objRestore = JsonNode.Parse(rawBefore!)!.AsObject();
            objRestore.Remove("Uri");
            objRestore.Remove("Links");
            var restoreBody = new JsonObject { ["Customer"] = objRestore, ["TransactionId"] = Guid.NewGuid().ToString() };
            await settl.CustomerClient.UpdateV3RawAsync(custId, restoreBody.ToJsonString());
            throw;
        }
    }

    // ---- helpers ----

    private static List<ReconcileDriftRow> FullNameDriftsFor(IEnumerable<ReconcileDriftRow> rows)
        => [.. rows.Where(r =>
            r.Entity == "Customer" &&
            r.SubType == "" &&
            r.Kind == DriftKind.FieldDiff &&
            r.Field == TargetField)];

    private static ReconcileRunner BuildRunner(FakeSettl settl, string connStr) => new(
        settl.ApiOptions,
        settl.CustomerClient, settl.PayerClient, settl.MeteringPointClient,
        settl.ContractClient, settl.SettlementGroupClient,
        connStr, NoExemption);

    private static ReconcileApplier BuildApplier(FakeSettl settl, string connStr) => new(
        settl.ApiOptions,
        settl.CustomerClient, settl.PayerClient, settl.MeteringPointClient,
        settl.ContractClient, settl.SettlementGroupClient,
        connStr, NoExemption);
}
