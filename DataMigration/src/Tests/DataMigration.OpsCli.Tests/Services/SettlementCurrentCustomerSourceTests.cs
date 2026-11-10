using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;
namespace DataMigration.OpsCli.Tests.Services;
public class SettlementCurrentCustomerSourceTests
{
    private static readonly Guid Mp = Guid.NewGuid();
    private static readonly Guid Old = Guid.NewGuid();
    private static readonly Guid New = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
    private static PayerForMeteringPointsDto Payer(Guid owner, bool primary = true, DateOnly? start = null, DateOnly? end = null) => new() {
        Payer = new() { CustomerId = owner, IsPrimaryCustomerPayer = primary },
        MeteringPoints = [new() { MeteringPointId = Mp, Start = start ?? new(2026, 9, 2), End = end ?? DateOnly.MaxValue }] };
    private static async Task<SettlementCurrentCustomerEvidence> Read(List<PayerForMeteringPointsDto>? rows, string? error = null) =>
        (await SettlementCurrentCustomerSource.ReadAsync(new Dictionary<string, Guid> { ["mp"] = Mp },
            new Dictionary<Guid, string> { [Old] = "101681", [New] = "600256" },
            (ids, at, _) => { Assert.Equal(new[] { Mp }, ids); Assert.Equal(Now, at); return Task.FromResult((rows, error)); }, Now))["mp"];
    [Fact]
    public async Task Move_in_returns_current_customer_not_the_expired_previous_owner()
    {
        var result = await Read([Payer(Old, start: new(2026, 9, 1), end: new(2026, 9, 2)), Payer(New)]);
        Assert.Equal("Read", result.ReadStatus);
        Assert.Equal("600256", result.CustomerNumber);
    }
    [Fact]
    public async Task Different_customers_are_ambiguous_even_if_one_payer_is_primary()
    {
        var result = await Read([Payer(Old), Payer(New, primary: false)]);
        Assert.Equal("Ambiguous", result.ReadStatus);
        Assert.Null(result.CustomerNumber);
        Assert.Equal(new[] { "101681", "600256" }, result.CustomerNumbers);
    }
    [Fact]
    public async Task Multiple_payers_for_same_customer_are_unambiguous() =>
        Assert.Equal("600256", (await Read([Payer(New), Payer(New, primary: false)])).CustomerNumber);
    [Fact]
    public async Task Missing_unknown_and_failed_current_owner_reads_are_distinct()
    {
        Assert.Equal("Missing", (await Read([])).ReadStatus);
        Assert.Equal("Missing", (await Read([Payer(Guid.NewGuid())])).ReadStatus);
        Assert.Equal("Missing", (await Read([Payer(Guid.Empty), Payer(New)])).ReadStatus);
        Assert.Equal("ReadFailed", (await Read(null, "unavailable")).ReadStatus);
    }
    [Fact]
    public async Task Exception_is_reported_without_throwing_or_customer_guessing()
    {
        var result = await SettlementCurrentCustomerSource.ReadAsync(new Dictionary<string, Guid> { ["mp"] = Mp },
            new Dictionary<Guid, string>(), (_, _, _) => throw new InvalidOperationException("failed"), Now);
        Assert.Equal("ReadFailed", result["mp"].ReadStatus);
    }
    [Theory]
    [InlineData("600256", "Read", "DifferentCustomer", "Erhverv")]
    [InlineData("101681", "Read", "SameCustomer", "Erhverv_UG")]
    [InlineData("", "Read", "CurrentCustomerMissing", "Erhverv")]
    [InlineData(null, "Missing", "CurrentCustomerMissing", "Erhverv")]
    [InlineData(null, "Ambiguous", "CurrentCustomerAmbiguous", "Erhverv")]
    [InlineData(null, "ReadFailed", "CurrentCustomerReadFailed", "Erhverv")]
    [InlineData(null, "NotRead", "CurrentCustomerReadFailed", "Erhverv")]
    public void Historical_exemption_applies_only_to_same_customer(string? current, string readStatus, string attribution, string expected)
    {
        SettlementConfigEntry[] entries = [new("mp", "Month", "101681", null, "business")];
        var identities = new Dictionary<string, CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string, SegmentRule> { ["business"] = new(["Erhverv"], ["Privat"], ["Privat"]) }, identities);
        var result = SettlementExemptionProposal.Apply(original, entries, identities,
            [new("mp", null, "Read", [new(Guid.NewGuid(), "ProductElement", Guid.NewGuid(), 100)], "NotVerified", null)],
            new(2026, 9, 1), Now, new Dictionary<string, SettlementCurrentCustomerEvidence> { ["mp"] = new(current, readStatus) });
        Assert.Equal(expected, Assert.Single(result.Segments.Segments).Segment);
        Assert.Empty(result.Segments.Unresolved);
        var review = Assert.Single(result.Review!.MeteringPoints);
        Assert.Equal("101681", review.CustomerNumber);
        Assert.Equal(current, review.CurrentCustomerNumber);
        Assert.Equal(attribution, review.AttributionStatus);
        Assert.NotNull(review.Warning);
        Assert.NotNull(review.AttributionReason);
        Assert.Equal(expected == "Erhverv_UG", Assert.Single(result.Review.Customers).Changed);
        Assert.Equal("NotVerified", review.HistoricalValidity);
    }
    [Fact]
    public void No_date_preserves_exemption_behavior_without_new_owner_requirement()
    {
        SettlementConfigEntry[] entries = [new("mp", "Month", "101681", null, "business")];
        var identity = new Dictionary<string, CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string, SegmentRule> { ["business"] = new(["Erhverv"], ["Privat"], ["Privat"]) }, identity);
        var result = SettlementExemptionProposal.Apply(original, entries, identity,
            [new("mp", null, "Read", [new(Guid.NewGuid(), "ProductElement", Guid.NewGuid(), 100)], "Current", null)], null, Now);
        Assert.Equal("Erhverv_UG", Assert.Single(result.Segments.Segments).Segment);
        Assert.Equal("NotRequired", Assert.Single(result.Review!.MeteringPoints).AttributionStatus);
    }
    [Fact]
    public void Customer_number_padding_is_normalized_using_production_policy()
    {
        SettlementConfigEntry[] entries = [new("mp", "Month", "941589", null, "business")];
        var identity = new Dictionary<string, CustomerIdentityKind> { ["941589"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string, SegmentRule> { ["business"] = new(["Erhverv"], ["Privat"], ["Privat"]) }, identity);
        var result = SettlementExemptionProposal.Apply(original, entries, identity,
            [new("mp", null, "Read", [new(Guid.NewGuid(), "ProductElement", Guid.NewGuid(), 100)], "NotVerified", null)], new(2026, 9, 1), Now,
            new Dictionary<string, SettlementCurrentCustomerEvidence> { ["mp"] = new("41589", "Read") }, "production");
        Assert.Equal("SameCustomer", Assert.Single(result.Review!.MeteringPoints).AttributionStatus);
    }
}
