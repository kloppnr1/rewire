using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// End-to-end reconcile flow for Contract→MP links:
///   Verify present → Delete → Reconcile (detect MissingInSettl)
///   → Reconcile-apply (restore) → Reconcile (verify restored)
///
/// Uses FixTestAccounts.HappyPath (448928) — single active
/// El-ekstern contract, 5 MPs, migrated into the fake Settl tenant by ProvisioningFixture.
///
/// The "create" step is performed by ProvisioningFixture (idempotent CreateRunner run).
/// The test picks the last MP link on the account's electricity contract.
///
/// Verifies:
/// - RemoveContractMeteringPointAsync (PascalCase ?TransactionId=) actually deletes the link
/// - ScanRepresentativeAsync detects the missing link as MissingInSettl
/// - ReconcileApplier.ApplyAsync restores the link (electricity SearchByIdentificationAsync fallback)
/// - A second scan shows the link is back (no MissingInSettl)
/// </summary>
public class ContractLinkReconcileFlowTests(ProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ProvisioningFixture>
{
    // Minimal reconcile mapping for the hol dataarea. No explicit contract-name mapping needed:
    // CapturePlanAsync resolves contracts by name from the live Settl contracts list.
    private static readonly ReconcileMappingOptions Mapping = new()
    {
        DataAreaId = "hol",
        DeliveryCategory = "El-ekstern",
    };

    // HappyPath contract is always active (enddate 1900-01-01 in Xellent), so today is a valid COS.
    // refDate = today-1; the contract is active on any date.
    private static readonly DateOnly Cos = DateOnly.FromDateTime(DateTime.Today);

    // BillingExemptionConfig with no exemption (used by runners that need the param but
    // HappyPath has no billing exemptions).
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 0d);

    [Fact]
    public async Task ContractLink_DeleteThenApply_RestoredAndDetectedCorrectly()
    {
        var settl   = fixture.Settl;
        var connStr = fixture.XellentConnectionString;
        var runner  = BuildRunner(settl, connStr);
        var applier = BuildApplier(settl, connStr);

        // === Discover: customer → electricity contract → last MP link ===

        var (customer, custErr) = await settl.CustomerClient.SearchByCustomerNumberAsync(FixTestAccounts.HappyPath);
        Assert.True(customer != null,
            $"HappyPath customer 448928 not in the fake tenant: {custErr}. Run ProvisioningFixture first.");

        var (customerContracts, _) = await settl.ContractClient.GetCustomerMeteringPointContractsAsync(customer!.Id);
        Assert.True(customerContracts?.Count > 0, "No contracts for customer 448928");

        var elContract = customerContracts!.FirstOrDefault(c =>
            c.MeteringPoints.Any(mp =>
                string.Equals(mp.CommodityType, "Electricity", StringComparison.OrdinalIgnoreCase)));
        Assert.True(elContract != null, "No electricity contract found for customer 448928");

        var contractId = elContract!.Id;
        output.WriteLine($"Contract: {contractId} ({elContract.ContractName})");

        // Pick the last MP that belongs to HappyPath (from the customer-scoped list, not the
        // full 700+ links on the shared "V Variabel" contract). Then look up the concrete link
        // object (with its link Id) from the contract's MP list.
        var targetCustomerMp = elContract.MeteringPoints[^1];
        output.WriteLine($"Target MP: mpId={targetCustomerMp.MeteringPointId} natId={targetCustomerMp.MeteringPointIdentification}");

        var (currentLinks, _) = await settl.ContractClient.GetContractMeteringPointsAsync(contractId);
        Assert.True(currentLinks?.Count > 0, $"No MP links on contract {contractId}");

        var targetLink = currentLinks!.FirstOrDefault(l => l.MeteringPointId == targetCustomerMp.MeteringPointId);
        Assert.True(targetLink != null,
            $"No link found for MP {targetCustomerMp.MeteringPointIdentification} on contract {contractId}. " +
            "HappyPath MPs should be linked here (ProvisioningFixture ran CreateRunner).");
        output.WriteLine($"Target: link={targetLink!.Id} mpId={targetLink.MeteringPointId} natId={targetLink.MeteringPointIdentification}");

        // === Step 1 (create / verify present): link exists in Settl ===
        // The link was created by ProvisioningFixture — this step is verification, not creation.

        Assert.Contains(currentLinks, l => l.Id == targetLink.Id);
        output.WriteLine($"[Create/Verify] Link {targetLink.Id} confirmed present ({currentLinks.Count} links total)");

        // === Step 2 (reconcile): scan before delete — no MissingInSettl for target link ===

        var rowsBefore = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var missingBefore = rowsBefore
            .Where(r => r.SubType == "ContractLink" && r.Kind == DriftKind.MissingInSettl
                     && r.EntityKey == targetLink.MeteringPointIdentification)
            .ToList();
        Assert.Empty(missingBefore);
        output.WriteLine($"[Reconcile] No MissingInSettl for {targetLink.MeteringPointIdentification} before delete — OK");

        // === Step 3 (delete): remove the link ===

        var (deleted, deleteErr) = await settl.ContractClient.RemoveContractMeteringPointAsync(contractId, targetLink.Id);
        Assert.True(deleted, $"RemoveContractMeteringPointAsync failed: {deleteErr}");
        output.WriteLine($"[Delete] Link {targetLink.Id} deleted");

        // === Step 4 (reconcile): scan after delete — MissingInSettl detected ===

        var rowsAfterDelete = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var missingAfterDelete = rowsAfterDelete
            .Where(r => r.SubType == "ContractLink" && r.Kind == DriftKind.MissingInSettl
                     && r.EntityKey == targetLink.MeteringPointIdentification)
            .ToList();
        Assert.True(missingAfterDelete.Count > 0,
            $"Expected MissingInSettl for {targetLink.MeteringPointIdentification} after delete, but none found. " +
            "Check that ReconcileRunner maps this MP's EntityKey correctly.");
        output.WriteLine($"[Reconcile] MissingInSettl detected for {targetLink.MeteringPointIdentification} ({missingAfterDelete.Count} rows) — OK");

        // === Step 5 (reconcile-apply): restore the link ===
        // Build the apply target directly from the drift rows (no CSV roundtrip needed).

        DateOnly repCos = missingAfterDelete.Select(r => r.Cos).FirstOrDefault(c => c.HasValue)
                       ?? missingAfterDelete.Select(r => r.CosFrom).FirstOrDefault(c => c.HasValue)
                       ?? Cos;

        var applyRows = missingAfterDelete
            .Select(r => new ReconcileApplyRow(r.Entity, r.SubType, r.EntityKey, r.Field, r.Kind.ToString(), r.IntendedJson))
            .ToList();

        var firstRow = missingAfterDelete[0];
        var memberAccounts = firstRow.MemberAccounts?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          ?? [firstRow.RepresentativeAccount];

        var applyTarget = new ReconcileApplyTarget(
            firstRow.RepresentativeAccount, memberAccounts, repCos, applyRows);

        var applyResult = await applier.ApplyAsync(
            Mapping,
            new ReconcileApplyRequest(Env: null, DryRun: false, Targets: [applyTarget]));

        output.WriteLine($"[Reconcile-apply] Applied={applyResult.Applied} NoLongerDrifts={applyResult.NoLongerDrifts} Failed={applyResult.Failed} Skipped={applyResult.Skipped}");
        foreach (var row in applyResult.Rows)
            output.WriteLine($"  {row.Entity}/{row.SubType} {row.EntityKey} [{row.Field}] → {row.Outcome}{(row.Detail != null ? " " + row.Detail : "")}");

        Assert.Equal(0, applyResult.Failed);
        Assert.True(applyResult.Applied > 0,
            "Expected at least one Applied row. Check that ReconcileApplier.ApplyContractLinksAsync " +
            "finds the MP via SearchByIdentificationAsync and posts the link successfully.");

        // === Step 6 (reconcile): scan after apply — MissingInSettl gone ===

        var rowsAfterApply = await runner.ScanRepresentativeAsync(Mapping, env: null, [FixTestAccounts.HappyPath], Cos);
        var missingAfterApply = rowsAfterApply
            .Where(r => r.SubType == "ContractLink" && r.Kind == DriftKind.MissingInSettl
                     && r.EntityKey == targetLink.MeteringPointIdentification)
            .ToList();
        Assert.Empty(missingAfterApply);
        output.WriteLine($"[Reconcile] No MissingInSettl for {targetLink.MeteringPointIdentification} after apply — OK");
    }

    // ---- helpers ----

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
