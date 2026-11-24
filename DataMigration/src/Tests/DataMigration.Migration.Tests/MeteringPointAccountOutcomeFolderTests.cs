using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Products;
using DataMigration.Migration.Services;
using DataMigration.SettlClient;

namespace DataMigration.Migration.Tests;

/// <summary>
/// The metering-point phase answers per metering point; the bookkeeping happens per account. This
/// is the fold in between, and the one thing it must never do is turn "no answer" into "landed" —
/// an account booked as done on a metering point nobody confirmed is never coming back.
/// </summary>
public class MeteringPointAccountOutcomeFolderTests
{
    [Fact]
    public void An_account_lands_when_every_metering_point_is_in_Settl()
    {
        // Created and already-existing mean the same thing to the account: nothing is missing on
        // its behalf. A run that only found things is as finished as one that created them.
        var customers = new[] { Account("100001", "571313100000000001", "571313100000000002") };

        var result = Fold(customers,
            Outcome("571313100000000001", SettlMeteringPointStatus.Created),
            Outcome("571313100000000002", SettlMeteringPointStatus.AlreadyExisted));

        var account = Assert.Single(result);
        Assert.Equal("100001", account.Account);
        Assert.Equal(MeteringPointAccountResult.Landed, account.Result);
        Assert.Equal(2, account.MeteringPointCount);
        Assert.Equal(2, account.LandedCount);
        Assert.Null(account.Error);
    }

    [Fact]
    public void One_refused_metering_point_fails_the_account_and_says_why()
    {
        var customers = new[] { Account("100002", "571313100000000010", "571313100000000011") };

        var result = Fold(customers,
            Outcome("571313100000000010", SettlMeteringPointStatus.Created),
            Outcome("571313100000000011", SettlMeteringPointStatus.Failed, "Address not found in DAR"));

        var account = Assert.Single(result);
        Assert.Equal(MeteringPointAccountResult.Failed, account.Result);
        // A human has to be able to see which metering point and what Settl said, without going
        // back to the run's error list and matching it up by hand.
        Assert.Equal("571313100000000011", account.FailedMeteringPointIdentification);
        Assert.Equal("Address not found in DAR", account.Error);
        Assert.Equal(1, account.LandedCount);
    }

    [Fact]
    public void A_metering_point_the_phase_never_settled_leaves_the_account_untouched()
    {
        // The whole bug: the account's other metering point did land, and booking the account on
        // that alone would leave the unsettled one behind forever.
        var customers = new[] { Account("100003", "571313100000000020", "571313100000000021") };

        var result = Fold(customers,
            Outcome("571313100000000020", SettlMeteringPointStatus.Created),
            Outcome("571313100000000021", SettlMeteringPointStatus.NotAttempted));

        var account = Assert.Single(result);
        Assert.Equal(MeteringPointAccountResult.NotAttempted, account.Result);
        Assert.Equal(1, account.LandedCount);
    }

    [Fact]
    public void A_metering_point_the_phase_never_mentions_counts_the_same_as_no_answer()
    {
        var customers = new[] { Account("100004", "571313100000000030", "571313100000000031") };

        var result = Fold(customers, Outcome("571313100000000030", SettlMeteringPointStatus.Created));

        Assert.Equal(MeteringPointAccountResult.NotAttempted, Assert.Single(result).Result);
    }

    [Fact]
    public void A_shared_metering_point_answers_for_every_account_that_reaches_it()
    {
        // First owner wins in the phase, so a metering point under two accounts appears once. Both
        // accounts depend on that one answer — and when it is a refusal, both are held back.
        var customers = new[]
        {
            Account("100005", "571313100000000040"),
            Account("100006", "571313100000000040"),
        };

        var landed = Fold(customers, Outcome("571313100000000040", SettlMeteringPointStatus.Created));
        Assert.All(landed, a => Assert.Equal(MeteringPointAccountResult.Landed, a.Result));

        var refused = Fold(customers,
            Outcome("571313100000000040", SettlMeteringPointStatus.Failed, "Grid area unknown"));
        Assert.All(refused, a =>
        {
            Assert.Equal(MeteringPointAccountResult.Failed, a.Result);
            Assert.Equal("Grid area unknown", a.Error);
        });
    }

