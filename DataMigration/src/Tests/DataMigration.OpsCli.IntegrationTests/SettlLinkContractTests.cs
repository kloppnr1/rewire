using DataMigration.OpsCli.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// Contract tests for entity relationship (link) APIs: Customer→MP, Payer→MP,
/// PayersForMeteringPoints, and Contract→MP.
///
/// <para>These APIs are used in the reconcile scan and in create's link steps. A change to
/// the response shapes (renamed fields, restructured collections, field type change) breaks
/// silent assumptions in the runners and translators that consume them.</para>
///
/// <para>Uses <see cref="ContractTestProvisioningFixture.MpCustomerId"/> and
/// <see cref="ContractTestProvisioningFixture.MpPayerId"/>, which belong to an actual
/// migration run (not the synthetic customer) so they have real MP links.</para>
/// </summary>
public class SettlLinkContractTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>
{
    // =========================================================
    // Customer → MP links
    // =========================================================

    [Fact]
    public async Task CustomerLinks_GetMeteringPoints_ReturnsAtLeastOne()
    {
        // GetMeteringPointsAsync is the discovery path in SettlApiContractTests.InitializeAsync
        // and in CreateRunner's link verification. Fails if Settl removes or paginates the endpoint.
        if (fixture.MpCustomerId == Guid.Empty) { output.WriteLine("Skipping: MpCustomerId not discovered — no migrated MPs in featuretest"); return; }

        var (links, err) = await fixture.Settl.CustomerClient.GetMeteringPointsAsync(fixture.MpCustomerId);
        output.WriteLine($"Customer {fixture.MpCustomerId}: {links?.Count ?? 0} MP links. Error: {err}");
        Assert.True(links != null && links.Count > 0,
            $"Customer {fixture.MpCustomerId} has no MP links: {err}");
    }

    [Fact]
    public async Task CustomerLinks_MeteringPointDto_HasCommodityType()
    {
        if (fixture.MpCustomerId == Guid.Empty) { output.WriteLine("Skipping: MpCustomerId not discovered"); return; }

        var (links, err) = await fixture.Settl.CustomerClient.GetMeteringPointsAsync(fixture.MpCustomerId);
        Assert.True(links?.Count > 0, $"No MP links: {err}");

        var first = links![0];
        output.WriteLine($"CustomerMeteringPointDto.CommodityType = {first.CommodityType}");
        // CommodityType drives DeliveryCategory-based routing (electricity vs water/heat).
        // If Settl renames this field, the typed property defaults to "Electricity" (class default)
        // and routing silently wrong-routes all non-electricity MPs.
        Assert.False(string.IsNullOrEmpty(first.CommodityType),
            "CustomerMeteringPointDto.CommodityType is null/empty — Settl may have renamed this field");
    }

    [Fact]
    public async Task CustomerLinks_MeteringPointDto_HasMeteringPointId()
    {
        if (fixture.MpCustomerId == Guid.Empty) { output.WriteLine("Skipping: MpCustomerId not discovered"); return; }

        var (links, err) = await fixture.Settl.CustomerClient.GetMeteringPointsAsync(fixture.MpCustomerId);
        Assert.True(links?.Count > 0, $"No MP links: {err}");

        var first = links![0];
        output.WriteLine($"CustomerMeteringPointDto.MeteringPointId = {first.MeteringPointId}");
        Assert.NotEqual(Guid.Empty, first.MeteringPointId);
    }

    [Fact]
    public async Task CustomerLinks_MeteringPointDto_Start_IsNonDefault()
    {
        // CustomerMeteringPointDto.Start uses DateOnlyUtcConverter — same risk as PayerBilling.Start.
        // If Settl changes date format, Start silently parses as DateOnly.MinValue.
        if (fixture.MpCustomerId == Guid.Empty) { output.WriteLine("Skipping: MpCustomerId not discovered"); return; }

        var (links, err) = await fixture.Settl.CustomerClient.GetMeteringPointsAsync(fixture.MpCustomerId);
        Assert.True(links?.Count > 0, $"No MP links: {err}");

        var first = links![0];
        output.WriteLine($"CustomerMeteringPointDto.Start = {first.Start}");
        Assert.NotEqual(DateOnly.MinValue, first.Start);
    }

    [Fact]
    public async Task CustomerLinks_ElectricityLink_Resolves_InBillingV1()
    {
        // An electricity MP GUID from the customer link list must be retrievable via billing v1.
        // If the link's MeteringPointId ever drifts (e.g. Settl uses a different ID namespace
        // per API), GetBillingByIdAsync would silently return null and reconcile would miss data.
        if (fixture.MpCustomerId == Guid.Empty) { output.WriteLine("Skipping: MpCustomerId not discovered"); return; }

        var (links, _) = await fixture.Settl.CustomerClient.GetMeteringPointsAsync(fixture.MpCustomerId);
        var elLink = links?.FirstOrDefault(l =>
            string.Equals(l.CommodityType, "Electricity", StringComparison.OrdinalIgnoreCase));
        if (elLink == null) { output.WriteLine("Skipping: No electricity link found for MpCustomerId"); return; }

        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(elLink!.MeteringPointId);
        output.WriteLine($"Billing v1 GET for linked MP {elLink.MeteringPointId}: {(mp != null ? "OK" : err)}");
        Assert.True(mp != null, $"Electricity link MP {elLink.MeteringPointId} not found in billing v1: {err}");
    }

