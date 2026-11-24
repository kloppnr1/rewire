using System.Text.Json.Nodes;
using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// Contract tests for the detailed MP APIs: market v3 GET-by-ID (typed),
/// billing v1 PaymentConfiguration, and billing history.
///
/// <para>Market v3 typed GET is used in reconcile projectors and in fix-settlement-groups.
/// Billing v1 PaymentConfiguration is used in reconcile payer-billing diff and in the
/// fix-electronic-number runner (now via reconcile-apply). If Settl renames a field here
/// the runner's PUT echo silently drops the field.</para>
/// </summary>
public class SettlMpDetailContractTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>
{
    // =========================================================
    // Market v3 — typed GET by ID (MeteringPointCurrentDto)
    // =========================================================

    [Fact]
    public async Task MpV3_GetByIdTyped_ReturnsNonNull()
    {
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (current, err) = await mp.GetByIdAsync(fixture.MpId);
        output.WriteLine($"Market v3 GetByIdAsync for {fixture.MpId}: {(current != null ? "OK" : err)}");
        Assert.True(current != null, $"GetByIdAsync returned null: {err}");
    }

    [Fact]
    public async Task MpV3_Typed_Identification_MatchesNationalId()
    {
        // MeteringPointCurrentDto.Identification.MeteringPointIdentification is the 18-digit
        // national MP ID used in reconcile projectors and market v3 raw GET.
        // If Settl renames Identification or MeteringPointIdentification, the projectors return null.
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (current, err) = await mp.GetByIdAsync(fixture.MpId);
        Assert.True(current != null, $"GetByIdAsync failed: {err}");

        var identification = current!.Identification?.MeteringPointIdentification;
        output.WriteLine($"MeteringPointCurrentDto.Identification.MeteringPointIdentification = {identification}");
        Assert.False(string.IsNullOrEmpty(identification),
            "Identification.MeteringPointIdentification is null — Settl may have restructured the Identification section");

        if (!string.IsNullOrEmpty(fixture.MpNationalId))
            Assert.Equal(fixture.MpNationalId, identification);
    }

    [Fact]
    public async Task MpV3_Typed_Grid_IsPresent()
    {
        // Grid section holds MarketBalanceAreaId + MeteringGridAreaId used in reconcile grid drift.
        // If Settl drops Grid from the v3 GET response, reconcile can't detect grid changes.
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (current, err) = await mp.GetByIdAsync(fixture.MpId);
        Assert.True(current != null, $"GetByIdAsync failed: {err}");

        output.WriteLine($"MeteringPointCurrentDto.Grid = {(current!.Grid != null ? "present" : "null")}");
        Assert.NotNull(current.Grid);
    }

    [Fact]
    public async Task MpV3_Typed_Grid_HasMarketBalanceAreaId()
    {
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (current, err) = await mp.GetByIdAsync(fixture.MpId);
        Assert.True(current != null, $"GetByIdAsync failed: {err}");

        output.WriteLine($"Grid.MarketBalanceAreaId = {current!.Grid?.MarketBalanceAreaId}");
        Assert.NotNull(current.Grid?.MarketBalanceAreaId);
        Assert.NotEqual(Guid.Empty, current.Grid!.MarketBalanceAreaId!.Value);
    }

    [Fact]
    public async Task MpV3_Typed_MeterCharacteristics_IsPresent()
    {
        // MeterCharacteristics holds EstimatedAnnualVolume (reconcile annual-volume projector)
        // and SettlementMethod. If dropped, annual-volume drift becomes invisible.
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (current, err) = await mp.GetByIdAsync(fixture.MpId);
        Assert.True(current != null, $"GetByIdAsync failed: {err}");

        output.WriteLine($"MeteringPointCurrentDto.MeterCharacteristics = {(current!.MeterCharacteristics != null ? "present" : "null")}");
        Assert.NotNull(current.MeterCharacteristics);
    }

