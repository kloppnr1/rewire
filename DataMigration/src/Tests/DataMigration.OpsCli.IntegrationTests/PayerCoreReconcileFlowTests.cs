using System.Text.Json.Nodes;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// End-to-end reconcile flow for Payer core (PayerInformation) fields:
///   Mutate PayerInformation.PostalAddress.Name1.FullName via raw PUT
///   → Reconcile (detect FieldDiff with IntendedJson carrying Xellent name)
///   → Reconcile-apply (restore via ApplyCoreLeaves echo-PUT)
///   → Reconcile (verify restored)
///
/// Uses FixTestAccounts.HappyPath (448928). Payer is looked up by PayerIdentification = Accountnum.
///
/// Verifies:
/// - GetRawByIdAsync + UpdateRawAsync can roundtrip the payer JSON with a modified leaf
/// - ScanRepresentativeAsync detects the mutated field as FieldDiff with IntendedJson set
/// - ReconcileApplier.ApplyAsync navigates the raw JSON path and restores the leaf
/// - A second scan shows no FieldDiff for this field
/// </summary>
public class PayerCoreReconcileFlowTests(ProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ProvisioningFixture>
{
    private static readonly ReconcileMappingOptions Mapping = new()
    {
        DataAreaId = "hol",
        DeliveryCategory = "El-ekstern",
    };

    private static readonly DateOnly Cos = DateOnly.FromDateTime(DateTime.Today);
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 0d);

    private const string TargetField = "PayerInformation.PostalAddress.Name1.FullName";
    private const string MutatedValue = "TESTPAYER";

    [Fact]
    public async Task PayerCore_FieldDiff_FullName_RestoredByApply()
    {
        var settl   = fixture.Settl;
        var connStr = fixture.XellentConnectionString;
        var runner  = BuildRunner(settl, connStr);
        var applier = BuildApplier(settl, connStr);

        // === Discover: payer for HappyPath ===

        var (payerInfo, searchErr) = await settl.PayerClient.SearchByPayerIdentificationAsync(FixTestAccounts.HappyPath, "DK");
        Assert.True(payerInfo != null,
            $"Payer for HappyPath (448928) not found in the fake tenant: {searchErr}. Run ProvisioningFixture first.");

        var payerId = payerInfo!.Id;
        output.WriteLine($"Payer: {payerId}");

        // === Step 1: read current raw payer JSON + extract current FullName ===

        var (rawBefore, rawErr) = await settl.PayerClient.GetRawByIdAsync(payerId);
        Assert.True(rawBefore != null, $"GetRawByIdAsync failed: {rawErr}");

        var objBefore = JsonNode.Parse(rawBefore!)!.AsObject();
        var originalFullName = objBefore["PayerInformation"]?["PostalAddress"]?["Name1"]?["FullName"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(originalFullName),
            $"PayerInformation.PostalAddress.Name1.FullName is null/empty in raw payer JSON — " +
            "cannot mutate and restore. Check that HappyPath payer has a Name1.FullName.");
        output.WriteLine($"[Setup] Original FullName = {originalFullName}");

        // === Step 2: scan before — no FullName FieldDiff ===

        var rowsBefore = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var driftsBefore = FullNameDriftsFor(rowsBefore);
        Assert.Empty(driftsBefore);
        output.WriteLine($"[Reconcile] No {TargetField} FieldDiff before mutation — OK");

        // === Step 3: mutate FullName in Settl via raw PUT ===
        // Mirrors what ApplyCoreLeaves does: GET raw → modify leaf → remove Uri/Links → add TransactionId → PUT.

        var objMutated = JsonNode.Parse(rawBefore!)!.AsObject();
        var name1Node = objMutated["PayerInformation"]?["PostalAddress"]?["Name1"]?.AsObject();
        Assert.True(name1Node != null, "PayerInformation.PostalAddress.Name1 not found in raw JSON");
        name1Node!["FullName"] = MutatedValue;
        objMutated.Remove("Uri");
        objMutated.Remove("Links");
        objMutated["TransactionId"] = Guid.NewGuid().ToString();

        var (mutated, mutateErr) = await settl.PayerClient.UpdateRawAsync(payerId, objMutated.ToJsonString());
        Assert.True(mutated, $"UpdateRawAsync (set FullName = TESTPAYER) failed: {mutateErr}");
        output.WriteLine($"[Mutation] FullName changed to '{MutatedValue}'");

        try
        {
            // === Step 4: scan — FieldDiff detected on PayerInformation.PostalAddress.Name1.FullName ===

            var rowsAfterMutation = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterMutation = FullNameDriftsFor(rowsAfterMutation);
            Assert.True(driftsAfterMutation.Count > 0,
                $"Expected FieldDiff on {TargetField} after mutation to '{MutatedValue}', but none found. " +
                $"Payer core rows: {string.Join("; ", rowsAfterMutation.Where(r => r.Entity == "Payer" && r.SubType == "").Select(r => r.Field))}");
            output.WriteLine($"[Reconcile] {TargetField} FieldDiff detected ({driftsAfterMutation.Count} rows) — OK");

            var fullNameRow = driftsAfterMutation[0];
            Assert.False(string.IsNullOrEmpty(fullNameRow.IntendedJson),
                $"IntendedJson must be set on {TargetField} row so the applier can restore without a capture plan. " +
                "If null, JsonValueDiff.Diff returns IntendedJson=null for intended=null leaves — " +
                "check that the Xellent plan has a non-null FullName for HappyPath.");

            // === Step 5: apply — pass only the FullName FieldDiff row ===

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
            objRestore["TransactionId"] = Guid.NewGuid().ToString();
            await settl.PayerClient.UpdateRawAsync(payerId, objRestore.ToJsonString());
            throw;
        }
    }

    // ---- helpers ----

    private static List<ReconcileDriftRow> FullNameDriftsFor(IEnumerable<ReconcileDriftRow> rows)
        => [.. rows.Where(r =>
            r.Entity == "Payer" &&
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