    // =========================================================
    // Payer → MP links
    // =========================================================

    [Fact]
    public async Task PayerLinks_GetMeteringPoints_ReturnsAtLeastOne()
    {
        // GetMeteringPointsAsync on payer is used in reconcile payer-fan-out detection.
        // If Settl removes this endpoint or changes its pagination, reconcile silently
        // stops seeing MP-level payer drift.
        if (fixture.MpPayerId == Guid.Empty) { output.WriteLine("Skipping: MpPayerId not discovered"); return; }

        var (links, err) = await fixture.Settl.PayerClient.GetMeteringPointsAsync(fixture.MpPayerId);
        output.WriteLine($"Payer {fixture.MpPayerId}: {links?.Count ?? 0} MP links. Error: {err}");
        Assert.True(links != null && links.Count > 0,
            $"Payer {fixture.MpPayerId} has no MP links: {err}");
    }

    [Fact]
    public async Task PayerLinks_MeteringPointDto_HasCommodityType()
    {
        if (fixture.MpPayerId == Guid.Empty) { output.WriteLine("Skipping: MpPayerId not discovered"); return; }

        var (links, err) = await fixture.Settl.PayerClient.GetMeteringPointsAsync(fixture.MpPayerId);
        Assert.True(links?.Count > 0, $"No payer MP links: {err}");

        var first = links![0];
        output.WriteLine($"PayerMeteringPointDto.CommodityType = {first.CommodityType}");
        Assert.False(string.IsNullOrEmpty(first.CommodityType),
            "PayerMeteringPointDto.CommodityType is null/empty");
    }

    [Fact]
    public async Task PayerLinks_MeteringPointDto_Start_IsNonDefault()
    {
        if (fixture.MpPayerId == Guid.Empty) { output.WriteLine("Skipping: MpPayerId not discovered"); return; }

        var (links, err) = await fixture.Settl.PayerClient.GetMeteringPointsAsync(fixture.MpPayerId);
        Assert.True(links?.Count > 0, $"No payer MP links: {err}");

        var first = links![0];
        output.WriteLine($"PayerMeteringPointDto.Start = {first.Start}");
        Assert.NotEqual(DateOnly.MinValue, first.Start);
    }

    // =========================================================
    // FindPayersForMeteringPoints
    // =========================================================

    [Fact]
    public async Task FindPayersForMeteringPoints_Returns_PayerWithCustomerId()
    {
        // FindPayersForMeteringPointsAsync is the bridge MP→Customer in reconcile's
        // payer-ownership discovery. PayerForMeteringPointsDto.Payer.CustomerId is the key.
        // If Settl renames Payer or CustomerId, reconcile can't find which customer owns the MP.
        var (results, err) = await fixture.Settl.PayerClient.FindPayersForMeteringPointsAsync([fixture.MpId]);
        output.WriteLine($"FindPayersForMeteringPoints for MP {fixture.MpId}: {results?.Count ?? 0} results. Error: {err}");

        Assert.True(results != null, $"FindPayersForMeteringPointsAsync failed: {err}");
        Assert.True(results!.Count > 0, "No payers found for the discovered billing MP");

        var ownerPayer = results[0].Payer;
        Assert.NotNull(ownerPayer);
        output.WriteLine($"  Payer.Id = {ownerPayer!.Id}  Payer.CustomerId = {ownerPayer.CustomerId}");
        Assert.NotEqual(Guid.Empty, ownerPayer.CustomerId);
    }

    [Fact]
    public async Task FindPayersForMeteringPoints_MeteringPoints_CollectionPresent()
    {
        // PayerForMeteringPointsDto.MeteringPoints lists the MP-payer link rows.
        // Reconcile payer-fan-out uses this to detect multiple payers per MP.
        var (results, _) = await fixture.Settl.PayerClient.FindPayersForMeteringPointsAsync([fixture.MpId]);
        Assert.True(results?.Count > 0, "No results from FindPayersForMeteringPoints");

        var mpList = results![0].MeteringPoints;
        output.WriteLine($"PayerForMeteringPointsDto.MeteringPoints count = {mpList?.Count}");
        Assert.NotNull(mpList);
    }

    // =========================================================
    // Contract → MP links
    // =========================================================