    [Fact]
    public async Task MpV3_Raw_HasInstallationAddress_Section()
    {
        // InstallationAddress drives address-drift detection in reconcile. If Settl renames
        // this section (e.g. to "address"), the reconcile projector reads null and misses drift.
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (json, err) = await mp.GetByIdRawAsync(fixture.MpId);
        Assert.True(json != null, $"GetByIdRawAsync failed: {err}");

        var root = JsonNode.Parse(json!)?.AsObject();
        Assert.NotNull(root);

        var hasAddress = root!.Any(kv =>
            kv.Key.Equals("installationAddress", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"Market v3 raw has 'installationAddress': {hasAddress}");
        output.WriteLine($"Top-level keys: {string.Join(", ", root.Select(kv => kv.Key))}");
        Assert.True(hasAddress,
            "Market v3 raw response has no 'installationAddress' key — Settl may have renamed this section");
    }

    // =========================================================
    // Billing v1 — PaymentConfiguration (EAN, BillingType, etc.)
    // =========================================================

    [Fact]
    public async Task MpBilling_PaymentConfiguration_IsPresent()
    {
        // PaymentConfiguration holds ElectronicNumber (EAN), BillingType, and identifiers.
        // Reconcile and fix-electronic-number depend on PaymentConfiguration being populated.
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 MP {fixture.MpId} failed: {err}");
        output.WriteLine($"BillingMeteringPointCurrentDto.PaymentConfiguration = {(mp!.PaymentConfiguration != null ? "present" : "null")}");
        Assert.NotNull(mp.PaymentConfiguration);
    }

    [Fact]
    public async Task MpBilling_PaymentConfiguration_ElectronicNumber_IsAccessible()
    {
        // MeteringPointPaymentConfigurationDto.ElectronicNumber is the per-MP EAN.
        // Reconcile payment-config diff compares intended vs actual ElectronicNumber.
        // If Settl renames this field, the typed property silently defaults to null and
        // EAN drift becomes invisible.
        // (No raw billing v1 GET available — compile-time contract via typed DTO.)
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 for {fixture.MpId} failed: {err}");
        Assert.NotNull(mp!.PaymentConfiguration);

        // Log value; may be null for MPs without EAN — that's fine.
        output.WriteLine($"PaymentConfiguration.ElectronicNumber = {mp.PaymentConfiguration!.ElectronicNumber ?? "(null)"}");
        output.WriteLine($"PaymentConfiguration.BillingType = {mp.PaymentConfiguration.BillingType ?? "(null)"}");
        output.WriteLine($"PaymentConfiguration.Start = {mp.PaymentConfiguration.Start}");

        // The DTO property itself must be reachable without exception (compile + deserialise).
        // Assign to local to prevent optimisation elision.
        _ = mp.PaymentConfiguration.ElectronicNumber;
        _ = mp.PaymentConfiguration.BillingType;
    }

    [Fact]
    public async Task MpBilling_PaymentConfiguration_Start_IsNonDefault()
    {
        // PaymentConfiguration.Start uses DateOnlyUtcConverter — same risk as PayerBilling.Start.
        // If Settl changes the date format from "yyyy-MM-ddT00:00:00Z" to plain "yyyy-MM-dd",
        // DateOnlyUtcConverter fails silently and returns DateOnly.MinValue.
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 for {fixture.MpId} failed: {err}");
        Assert.NotNull(mp!.PaymentConfiguration);

        var start = mp.PaymentConfiguration!.Start;
        output.WriteLine($"PaymentConfiguration.Start (typed) = {start}");
        Assert.NotEqual(DateOnly.MinValue, start);
    }

    // =========================================================
    // Billing v1 — History (BillingConfigurationDates + PaymentConfigurationDates)
    // =========================================================

    [Fact]
    public async Task MpBilling_History_HasBillingConfigurationDates()
    {
        // GetBillingHistoryAsync is used in BillingPaymentHistoryTests and in the
        // reconcile-apply EAV (annual volume) runner to confirm how many historical billing
        // versions an MP has. If BillingConfigurationDates is renamed or removed, the runner
        // can't detect whether to do a create-new or update-existing billing entry.
        var (history, err) = await fixture.Settl.MeteringPointClient.GetBillingHistoryAsync(fixture.MpId);
        Assert.True(history != null, $"GetBillingHistoryAsync for {fixture.MpId} failed: {err}");

        output.WriteLine(
            $"BillingConfigurationDates count = {history!.BillingConfigurationDates?.Count}  " +
            $"PaymentConfigurationDates count = {history.PaymentConfigurationDates?.Count}");

        Assert.NotNull(history.BillingConfigurationDates);
        Assert.True(history.BillingConfigurationDates!.Count > 0,
            "BillingConfigurationDates is empty — MP has no billing history, which is unexpected for a migrated MP");
    }

    [Fact]
    public async Task MpBilling_History_HasPaymentConfigurationDates()
    {
        var (history, err) = await fixture.Settl.MeteringPointClient.GetBillingHistoryAsync(fixture.MpId);
        Assert.True(history != null, $"GetBillingHistoryAsync failed: {err}");

        Assert.NotNull(history!.PaymentConfigurationDates);
        Assert.True(history.PaymentConfigurationDates!.Count > 0,
            "PaymentConfigurationDates is empty — MP has no payment config history");
    }

}
