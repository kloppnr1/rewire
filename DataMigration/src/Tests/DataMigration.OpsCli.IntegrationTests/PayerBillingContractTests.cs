using System.Text.Json.Nodes;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// Verifies that the Settl API honours the create/GET field-name asymmetry for payer billing:
/// create+PUT uses <c>VatIdentificationNumber</c>, GET returns <c>OrganizationIdentifier</c>.
///
/// <para>Failing tests indicate a Settl API contract change that will break reconcile scanning —
/// the typed intersection diff silently drops both fields when the names diverge, causing CVR
/// drift to become invisible (regression: 2026-06-16 fix(reconcile) 1c0349c3).</para>
///
/// <para>All tests are read/update-only against the dedicated contract-test payer — no creates.
/// Original CVR is restored in teardown.</para>
/// </summary>
public class PayerBillingContractTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>, IAsyncLifetime
{
    // Synthetic CVR so the test value is identifiable in a request log.
    private const string TestCvr = "99887766";

    private string? _originalCvr;
    private bool _setupComplete;

    public async Task InitializeAsync()
    {
        output.WriteLine($"Using payer {fixture.PayerId} (contract-test payer)");

        // Snapshot current CVR so teardown can restore it exactly.
        var (json, _) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        _originalCvr = json != null ? ExtractBillingField(json, "organizationIdentifier") : null;
        output.WriteLine($"Original OrganizationIdentifier = {_originalCvr ?? "(null)"}");

        _setupComplete = true;
    }

    public async Task DisposeAsync()
    {
        if (!_setupComplete) return;

        // Restore the original CVR regardless of test outcome.
        var patches = new Dictionary<string, JsonNode?>
        {
            ["OrganizationIdentifier"] = _originalCvr != null ? JsonValue.Create(_originalCvr) : null,
        };
        var (ok, err) = await fixture.Settl.PayerClient.UpdatePayerBillingAsync(fixture.PayerId, patches);
        if (!ok)
            output.WriteLine($"Teardown warning: restore OrganizationIdentifier failed: {err}");
    }

    /// <summary>
    /// Settl GET response must contain <c>organizationIdentifier</c> (not <c>vatIdentificationNumber</c>)
    /// in the PayerBilling object.
    ///
    /// <para>Fails if Settl renames the GET field — update <see cref="PayerBillingDto.OrganizationIdentifier"/>
    /// and <c>ReconcileRunner</c>'s explicit CVR comparison accordingly.</para>
    /// </summary>
    [Fact]
    public async Task GetRaw_PayerBilling_HasOrganizationIdentifier_NotVatIdentificationNumber()
    {
        var (json, err) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        Assert.True(json != null, $"GET raw payer {fixture.PayerId} failed: {err}");

        var billing = FindPayerBilling(json!);
        Assert.NotNull(billing);

        var keys = billing!.Select(kv => kv.Key).ToList();
        output.WriteLine($"PayerBilling raw keys: {string.Join(", ", keys)}");

        var hasOrg = keys.Any(k => k.Equals("organizationIdentifier", StringComparison.OrdinalIgnoreCase));
        var hasVat = keys.Any(k => k.Equals("vatIdentificationNumber", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasOrg,
            "GET response PayerBilling does not contain 'organizationIdentifier' — " +
            "Settl may have renamed this field. Update PayerBillingDto and ReconcileRunner.");
        Assert.False(hasVat,
            "GET response PayerBilling contains 'vatIdentificationNumber' — the GET/PUT asymmetry " +
            "may have been collapsed. Update PayerBillingDto and CreatePayerBillingDto accordingly.");
    }

    /// <summary>
    /// Patching <c>OrganizationIdentifier</c> via <see cref="ISettlPayerClient.UpdatePayerBillingAsync"/>
    /// must persist and be retrievable via GET.
    ///
    /// <para>Fails if <c>UpdatePayerBillingAsync</c>'s internal translation
    /// (<c>OrganizationIdentifier</c> → <c>VatIdentificationNumber</c> in PUT body) breaks —
    /// either because Settl renamed the PUT field or because the translate step was removed.</para>
    /// </summary>
    [Fact]
    public async Task UpdatePayerBilling_WithOrganizationIdentifierKey_PersistsViaGetRaw()
    {
        var patches = new Dictionary<string, JsonNode?> { ["OrganizationIdentifier"] = JsonValue.Create(TestCvr) };
        var (ok, patchErr) = await fixture.Settl.PayerClient.UpdatePayerBillingAsync(fixture.PayerId, patches);
        Assert.True(ok, $"UpdatePayerBillingAsync failed: {patchErr}");

        var (json, getErr) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        Assert.True(json != null, $"GET raw after update failed: {getErr}");

        var actualCvr = ExtractBillingField(json!, "organizationIdentifier");
        output.WriteLine($"organizationIdentifier after patch = {actualCvr}");

        Assert.Equal(TestCvr, actualCvr);
    }

    // ---- helpers ----

    private static JsonObject? FindPayerBilling(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;
        foreach (var kv in root)
            if (kv.Key.Equals("payerBilling", StringComparison.OrdinalIgnoreCase))
                return kv.Value?.AsObject();
        return null;
    }

    private static string? ExtractBillingField(string json, string fieldName)
    {
        var billing = FindPayerBilling(json);
        if (billing == null) return null;
        foreach (var kv in billing)
            if (kv.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                return kv.Value?.GetValue<string>();
        return null;
    }
}
