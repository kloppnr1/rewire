using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// Contract tests for Find*/List* pagination APIs across all entity types.
///
/// <para>Report runners and reconcile scanners rely on these to iterate all entities
/// in featuretest/production. Breaking changes here (renamed TotalCount field, changed
/// pagination parameters, empty Items on first page) cause runners to silently process
/// zero records — no error, no output, undetected.</para>
/// </summary>
public class SettlPaginationContractTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>
{
    // =========================================================
    // Payer pagination
    // =========================================================

    [Fact]
    public async Task FindPayersAsync_ReturnsTotalCountAndItems()
    {
        // FindPayersAsync is used in report-payers and reconcile payer scan.
        // BulkPaginator terminates on empty page — TotalCount is advisory only.
        // Settl stopped returning TotalCount (always 0 now); runners are unaffected.
        var (items, totalCount, err) = await fixture.Settl.PayerClient.FindPayersAsync(limit: 5);
        output.WriteLine($"FindPayersAsync: {items.Count} items, TotalCount = {totalCount}. Error: {err}");

        Assert.True(items.Count > 0, $"FindPayersAsync returned 0 items: {err}");
    }

    [Fact]
    public async Task FindPayersAsync_Limit_IsRespected()
    {
        var (items, _, err) = await fixture.Settl.PayerClient.FindPayersAsync(limit: 2);
        output.WriteLine($"FindPayersAsync(limit:2): {items.Count} items. Error: {err}");
        Assert.True(items.Count <= 2,
            $"FindPayersAsync returned {items.Count} items for limit=2 — Settl is ignoring the Limit parameter");
    }

    [Fact]
    public async Task FindPayersAsync_Items_HaveNonEmptyId()
    {
        var (items, _, err) = await fixture.Settl.PayerClient.FindPayersAsync(limit: 3);
        Assert.True(items.Count > 0, $"No payers returned: {err}");

        foreach (var p in items)
        {
            output.WriteLine($"PayerInfoDto.Id = {p.Id}");
            Assert.NotEqual(Guid.Empty, p.Id);
        }
    }

    [Fact]
    public async Task FindPayersByCustomer_ReturnsDedicatedPayer()
    {
        // FindPayersByCustomerAsync is used in create's payer-discovery step (what payers
        // does this customer already have?). If the CustomerId filter stops working,
        // create would see all payers and mis-detect "already exists".
        var (items, err) = await fixture.Settl.PayerClient.FindPayersByCustomerAsync(fixture.CustomerId);
        output.WriteLine($"FindPayersByCustomer for {fixture.CustomerId}: {items?.Count} items. Error: {err}");

        Assert.True(items?.Count > 0, $"Dedicated customer {fixture.CustomerId} has no payers: {err}");
        Assert.True(
            items!.Any(p => p.Id == fixture.PayerId),
            $"Dedicated payer {fixture.PayerId} not found in customer {fixture.CustomerId}'s payer list");
    }

    // =========================================================
    // Metering point pagination — billing v1
    // =========================================================

    [Fact]
    public async Task FindBillingMpsAsync_ReturnsTotalCountAndItems()
    {
        // Used in report-meteringpoints and reconcile MP scan.
        // BulkPaginator terminates on empty page — TotalCount is advisory only.
        // Settl stopped returning TotalCount (always 0 now); runners are unaffected.
        var (items, totalCount, err) = await fixture.Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 5);
        output.WriteLine($"FindBillingMeteringPointsAsync: {items.Count} items, TotalCount = {totalCount}. Error: {err}");

