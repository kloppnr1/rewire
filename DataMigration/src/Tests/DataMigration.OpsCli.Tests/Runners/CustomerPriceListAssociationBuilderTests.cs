using DataMigration.Core;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Runners;

public class CustomerPriceListAssociationBuilderTests
{
    private static readonly DateTime Cos = new(2026, 1, 1);
    private static readonly DateTime RefDate = new(2025, 12, 31); // COS - 1 day

    // "P1" -> "PE1"; price-list names resolve "ST 1000"->pl1, "ST 2000"->pl2.
    private static readonly ProductElementCodeResolver PeResolver = new(new[]
    {
        new ProductElementMappingRule { Exact = "P1", Code = "PE1" },
    });

    private static string? ResolvePriceList(string name) => name switch
    {
        "ST 1000" => "pl1",
        "ST 2000" => "pl2",
        _ => null,
    };

    private static PriceListMpInput Mp(
        string contractNum, string amrCode, string productNum,
        DateTime partStart, DateTime partEnd, string mpId = "571000")
        => new(mpId, contractNum, productNum, amrCode, partStart, partEnd);

    private static CustomerPriceListAssociationResult Build(
        Dictionary<string, List<PriceListMpInput>> mps,
        Dictionary<string, DateTime> contractEndDates,
        Dictionary<(string, string, string, DateTime), DateTime>? logEndDates = null,
        Dictionary<(string, string, DateTime), string>? deliveryRateCodes = null,
        HashSet<string>? stillActive = null)
        => CustomerPriceListAssociationBuilder.Build(
            mps,
            contractEndDates,
            deliveryRateCodes ?? new Dictionary<(string, string, DateTime), string>(),
            logEndDates ?? new Dictionary<(string, string, string, DateTime), DateTime>(),
            ResolvePriceList,
            PeResolver,
            stillActive ?? new HashSet<string>(StringComparer.Ordinal),
            Cos, RefDate, env: null);

    [Fact]
    public void Emits_one_association_for_a_matching_mp_with_start_pinned_to_cos()
    {
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), new DateTime(2026, 12, 31)) } },
            new() { ["C1"] = new DateTime(2025, 12, 31) });

        var row = Assert.Single(r.Rows);
        Assert.Equal("100001", row.CustomerNumber);
        Assert.Equal("PE1", row.ProductElementCode);
        Assert.Equal("pl1", row.PriceListIdentifier);
        Assert.Equal(Cos, row.Start);
        Assert.Equal(new DateTime(2026, 12, 31), row.End);
    }

    [Fact]
    public void Skips_contract_still_active_after_reference_date()
    {
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), new DateTime(2027, 1, 1)) } },
            new() { ["C1"] = new DateTime(2027, 1, 1) }); // > RefDate
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Skips_contract_with_enddatenull_sentinel()
    {
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), MigrationConstants.EndDateNull) } },
            new()); // C1 missing -> defaults to EndDateNull
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Skips_representative_still_active_on_cos()
    {
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), new DateTime(2026, 12, 31)) } },
            new() { ["C1"] = new DateTime(2025, 12, 31) },
            stillActive: new HashSet<string>(StringComparer.Ordinal) { "100001" });
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Ignores_delivery_group_region_but_keeps_amr_pricelist()
    {
        // Delivery-group rate code "Øst" must NOT become a price list; the amrcode still resolves.
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), new DateTime(2026, 12, 31)) } },
            new() { ["C1"] = new DateTime(2025, 12, 31) },
            deliveryRateCodes: new() { [("C1", "KLI 1000", new DateTime(2025, 6, 1))] = "Øst" });

        var row = Assert.Single(r.Rows);
        Assert.Equal("pl1", row.PriceListIdentifier);
        Assert.Empty(r.UnmatchedContracts);
    }

    [Fact]
    public void Reports_unmatched_product_and_emits_no_row()
    {
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "PX", new DateTime(2025, 6, 1), new DateTime(2026, 12, 31)) } },
            new() { ["C1"] = new DateTime(2025, 12, 31) });

        Assert.Empty(r.Rows);
        var u = Assert.Single(r.UnmatchedProducts);
        Assert.Equal("PX", u.Product);
    }

    [Fact]
    public void Reports_unmatched_pricelist_name()
    {
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 9999", "P1", new DateTime(2025, 6, 1), new DateTime(2026, 12, 31)) } },
            new() { ["C1"] = new DateTime(2025, 12, 31) });

        Assert.Empty(r.Rows);
        var u = Assert.Single(r.UnmatchedContracts);
        Assert.Equal("KLI 9999", u.AmrCode);
    }

    [Fact]
    public void Collapses_multiple_parts_to_one_row_with_latest_end()
    {
        var r = Build(
            new()
            {
                ["100001"] = new()
                {
                    Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), new DateTime(2026, 6, 30)),
                    Mp("C1", "KLI 1000", "P1", new DateTime(2026, 7, 1), new DateTime(2026, 12, 31)),
                },
            },
            new() { ["C1"] = new DateTime(2025, 12, 31) });

        var row = Assert.Single(r.Rows);
        Assert.Equal("pl1", row.PriceListIdentifier);
        Assert.Equal(Cos, row.Start);
        Assert.Equal(new DateTime(2026, 12, 31), row.End); // latest end wins
    }

    [Fact]
    public void Go_forward_log_part_replaces_predecessor_for_same_product_element()
    {
        // Live part on pl1 ending 2025-12-31; a log-only future part (start >= COS) on pl2 ending
        // 2028-06-30. Same (customer, ProductElement) → go-forward replaces predecessor: pl2 wins.
        var r = Build(
            new() { ["100001"] = new() { Mp("C1", "KLI 1000", "P1", new DateTime(2025, 6, 1), new DateTime(2025, 12, 31)) } },
            new() { ["C1"] = new DateTime(2025, 12, 31) },
            logEndDates: new()
            {
                [("C1", "KLI 2000", "P1", new DateTime(2026, 1, 1))] = new DateTime(2028, 6, 30),
            });

        Assert.Equal(1, r.GoForwardEmitted);
        var row = Assert.Single(r.Rows);
        Assert.Equal("pl2", row.PriceListIdentifier);
        Assert.Equal(Cos, row.Start);
        Assert.Equal(new DateTime(2028, 6, 30), row.End);
    }
}