    [Fact]
    public void An_empty_outcome_list_still_leaves_an_account_with_metering_points_alone()
    {
        // A dry run answers about nothing at all. That is an absence of information about this
        // account, not a run in which nothing landed — so it comes out unclaimed, but it does come
        // out. The count above zero is what tells the caller to keep its hands off.
        var customers = new[] { Account("100007", "571313100000000050") };

        var account = Assert.Single(Fold(customers));
        Assert.Equal(MeteringPointAccountResult.NotAttempted, account.Result);
        Assert.Equal(1, account.MeteringPointCount);
        Assert.Equal(0, account.LandedCount);
    }

    [Fact]
    public void An_empty_outcome_list_still_books_an_account_that_had_nothing_to_create()
    {
        // The endless loop: an account whose contract parts have all ended has no metering point
        // for the phase to reach, and when it is alone in the queue the phase answers about nobody.
        // Skipping the account entirely left it at "not begun" and it was taken again every time.
        var customers = new[] { Account("100010") };

        var account = Assert.Single(Fold(customers));
        Assert.Equal(MeteringPointAccountResult.NotAttempted, account.Result);
        Assert.Equal(0, account.MeteringPointCount);
        Assert.Equal(0, account.LandedCount);
    }

    [Fact]
    public void An_account_with_nothing_for_the_phase_to_do_is_not_claimed_as_done()
    {
        // No metering points inside the phase's field of view. The empty set is vacuously covered,
        // but "everything succeeded" is not a claim to build on nothing.
        var customers = new[] { Account("100008"), Account("100009", "571313100000000060") };

        var result = Fold(customers, Outcome("571313100000000060", SettlMeteringPointStatus.Created));

        Assert.Equal(MeteringPointAccountResult.NotAttempted, result[0].Result);
        Assert.Equal(0, result[0].MeteringPointCount);
        Assert.Equal(MeteringPointAccountResult.Landed, result[1].Result);
    }

    [Fact]
    public void A_landed_account_names_every_metering_point_it_landed()
    {
        // Counts say how many; only the ids say which. A metering-points-only run leaves no Settl
        // customer behind, so this list is the only way back to the metering points afterwards.
        var customers = new[] { Account("100011", "571313100000000070", "571313100000000071") };

        var account = Assert.Single(Fold(customers,
            Outcome("571313100000000070", SettlMeteringPointStatus.Created),
            Outcome("571313100000000071", SettlMeteringPointStatus.AlreadyExisted)));

        var lines = account.MeteringPoints!;
        Assert.Equal(account.MeteringPointCount, lines.Count);
        Assert.Equal(
            account.LandedCount,
            lines.Count(l => l.Result == MeteringPointAccountResult.Landed));
        // Contract order, first mention wins — the same order twice, so a caller can compare runs.
        Assert.Equal(
            new[] { "571313100000000070", "571313100000000071" },
            lines.Select(l => l.MeteringPointIdentification));
    }

    [Fact]
    public void A_failed_account_points_at_the_refused_metering_point_and_no_other()
    {
        var customers = new[] { Account("100012", "571313100000000080", "571313100000000081") };

        var account = Assert.Single(Fold(customers,
            Outcome("571313100000000080", SettlMeteringPointStatus.Created),
            Outcome("571313100000000081", SettlMeteringPointStatus.Failed, "Address not found in DAR")));

        var lines = account.MeteringPoints!;
        var refused = Assert.Single(lines, l => l.Result == MeteringPointAccountResult.Failed);
        Assert.Equal(account.FailedMeteringPointIdentification, refused.MeteringPointIdentification);
        Assert.Equal(account.LandedCount,
            lines.Count(l => l.Result == MeteringPointAccountResult.Landed));
    }

