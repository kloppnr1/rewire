using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Products;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// A contract's metering-point assignments must come back in an order that always decides, with the
/// part in force at the reference date first for each metering point.
///
/// <para>The case behind these: with an include-ended window on, production account 100569 came back
/// with both <c>V Kvartal</c> (ended 2026-02-28) and <c>V ErhKvt</c> (in force) on metering point
/// 571313115200380596, and the expired one came first — because the rows arrived in whatever order
/// SQL Server produced and nothing re-ordered them. A consumer reading one product per metering point
/// reported the expired product.</para>
/// </summary>
public class ContractMapper_AssignmentOrderTests
{
    private static readonly DateOnly Reference = new(2026, 8, 15);
    private static readonly DateOnly OpenEnded = DateOnly.FromDateTime(MigrationConstants.EndDateNull);

    private static MeteringPointAssignment Part(
        string mpId, string product, string start, string end, string amrCode = "")
        => new(mpId, new Product(product, product, Commodity.Electricity),
            DateOnly.Parse(start), DateOnly.Parse(end), amrCode);

    [Fact]
    public void Part_in_force_at_the_reference_date_comes_before_one_that_has_ended()
    {
        // Account 100569, contract 10288981, as Xellent holds it.
        var ordered = ContractMapper.OrderAssignments(
            [
                Part("571313115200380596", "V Kvartal", "2016-04-01", "2026-02-28"),
                Part("571313115200380596", "V ErhKvt", "2026-03-01", "2026-08-31")
            ],
            Reference);

        Assert.Equal(["V ErhKvt", "V Kvartal"], ordered.Select(a => a.Product.Code));
    }

    [Fact]
    public void Row_order_does_not_decide_the_result()
    {
        var parts = new[]
        {
            Part("MP1", "V ErhVari", "2026-03-01", "2026-08-31"),
            Part("MP1", "V Variabel", "2023-10-01", "2026-02-28"),
            Part("MP1", "V Kvartal", "2016-04-01", "2022-11-30")
        };

        var forward = ContractMapper.OrderAssignments(parts, Reference).Select(a => a.Product.Code);
        var reversed = ContractMapper.OrderAssignments(parts.Reverse(), Reference).Select(a => a.Product.Code);

        Assert.Equal(["V ErhVari", "V Variabel", "V Kvartal"], forward);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Open_ended_part_counts_as_in_force()
    {
        // Account 101932: V SMESpotH runs open-ended, V SMEspot ended before the reference date.
        var ordered = ContractMapper.OrderAssignments(
            [
                Part("MP1", "V SMEspot", "2023-04-18", "2026-04-18"),
                Part("MP1", "V SMESpotH", "2026-04-19", "1900-01-01")
            ],
            Reference);

        Assert.Equal(["V SMESpotH", "V SMEspot"], ordered.Select(a => a.Product.Code));
        Assert.Equal(OpenEnded, ordered[0].EndDate);
    }

    [Fact]
    public void Future_part_sorts_behind_the_one_running_now()
    {
        var ordered = ContractMapper.OrderAssignments(
            [
                Part("MP1", "V Future", "2026-12-01", "1900-01-01"),
                Part("MP1", "V Now", "2026-01-01", "2026-11-30")
            ],
            Reference);

        Assert.Equal(["V Now", "V Future"], ordered.Select(a => a.Product.Code));
    }

    [Fact]
    public void Two_products_in_force_on_the_same_metering_point_are_both_kept_and_ordered_by_code()
    {
        // Account 472286, contract 10525341: V ProdErhv and V ProdHand start the same day and always
        // travel as a pair. Neither may be dropped, and the pair must not depend on row order.
        var ordered = ContractMapper.OrderAssignments(
            [
                Part("571313113162851277", "V ProdHand", "2024-02-01", "2026-08-31"),
                Part("571313113162851277", "V ProdErhv", "2024-02-01", "2026-08-31", "ST 0000")
            ],
            Reference);

        Assert.Equal(["V ProdErhv", "V ProdHand"], ordered.Select(a => a.Product.Code));
    }

    [Fact]
    public void Assignments_group_by_metering_point()
    {
        var ordered = ContractMapper.OrderAssignments(
            [
                Part("MP2", "B old", "2020-01-01", "2024-01-01"),
                Part("MP1", "A old", "2020-01-01", "2024-01-01"),
                Part("MP2", "B now", "2026-01-01", "1900-01-01"),
                Part("MP1", "A now", "2026-01-01", "1900-01-01")
            ],
            Reference);

        Assert.Equal(["A now", "A old", "B now", "B old"], ordered.Select(a => a.Product.Code));
    }

    [Fact]
    public void Same_product_repeated_per_year_puts_the_current_period_first()
    {
        // Account 463454: VGGFastErh is re-issued every year, so an include-ended window returns
        // four assignments that differ only in period.
        var ordered = ContractMapper.OrderAssignments(
            [
                Part("MP1", "VGGFastErh", "2023-10-01", "2023-12-31"),
                Part("MP1", "VGGFastErh", "2024-01-01", "2024-12-31"),
                Part("MP1", "VGGFastErh", "2026-01-01", "2026-08-31"),
                Part("MP1", "VGGFastErh", "2025-01-01", "2025-12-31")
            ],
            Reference);

        Assert.Equal(new DateOnly(2026, 1, 1), ordered[0].Start);
        Assert.Equal(
            [new DateOnly(2026, 1, 1), new DateOnly(2025, 1, 1), new DateOnly(2024, 1, 1), new DateOnly(2023, 10, 1)],
            ordered.Select(a => a.Start));
    }
}
