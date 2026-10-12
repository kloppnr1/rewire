using System.Text.Json.Nodes;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// End-to-end reconcile flow for Payer billing (PayerBilling) fields:
///   Mutate PayerBilling.Notes via UpdatePayerBillingAsync
///   → Reconcile (detect FieldDiff on PayerBilling.Notes)
///   → Reconcile-apply (restore via ApplyPayerBillingAsync)
///   → Reconcile (verify restored)
///
/// Uses FixTestAccounts.HappyPath (448928).
///
/// Verifies:
/// - UpdatePayerBillingAsync creates detectable billing drift
/// - ScanRepresentativeAsync emits PayerBilling.Notes as FieldDiff with SubType="Billing"
/// - ReconcileApplier.ApplyAsync patches the billing leaf via UpdatePayerBillingAsync
/// - A second scan shows no FieldDiff for Notes
/// </summary>
public class PayerBillingReconcileFlowTests(ProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ProvisioningFixture>
{
    private static readonly ReconcileMappingOptions Mapping = new()
    {
        DataAreaId = "hol",
        DeliveryCategory = "El-ekstern",
    };

    private static readonly DateOnly Cos = DateOnly.FromDateTime(DateTime.Today);
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 0d);

    private const string NotesField = "PayerBilling.Notes";
    private const string MutatedNotes = "TESTBILLING_MUTATION";

    [Fact]
    public async Task PayerBilling_FieldDiff_Notes_RestoredByApply()
    {
        var settl   = fixture.Settl;
        var connStr = settl.XellentConnectionString;
        var runner  = BuildRunner(settl, connStr);
        var applier = BuildApplier(settl, connStr);

        // === Discover payer for HappyPath ===

        var (payerInfo, searchErr) = await settl.PayerClient.SearchByPayerIdentificationAsync(FixTestAccounts.HappyPath, "DK");
        Assert.True(payerInfo != null,
            $"Payer for HappyPath (448928) not found in featuretest: {searchErr}. Run ProvisioningFixture first.");

        var payerId = payerInfo!.Id;
        output.WriteLine($"Payer: {payerId}");

        // === Step 1: read current Notes value (for safety-net restore) ===

        var (payerBefore, getBeforeErr) = await settl.PayerClient.GetByIdAsync(payerId);
        Assert.True(payerBefore != null, $"GetByIdAsync failed: {getBeforeErr}");
        var originalNotes = payerBefore!.PayerBilling?.Notes;
        output.WriteLine($"[Setup] Current PayerBilling.Notes = {(originalNotes == null ? "<null>" : $"\"{originalNotes}\"")}");

        // Avoid mutating to the same value (idempotency edge case)
        var mutatedNotes = originalNotes == MutatedNotes ? MutatedNotes + "_2" : MutatedNotes;

        // === Step 2: scan before — no Notes FieldDiff ===

        var rowsBefore = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var driftsBefore = NotesDriftsFor(rowsBefore);
        Assert.Empty(driftsBefore);
        output.WriteLine("[Reconcile] No PayerBilling.Notes FieldDiff before mutation — OK");

        // === Step 3: mutate Notes in Settl via UpdatePayerBillingAsync ===

        var (mutated, mutateErr) = await settl.PayerClient.UpdatePayerBillingAsync(
            payerId,
            new Dictionary<string, JsonNode?> { ["Notes"] = JsonValue.Create(mutatedNotes) });
        Assert.True(mutated, $"UpdatePayerBillingAsync (set Notes = '{mutatedNotes}') failed: {mutateErr}");
        output.WriteLine($"[Mutation] Notes changed to '{mutatedNotes}'");

        try
        {
            // === Step 4: scan — FieldDiff detected on PayerBilling.Notes ===

            var rowsAfterMutation = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterMutation = NotesDriftsFor(rowsAfterMutation);
            Assert.True(driftsAfterMutation.Count > 0,
                $"Expected PayerBilling.Notes FieldDiff after mutation to '{mutatedNotes}', but none found. " +
                $"Payer billing rows: {string.Join("; ", rowsAfterMutation.Where(r => r.Entity == "Payer" && r.SubType == "Billing").Select(r => r.Field))}");
            output.WriteLine($"[Reconcile] PayerBilling.Notes FieldDiff detected ({driftsAfterMutation.Count} rows) — OK");

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
            // Env: null — needed if capture runs (when intended Notes is null → IntendedJson=null → RowNeedsCapture=true)
            var applyResult = await applier.ApplyAsync(
                Mapping,
                new ReconcileApplyRequest(Env: null, DryRun: false, Targets: [applyTarget]));

            output.WriteLine($"[Reconcile-apply] Applied={applyResult.Applied} NoLongerDrifts={applyResult.NoLongerDrifts} Failed={applyResult.Failed} Skipped={applyResult.Skipped}");
            foreach (var row in applyResult.Rows)
                output.WriteLine($"  {row.Entity}/{row.SubType} {row.EntityKey} [{row.Field}] → {row.Outcome}{(row.Detail != null ? " " + row.Detail : "")}");

            Assert.Equal(0, applyResult.Failed);
            Assert.True(applyResult.Applied > 0,
                $"Expected at least one Applied row for PayerBilling.Notes FieldDiff.");

            // === Step 6: scan — no Notes FieldDiff ===

            var rowsAfterApply = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterApply = NotesDriftsFor(rowsAfterApply);
            Assert.Empty(driftsAfterApply);
            output.WriteLine("[Reconcile] No PayerBilling.Notes FieldDiff after apply — OK");
        }
        catch
        {
            // Safety net: restore original Notes so featuretest is left clean even on test failure.
            await settl.PayerClient.UpdatePayerBillingAsync(
                payerId,
                new Dictionary<string, JsonNode?> { ["Notes"] = originalNotes != null ? JsonValue.Create(originalNotes) : null });
            throw;
        }
    }

    // ---- helpers ----

    private static List<ReconcileDriftRow> NotesDriftsFor(IEnumerable<ReconcileDriftRow> rows)
        => [.. rows.Where(r =>
            r.Entity == "Payer" &&
            r.SubType == "Billing" &&
            r.Kind == DriftKind.FieldDiff &&
            r.Field == NotesField)];

    private static ReconcileRunner BuildRunner(SettlFeaturetestFixture settl, string connStr) => new(
        settl.ApiOptions,
        settl.CustomerClient, settl.PayerClient, settl.MeteringPointClient,
        settl.ContractClient, settl.SettlementGroupClient,
        connStr, NoExemption);

    private static ReconcileApplier BuildApplier(SettlFeaturetestFixture settl, string connStr) => new(
        settl.ApiOptions,
        settl.CustomerClient, settl.PayerClient, settl.MeteringPointClient,
        settl.ContractClient, settl.SettlementGroupClient,
        connStr, NoExemption);
}