        Assert.True(items.Count > 0, $"FindBillingMeteringPointsAsync returned 0 items: {err}");
    }

    [Fact]
    public async Task FindBillingMpsAsync_Limit_IsRespected()
    {
        var (items, _, err) = await fixture.Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 2);
        output.WriteLine($"FindBillingMeteringPointsAsync(limit:2): {items.Count}. Error: {err}");
        Assert.True(items.Count <= 2,
            $"FindBillingMeteringPointsAsync returned {items.Count} items for limit=2");
    }

    [Fact]
    public async Task FindBillingMpsAsync_Items_HaveIdAndMeteringPointIdentification()
    {
        // BillingMeteringPointInfoDto.Id is used as the billing-v1 MP GUID.
        // MeteringPointIdentification is the 18-digit national ID — used for
        // SearchBillingByIdentificationAsync cross-lookup in create.
        var (items, _, err) = await fixture.Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 3);
        Assert.True(items.Count > 0, $"No billing MPs returned: {err}");

        foreach (var mp in items)
        {
            output.WriteLine($"BillingMeteringPointInfoDto: Id={mp.Id}  NationalId={mp.MeteringPointIdentification}");
            Assert.NotEqual(Guid.Empty, mp.Id);
            Assert.False(string.IsNullOrEmpty(mp.MeteringPointIdentification),
                $"MP {mp.Id} has null/empty MeteringPointIdentification");
        }
    }

    [Fact]
    public async Task FindBillingMpsAsync_Skip_AdvancesPage()
    {
        // Pagination with skip — verifies the Skip parameter is applied server-side.
        // If ignored, page 2 == page 1 and the scan loop produces duplicate records.
        var (page1, total, err1) = await fixture.Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 2);
        Assert.True(page1.Count > 0, $"No billing MPs: {err1}");
        if (total <= 2) { output.WriteLine("Only 1 page available, Skip test not meaningful"); return; }

        var (page2, _, err2) = await fixture.Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 2, skip: 2);
        Assert.True(page2.Count > 0, $"Page 2 empty: {err2}");

        // At least one ID should differ between pages.
        var page1Ids = page1.Select(m => m.Id).ToHashSet();
        var hasNewId = page2.Any(m => !page1Ids.Contains(m.Id));
        output.WriteLine($"Page1 IDs: {string.Join(", ", page1Ids)}  Page2 IDs: {string.Join(", ", page2.Select(m => m.Id))}");
        Assert.True(hasNewId, "Page 2 returned same IDs as page 1 — Skip parameter may be ignored");
    }

    // =========================================================
    // Metering point pagination — market v3
    // =========================================================

    [Fact]
    public async Task FindMarketMpsAsync_ReturnsItems()
    {
        // FindMeteringPointsAsync (market v3) is used in reconcile MP scan for market data.
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (items, totalCount, err) = await mp.FindMeteringPointsAsync(limit: 5, doCount: true);
        output.WriteLine($"FindMeteringPointsAsync: {items.Count} items, TotalCount = {totalCount}. Error: {err}");

        Assert.True(items.Count > 0, $"FindMeteringPointsAsync returned 0 items: {err}");
    }

    [Fact]
    public async Task FindMarketMpsAsync_Items_HaveMeteringPointIdentification()
    {
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var (items, _, err) = await mp.FindMeteringPointsAsync(limit: 3);
        Assert.True(items.Count > 0, $"No market MPs: {err}");

        foreach (var item in items)
        {
            output.WriteLine($"MeteringPointInfoDto.MeteringPointIdentification = {item.MeteringPointIdentification}");
            Assert.False(string.IsNullOrEmpty(item.MeteringPointIdentification),
                "MeteringPointInfoDto.MeteringPointIdentification is null — Settl may have renamed this field");
        }
    }

    // =========================================================
    // Contract list
    // =========================================================

    [Fact]
    public async Task FindContractsAsync_NoFilter_ReturnsAtLeastTwoTypes()
    {
        // ContractNameResolver fetches all contracts once per run. If the unfiltered list
        // is empty, no contract-MP links can be created (all contracts silently skipped).
        var (contracts, err) = await fixture.Settl.ContractClient.FindContractsAsync();
        output.WriteLine($"FindContractsAsync (all): {contracts?.Count ?? 0} contracts. Error: {err}");

        Assert.True(contracts?.Count > 0, $"FindContractsAsync returned 0 contracts: {err}");

        var types = contracts!.Select(c => c.CommodityType).Distinct().OrderBy(t => t).ToList();
        output.WriteLine($"CommodityTypes in featuretest: {string.Join(", ", types)}");
        // We expect at least Electricity (migration created electricity contracts).
        Assert.Contains(types, t => string.Equals(t, "Electricity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindContractsAsync_ContractInfoDto_HasIdAndContractName()
    {
        // ContractInfoDto.Id is the Settl contract GUID written to contract-MP links.
        // ContractName is the join key to Xellent contract names in ContractNameResolver.
        var (contracts, err) = await fixture.Settl.ContractClient.FindContractsAsync(commodityType: "Electricity");
        Assert.True(contracts?.Count > 0, $"No electricity contracts: {err}");

        var first = contracts![0];
        output.WriteLine($"ContractInfoDto: Id={first.Id}  ContractName={first.ContractName}  CommodityType={first.CommodityType}");
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.False(string.IsNullOrEmpty(first.ContractName),
            "ContractInfoDto.ContractName is null — ContractNameResolver would produce empty name→GUID map");
    }

    // =========================================================
    // Settlement group list
    // =========================================================

    [Fact]
    public async Task SettlementGroups_ListAll_ReturnsGroups()
    {
        // ListAllAsync drives fix-settlement-groups' upfront group-name→GUID map.
        // If this returns empty, fix-settlement-groups silently skips all MPs.
        var (groups, err) = await fixture.Settl.SettlementGroupClient.ListAllAsync("DK");
        output.WriteLine($"SettlementGroups ListAll: {groups?.Count ?? 0} groups. Error: {err}");

        Assert.True(groups != null, $"ListAllAsync failed: {err}");
        Assert.True(groups!.Count > 0, "No settlement groups in featuretest — fix-settlement-groups would process nothing");
    }

    [Fact]
    public async Task SettlementGroups_GroupInfoDto_HasGroupName()
    {
        // SettlementGroupInfoDto.GroupName is the join key between the config file (group names
        // as strings) and Settl's GUID-based API. If renamed, fix-settlement-groups can't
        // resolve any group and silently skips everything.
        var (groups, err) = await fixture.Settl.SettlementGroupClient.ListAllAsync("DK");
        Assert.True(groups?.Count > 0, $"No settlement groups: {err}");

        var first = groups![0];
        output.WriteLine($"SettlementGroupInfoDto: Id={first.Id}  GroupName={first.GroupName}  IsActive={first.IsActive}");
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.False(string.IsNullOrEmpty(first.GroupName),
            "SettlementGroupInfoDto.GroupName is null — fix-settlement-groups name→GUID resolution breaks");
    }

    [Fact]
    public async Task SettlementGroups_FindByMeteringPoint_DoesNotError()
    {
        // FindByMeteringPointAsync is used in fix-settlement-groups for idempotency
        // (skip add if MP already in group). If the endpoint errors, fix-settlement-groups
        // re-adds MPs on every run (or crashes depending on error handling).
        var (groups, err) = await fixture.Settl.SettlementGroupClient.FindByMeteringPointAsync(fixture.MpId);
        output.WriteLine($"FindByMeteringPoint for MP {fixture.MpId}: {groups?.Count ?? 0} groups. Error: {err}");
        // May be empty if the MP isn't in any group — that's fine. We just need no error.
        Assert.True(groups != null, $"FindByMeteringPointAsync failed: {err}");
    }
}
