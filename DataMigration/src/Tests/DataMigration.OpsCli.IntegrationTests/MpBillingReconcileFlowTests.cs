using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// End-to-end reconcile flow for MP BillingConfiguration:
///   Mutate BillingConfiguration.JointInvoice → Reconcile (detect FieldDiff)
///   → Reconcile-apply (restore) → Reconcile (verify restored)
///
/// Uses FixTestAccounts.HappyPath (448928) and its first electricity MP ([0]).
///
/// Only BillingConfiguration is covered. PaymentConfiguration is deliberately not: the create
/// pipeline attaches one only when the MP has an e-invoicing override in Xellent
/// (HasPaymentOverride over ElectronicNumber / OrderIdentifier / AgreementIdentifier /
/// BuyerReference), and without an intended config the intersection diff in JsonValueDiff cannot
/// produce per-field PaymentConfiguration.* rows at all. A test pinned to an MP without an
/// override can therefore never pass.
///
/// Verifies:
/// - UpdateBillingAsync with a flipped JointInvoice value creates detectable drift
/// - ScanRepresentativeAsync emits BillingConfiguration.JointInvoice as FieldDiff
///   (with IntendedJson carrying the Xellent-intended value)
/// - ReconcileApplier.ApplyAsync restores JointInvoice via WriteMpConfigVersionAsync
/// - A second scan shows no FieldDiff for this field
/// </summary>
public class MpBillingReconcileFlowTests(ProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ProvisioningFixture>
{
    private static readonly ReconcileMappingOptions Mapping = new()
    {
        DataAreaId = "hol",
        DeliveryCategory = "El-ekstern",
    };

    private static readonly DateOnly Cos = DateOnly.FromDateTime(DateTime.Today);
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 0d);

    [Fact]
    public async Task MpBilling_FieldDiff_JointInvoice_RestoredByApply()
    {
        var settl   = fixture.Settl;
        var connStr = fixture.XellentConnectionString;
        var runner  = BuildRunner(settl, connStr);
        var applier = BuildApplier(settl, connStr);

        // === Discover: first electricity MP for HappyPath ===

        var (customer, custErr) = await settl.CustomerClient.SearchByCustomerNumberAsync(FixTestAccounts.HappyPath);
        Assert.True(customer != null,
            $"HappyPath customer 448928 not in the fake tenant: {custErr}. Run ProvisioningFixture first.");

        var (customerContracts, _) = await settl.ContractClient.GetCustomerMeteringPointContractsAsync(customer!.Id);
        var elContract = customerContracts!.FirstOrDefault(c =>
            c.MeteringPoints.Any(mp =>
                string.Equals(mp.CommodityType, "Electricity", StringComparison.OrdinalIgnoreCase)));
        Assert.True(elContract != null, "No electricity contract found for customer 448928");
        Assert.True(elContract!.MeteringPoints.Count > 0, "Electricity contract has no MPs");

        // [0] — leave [^1] and [^2] for ContractLinkReconcileFlowTests
        var targetMp = elContract.MeteringPoints[0];
        output.WriteLine($"Target MP: {targetMp.MeteringPointIdentification} (Settl ID: {targetMp.MeteringPointId})");

        // === Step 1: read current billing config ===

        var (billing, billingErr) = await settl.MeteringPointClient.GetBillingByIdAsync(targetMp.MeteringPointId);
        Assert.True(billing?.BillingConfiguration != null,
            $"No billing config on MP {targetMp.MeteringPointIdentification}: {billingErr}");

        var orig = billing!.BillingConfiguration!;
        var origJointInvoice = orig.JointInvoice;
        output.WriteLine($"[Setup] Current JointInvoice = {origJointInvoice}");

        // === Step 2: scan before — no JointInvoice FieldDiff ===

        var rowsBefore = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var driftsBefore = JointInvoiceDriftsFor(rowsBefore, targetMp.MeteringPointIdentification!);
        Assert.Empty(driftsBefore);
        output.WriteLine($"[Reconcile] No BillingConfiguration.JointInvoice FieldDiff before mutation — OK");

        // === Step 3: flip JointInvoice (mutate Settl away from Xellent intent) ===

        var (mutated, mutateErr) = await settl.MeteringPointClient.UpdateBillingAsync(
            targetMp.MeteringPointId,
            new UpdateBillingMeteringPointRequest
            {
                BillingConfiguration = new BillingConfigurationDto
                {
                    Start                   = orig.Start,
                    LifeTime                = orig.LifeTime,
                    End                     = orig.End,
                    JointInvoice            = !origJointInvoice,
                    JointInvoiceNumber      = orig.JointInvoiceNumber,
                    Currency                = orig.Currency ?? "DKK",
                    NextInvoiceManual       = orig.NextInvoiceManual,
                    AutomaticApprovalOfDraft = orig.AutomaticApprovalOfDraft,
                }
            });
        Assert.True(mutated, $"UpdateBillingAsync (flip JointInvoice) failed: {mutateErr}");
        output.WriteLine($"[Mutation] JointInvoice flipped to {!origJointInvoice}");

        try
        {
            // === Step 4: scan — FieldDiff detected on BillingConfiguration.JointInvoice ===

            var rowsAfterMutation = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterMutation = JointInvoiceDriftsFor(rowsAfterMutation, targetMp.MeteringPointIdentification!);
            Assert.True(driftsAfterMutation.Count > 0,
                $"Expected BillingConfiguration.JointInvoice FieldDiff after flip, but none found. " +
                $"Scan rows for this MP: {string.Join("; ", rowsAfterMutation.Where(r => r.EntityKey == targetMp.MeteringPointIdentification).Select(r => r.Field))}");
            output.WriteLine($"[Reconcile] JointInvoice FieldDiff detected ({driftsAfterMutation.Count} rows) — OK");

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
            var applyResult = await applier.ApplyAsync(
                Mapping,
                new ReconcileApplyRequest(Env: null, DryRun: false, Targets: [applyTarget]));

            output.WriteLine($"[Reconcile-apply] Applied={applyResult.Applied} NoLongerDrifts={applyResult.NoLongerDrifts} Failed={applyResult.Failed} Skipped={applyResult.Skipped}");
            foreach (var row in applyResult.Rows)
                output.WriteLine($"  {row.Entity}/{row.SubType} {row.EntityKey} [{row.Field}] → {row.Outcome}{(row.Detail != null ? " " + row.Detail : "")}");

            Assert.Equal(0, applyResult.Failed);
            Assert.True(applyResult.Applied > 0,
                "Expected at least one Applied row for BillingConfiguration.JointInvoice FieldDiff.");

            // === Step 6: scan — no FieldDiff for JointInvoice ===

            var rowsAfterApply = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
            var driftsAfterApply = JointInvoiceDriftsFor(rowsAfterApply, targetMp.MeteringPointIdentification!);
            Assert.Empty(driftsAfterApply);
            output.WriteLine($"[Reconcile] No JointInvoice FieldDiff after apply — OK");
        }
        catch
        {
            // Safety net from the live version: restore the original value if the test fails
            // mid-flow. Swallow any error here — the primary failure already surfaced above.
            await settl.MeteringPointClient.UpdateBillingAsync(
                targetMp.MeteringPointId,
                new UpdateBillingMeteringPointRequest
                {
                    BillingConfiguration = new BillingConfigurationDto
                    {
                        Start                    = orig.Start,
                        LifeTime                 = orig.LifeTime,
                        End                      = orig.End,
                        JointInvoice             = origJointInvoice,
                        JointInvoiceNumber       = orig.JointInvoiceNumber,
                        Currency                 = orig.Currency ?? "DKK",
                        NextInvoiceManual        = orig.NextInvoiceManual,
                        AutomaticApprovalOfDraft = orig.AutomaticApprovalOfDraft,
                    }
                });
            throw;
        }
    }

    // ---- helpers ----

    private static List<ReconcileDriftRow> JointInvoiceDriftsFor(
        IEnumerable<ReconcileDriftRow> rows, string mpNatId)
        => [.. rows.Where(r =>
            r.Entity == "MeteringPoint" &&
            r.SubType == "Billing" &&
            r.Kind == DriftKind.FieldDiff &&
            r.EntityKey == mpNatId &&
            r.Field == "BillingConfiguration.JointInvoice")];

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