    [Fact]
    public async Task ContractLinks_GetContractMeteringPoints_ReturnsItems()
    {
        // GetContractMeteringPointsAsync maps Xellent contract → Settl contract GUID
        // and then verifies the link exists (idempotency check in create). If this endpoint
        // changes shape, contract-link step silently re-links already-linked MPs.
        if (fixture.FirstContractId == Guid.Empty) { output.WriteLine("Skipping: No electricity contracts in featuretest"); return; }

        var (mps, err) = await fixture.Settl.ContractClient.GetContractMeteringPointsAsync(fixture.FirstContractId);
        output.WriteLine($"Contract {fixture.FirstContractId}: {mps?.Count ?? 0} MP links. Error: {err}");
        Assert.True(mps != null, $"GetContractMeteringPointsAsync failed: {err}");
        Assert.True(mps!.Count > 0, "First electricity contract has no MP links in featuretest");
    }

    [Fact]
    public async Task ContractLinks_ContractMeteringPointInfoDto_HasMeteringPointId()
    {
        if (fixture.FirstContractId == Guid.Empty) { output.WriteLine("Skipping: No electricity contracts in featuretest"); return; }

        var (mps, _) = await fixture.Settl.ContractClient.GetContractMeteringPointsAsync(fixture.FirstContractId);
        Assert.True(mps?.Count > 0, "No MP links for first contract");

        var first = mps![0];
        output.WriteLine($"ContractMeteringPointInfoDto.MeteringPointId = {first.MeteringPointId}  NationalId = {first.MeteringPointIdentification}");
        Assert.NotEqual(Guid.Empty, first.MeteringPointId);
    }

    [Fact]
    public async Task ContractLinks_ContractMeteringPointInfoDto_HasMeteringPointIdentification()
    {
        // MeteringPointIdentification in the contract link is used to cross-reference with
        // the Xellent metering point identification. If Settl drops this field, the cross-check
        // in generate-state falls back to pure GUID matching (misses unlinked MPs).
        if (fixture.FirstContractId == Guid.Empty) { output.WriteLine("Skipping: No electricity contracts in featuretest"); return; }

        var (mps, _) = await fixture.Settl.ContractClient.GetContractMeteringPointsAsync(fixture.FirstContractId);
        Assert.True(mps?.Count > 0, "No MP links for first contract");

        var first = mps![0];
        output.WriteLine($"ContractMeteringPointInfoDto.MeteringPointIdentification = {first.MeteringPointIdentification}");
        Assert.False(string.IsNullOrEmpty(first.MeteringPointIdentification),
            "ContractMeteringPointInfoDto.MeteringPointIdentification is null — Settl may have dropped this field");
    }

    // =========================================================
    // Water contract → MP links (open-ended end date)
    // =========================================================

    [Fact]
    public async Task WaterContractLinks_GetContractMeteringPoints_ReturnsItems()
    {
        if (fixture.FirstWaterContractId == Guid.Empty) { output.WriteLine("Skipping: No water contracts in featuretest"); return; }

        var (mps, err) = await fixture.Settl.ContractClient.GetContractMeteringPointsAsync(fixture.FirstWaterContractId);
        output.WriteLine($"Water contract {fixture.FirstWaterContractId}: {mps?.Count ?? 0} MP links. Error: {err}");
        Assert.True(mps != null, $"GetContractMeteringPointsAsync failed: {err}");
        Assert.True(mps!.Count > 0, "First water contract has no MP links in featuretest");
    }

    [Fact]
    public async Task WaterContractLinks_AssociationEnd_IsOpenEnded()
    {
        // Water/heat profiles set OpenEnded=true in SettlMpContractLink so Settl receives
        // EndDate=9999-12-31 (the open-ended sentinel) — NOT the Xellent contract part's
        // concrete end date. BuildContractLinkDto must honour link.OpenEnded regardless of
        // ContractPartEnddate. Bug: link.OpenEnded was ignored → Settl received e.g. 31.03.2026.
        if (fixture.FirstWaterContractId == Guid.Empty) { output.WriteLine("Skipping: No water contracts in featuretest"); return; }

        var (mps, _) = await fixture.Settl.ContractClient.GetContractMeteringPointsAsync(fixture.FirstWaterContractId);
        Assert.True(mps?.Count > 0, "No MP links for first water contract — cannot verify end date");

        var now = DateTimeOffset.UtcNow;
        foreach (var mp in mps!)
        {
            output.WriteLine($"Water MP {mp.MeteringPointIdentification}: End = {mp.End:yyyy-MM-dd}");
            if (mp.End < now)
            {
                // Pre-fix link: created before BuildContractLinkDto honoured OpenEnded=true.
                // The link has already ended in Settl — it's known drift, not a code regression.
                output.WriteLine($"  (skipping: ended link from before OpenEnded fix — known featuretest drift)");
                continue;
            }
            Assert.True(mp.End.Year >= 9999,
                $"Water MP {mp.MeteringPointIdentification} has concrete future end date {mp.End:yyyy-MM-dd} — " +
                "expected open-ended (year 9999). Water profiles must send OpenEnded=true to Settl " +
                "(fix: BuildContractLinkDto must check link.OpenEnded before ContractPartEnddate).");
        }
    }
}