    [Fact]
    public void A_metering_point_without_an_answer_is_listed_as_one()
    {
        // The unsettled one must be visible by name too — that is the metering point somebody has
        // to come back to, and it is not the one that failed.
        var customers = new[] { Account("100013", "571313100000000090", "571313100000000091") };

        var account = Assert.Single(Fold(customers,
            Outcome("571313100000000090", SettlMeteringPointStatus.Created)));

        Assert.Equal(
            new[]
            {
                (Id: "571313100000000090", Result: MeteringPointAccountResult.Landed),
                (Id: "571313100000000091", Result: MeteringPointAccountResult.NotAttempted),
            },
            account.MeteringPoints!.Select(l => (Id: l.MeteringPointIdentification, l.Result)));
    }

    [Fact]
    public void An_account_with_nothing_in_scope_names_nothing()
    {
        var account = Assert.Single(Fold(new[] { Account("100014") }));

        Assert.Equal(0, account.MeteringPointCount);
        Assert.Empty(account.MeteringPoints!);
    }

    [Fact]
    public void A_metering_point_outside_the_phases_scope_stays_off_the_list()
    {
        // Water and heat are put in externally, so the phase never answers about them and the
        // account must not be left waiting for an answer — the counts skip them, and so must the
        // list, or the two would disagree.
        var customers = new[] { Account("100015", "571313100000000100", "571313100000000101") };
        var mps = new Dictionary<string, MeteringPoint>(StringComparer.Ordinal)
        {
            ["571313100000000100"] = ElectricityMeteringPoint("571313100000000100"),
            ["571313100000000101"] = WaterMeteringPoint("571313100000000101"),
        };

        var account = Assert.Single(MeteringPointAccountOutcomeFolder.Fold(
            customers, mps, [Outcome("571313100000000100", SettlMeteringPointStatus.Created)]));

        Assert.Equal(MeteringPointAccountResult.Landed, account.Result);
        Assert.Equal(1, account.MeteringPointCount);
        var line = Assert.Single(account.MeteringPoints!);
        Assert.Equal("571313100000000100", line.MeteringPointIdentification);
    }

    // === Fixtures ===

    private static IReadOnlyList<MeteringPointAccountOutcome> Fold(
        IReadOnlyList<Customer> customers, params SettlMeteringPointOutcome[] outcomes)
    {
        var mps = customers
            .SelectMany(c => c.Contracts)
            .SelectMany(c => c.MeteringPoints)
            .Select(a => a.MeteringPointId)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, ElectricityMeteringPoint, StringComparer.Ordinal);

        return MeteringPointAccountOutcomeFolder.Fold(customers, mps, outcomes);
    }

    private static SettlMeteringPointOutcome Outcome(
        string identification, SettlMeteringPointStatus status, string? error = null)
        => new(identification, status, error);

    private static Customer Account(string accountNumber, params string[] meteringPointIds)
    {
        var product = new Product("EL", "El", Commodity.Electricity);
        var start = new DateOnly(2026, 1, 1);
        var assignments = meteringPointIds
            .Select(id => new MeteringPointAssignment(id, product, start))
            .ToList();

        var contract = new Contract(
            $"{accountNumber}-1", "Contract", start, Commodity.Electricity, accountNumber,
            new BillingSchedule(BillingFrequency.Monthly, "M"), assignments);

        return new Customer(
            accountNumber, $"Customer {accountNumber}", null, CustomerType.Company,
            "36665030", null, TestAddress, ContactInfo.Empty,
            new CustomerBilling(null, null, null), [contract]);
    }

    private static MeteringPoint ElectricityMeteringPoint(string identification) => new(
        identification, TestAddress, Commodity.Electricity, null, [], null,
        ConnectionStatus.Connected, InDelivery: true,
        new GridConnection(PriceArea.Dk1, "031", SettlementMethod.NonProfiled,
            MeteringPointKind.Consumption, null),
        new DateOnly(2026, 1, 1));

    // No grid connection is what marks a metering point as put in externally — the phase leaves it
    // alone, and the fold has to leave it out of the account's scope for the same reason.
    private static MeteringPoint WaterMeteringPoint(string identification) => new(
        identification, TestAddress, Commodity.Water, null, [], null,
        ConnectionStatus.Connected, InDelivery: true, null,
        new DateOnly(2026, 1, 1));

    private static readonly Address TestAddress = new(
        "Fiktivvejewj", "1", null, null, "8900", "Randers", null, null, null, "DK", null);
}
