using DataMigration.Migration.Services;

namespace DataMigration.Migration.Tests;

public class WarningGroupingTests
{
    [Fact]
    public void Template_collapses_ids_numbers_and_quotes()
    {
        Assert.Equal(
            "Customer #: invalid CVR '…' (expected 8 digits) [Xellent: CustTable]",
            WarningGrouping.Template("Customer 12345: invalid CVR '99' (expected 8 digits) [Xellent: CustTable]"));
    }

    [Fact]
    public void Template_normalises_guids()
    {
        Assert.Equal(
            "Contract <id>: missing",
            WarningGrouping.Template("Contract 3f946ac3-1111-2222-3333-444455556666: missing"));
    }

    [Theory]
    [InlineData("Customer 12345: no owner mapping", "12345")]
    [InlineData("GroupOwners references unknown GroupNum 'G1'", "G1")]
    [InlineData("Something generic happened", null)]
    public void Id_extracts_representative(string msg, string? expected)
    {
        Assert.Equal(expected, WarningGrouping.Id(msg));
    }

    [Fact]
    public void Template_collapses_a_masked_id_list_to_one_placeholder()
    {
        // Before: "Customer #, #, #, #, #, #: …" — unreadable, and a six-account message
        // bucketed separately from a two-account one about the same problem.
        Assert.Equal(
            "Customer #: same address",
            WarningGrouping.Template("Customer 524451, 524470, 524488: same address"));
    }

    [Fact]
    public void Ids_returns_every_account_in_the_message()
    {
        Assert.Equal(
            ["524451", "524470", "524488"],
            WarningGrouping.Ids("Customer 524451, 524470, 524488: same address"));
    }

    [Fact]
    public void Group_keeps_all_ids_from_a_multi_account_message()
    {
        var groups = WarningGrouping.Group(
        [
            "Customer 100, 200: same address",
            "Customer 300: same address",
        ]);

        var group = Assert.Single(groups);
        Assert.Equal("Customer #: same address", group.Template);
        Assert.Equal(2, group.Count);
        Assert.Equal(["100", "200", "300"], group.Ids);
    }

    [Fact]
    public void Group_buckets_by_template_and_dedups_ids_sorted_by_count()
    {
        var groups = WarningGrouping.Group(
        [
            "Customer 100: no owner mapping",
            "Customer 200: no owner mapping",
            "Customer 200: no owner mapping",
            "MP 571313179104952730: on contracts routing to multiple payers — using default payer",
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Customer #: no owner mapping", groups[0].Template);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(["100", "200"], groups[0].Ids);
        Assert.Equal(1, groups[1].Count);
    }
}
