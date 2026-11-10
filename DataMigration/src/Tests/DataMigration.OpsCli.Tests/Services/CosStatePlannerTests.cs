using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

/// <summary>
/// The date and set logic behind COS-driven state generation. Everything here is pure — the Settl
/// and Xellent round trips are covered by the CLI run, not by these tests.
/// </summary>
public class CosStatePlannerTests
{
    private static MeteringPointInfoDto Mp(bool? inDelivery, bool current, bool completed) => new()
    {
        Id = Guid.NewGuid(),
        InDelivery = inDelivery,
        CurrentOrNextDeliveryPeriod = current ? new TimePeriodDto { Start = new DateTimeOffset(2026, 4, 30, 22, 0, 0, TimeSpan.Zero) } : null,
        MostRecentCompletedDeliveryPeriod = completed ? new TimePeriodDto { Start = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero) } : null,
    };

    // --- never-in-delivery exclusion ---

    [Fact]
    public void Metering_point_in_delivery_is_in_scope()
    {
        Assert.True(CosStatePlanner.WasEverInDelivery(Mp(true, current: true, completed: false)));
    }

    [Fact]
    public void Metering_point_out_of_delivery_with_completed_period_is_in_scope()
    {
        Assert.True(CosStatePlanner.WasEverInDelivery(Mp(false, current: false, completed: true)));
    }

    [Fact]
    public void Metering_point_that_was_never_in_delivery_is_excluded()
    {
        Assert.False(CosStatePlanner.WasEverInDelivery(Mp(false, current: false, completed: false)));
        Assert.False(CosStatePlanner.WasEverInDelivery(Mp(null, current: false, completed: false)));
    }

    // --- per-customer COS ---

    [Fact]
    public void Customer_cos_is_the_earliest_across_its_metering_points()
    {
        var result = CosStatePlanner.MinCosByCustomer(
        [
            ("100001", new DateOnly(2026, 4, 30)),
            ("100001", new DateOnly(2025, 12, 31)),
            ("100001", new DateOnly(2026, 6, 30)),
            ("100002", new DateOnly(2026, 2, 28)),
        ]);

        Assert.Equal(new DateOnly(2025, 12, 31), result["100001"]);
        Assert.Equal(new DateOnly(2026, 2, 28), result["100002"]);
    }

    [Fact]
    public void Customer_cos_ignores_blank_customer_numbers()
    {
        var result = CosStatePlanner.MinCosByCustomer([("", new DateOnly(2026, 1, 1))]);
        Assert.Empty(result);
    }

    // --- CVR closure ---

    private static readonly Dictionary<string, string> CvrByAccount = new(StringComparer.Ordinal)
    {
        ["100001"] = "36665030",
        ["100009"] = "87654321",
        ["100050"] = "",
    };

    private static readonly Dictionary<string, List<string>> AccountsByCvr = new(StringComparer.Ordinal)
    {
        ["36665030"] = ["100001", "100002", "100003"],
        ["87654321"] = ["100009", "100010"],
    };

    [Fact]
    public void Closure_pulls_in_siblings_sharing_the_seeds_cvr()
    {
        var closure = CosStatePlanner.BuildCvrClosure(["100001"], CvrByAccount, AccountsByCvr);
        Assert.Equal<string[]>(["100001", "100002", "100003"], [.. closure.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void Closure_leaves_out_accounts_with_a_different_cvr()
    {
        var closure = CosStatePlanner.BuildCvrClosure(["100001"], CvrByAccount, AccountsByCvr);
        Assert.DoesNotContain("100009", closure);
        Assert.DoesNotContain("100010", closure);
    }

    [Fact]
    public void Closure_of_a_blank_cvr_account_is_the_account_alone()
    {
        var closure = CosStatePlanner.BuildCvrClosure(["100050"], CvrByAccount, AccountsByCvr);
        Assert.Equal<string[]>(["100050"], closure);
    }

    [Fact]
    public void Closure_of_an_account_unknown_to_the_cvr_map_is_the_account_alone()
    {
        var closure = CosStatePlanner.BuildCvrClosure(["999999"], CvrByAccount, AccountsByCvr);
        Assert.Equal<string[]>(["999999"], closure);
    }

    [Fact]
    public void Closure_over_several_seeds_is_the_union_of_their_cvr_classes()
    {
        var closure = CosStatePlanner.BuildCvrClosure(["100001", "100009"], CvrByAccount, AccountsByCvr);
        Assert.Equal<string[]>(
            ["100001", "100002", "100003", "100009", "100010"],
            [.. closure.Order(StringComparer.Ordinal)]);
    }

    // --- portioning the closure into resolver-sized calls ---

    private static readonly Dictionary<string, string> PortionCvrByAccount = new(StringComparer.Ordinal)
    {
        ["100001"] = "36665030",
        ["100009"] = "87654321",
        ["100050"] = "",          // no CVR at all — free filler
        ["100060"] = "  ",        // whitespace only — mergeable to the resolver, must stay together
        ["100061"] = "  ",
    };

    private static readonly Dictionary<string, List<string>> PortionAccountsByCvr = new(StringComparer.Ordinal)
    {
        ["36665030"] = ["100001", "100002", "100003"],
        ["87654321"] = ["100009", "100010"],
    };

    private static readonly string[] PortionSeeds = ["100001", "100009", "100050", "100060", "100061"];

    private static IReadOnlyList<CosStatePlanner.CvrPortion> Portions(int size, IEnumerable<string>? seeds = null) =>
        CosStatePlanner.BuildCvrPortions(seeds ?? PortionSeeds, PortionCvrByAccount, PortionAccountsByCvr, size);

    [Fact]
    public void Portions_flatten_to_exactly_the_closure()
    {
        var closure = CosStatePlanner.BuildCvrClosure(PortionSeeds, PortionCvrByAccount, PortionAccountsByCvr);
        var flattened = Portions(2).SelectMany(p => p.Accounts).ToArray();

        Assert.Equal<string[]>(
            [.. closure.Order(StringComparer.Ordinal)],
            [.. flattened.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void Portions_are_disjoint()
    {
        var flattened = Portions(2).SelectMany(p => p.Accounts).ToArray();
        Assert.Equal(flattened.Length, flattened.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void No_cvr_class_is_ever_split_across_portions()
    {
        // Portion size 1 is the hard case: every class is oversized, so nothing can be packed and a
        // naive account-level chunker would cut straight through the groups.
        var portions = Portions(1);

        foreach (var cvrClass in PortionAccountsByCvr.Values)
        {
            var holders = portions.Count(p => cvrClass.Any(a => p.Accounts.Contains(a, StringComparer.Ordinal)));
            Assert.Equal(1, holders);
        }
    }

    [Fact]
    public void Whitespace_cvr_accounts_share_a_portion()
    {
        // The resolver's blank test is IsNullOrEmpty, not IsNullOrWhiteSpace: two accounts whose CVR
        // is only spaces still form edges and can merge, so they must be resolved in the same call.
        var portions = Portions(1);
        var holder = portions.Single(p => p.Accounts.Contains("100060", StringComparer.Ordinal));
        Assert.Contains("100061", holder.Accounts);
    }

    [Fact]
    public void A_closure_that_fits_is_one_portion()
    {
        var portions = Portions(1000);
        Assert.Single(portions);
    }

    [Fact]
    public void An_oversized_cvr_class_is_kept_whole_in_its_own_portion()
    {
        var big = Enumerable.Range(1, 50).Select(i => $"2000{i:D2}").ToList();
        var cvrByAccount = new Dictionary<string, string>(StringComparer.Ordinal) { ["200001"] = "11111111" };
        var accountsByCvr = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["11111111"] = big };

        var portions = CosStatePlanner.BuildCvrPortions(["200001"], cvrByAccount, accountsByCvr, portionSize: 5);

        var portion = Assert.Single(portions);
        Assert.Equal(50, portion.Accounts.Length);
    }

    [Fact]
    public void Portioning_does_not_depend_on_the_order_the_seeds_arrive_in()
    {
        var forward = Portions(2, PortionSeeds);
        var reversed = Portions(2, PortionSeeds.Reverse());

        Assert.Equal(forward.Count, reversed.Count);
        for (var i = 0; i < forward.Count; i++)
        {
            Assert.Equal<string[]>(forward[i].Accounts, reversed[i].Accounts);
            Assert.Equal<string[]>(forward[i].SeedAccounts, reversed[i].SeedAccounts);
        }
    }

    [Fact]
    public void Every_seed_is_claimed_by_exactly_one_portion()
    {
        var claimed = Portions(1).SelectMany(p => p.SeedAccounts).ToArray();
        Assert.Equal<string[]>(
            [.. PortionSeeds.Order(StringComparer.Ordinal)],
            [.. claimed.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void A_seeds_members_are_the_same_portioned_or_not()
    {
        // Stands in for the resolver, which needs a database: merge accounts that share a non-empty
        // raw CVR, lowest account number represents. That is the property the portioning rests on —
        // a merge group never spans two CVR classes — so running it over the whole closure and over
        // each portion has to give every seed the same members.
        static Dictionary<string, List<string>> MembersOf(IEnumerable<string> accounts, IEnumerable<string> seeds)
        {
            var byRoot = accounts
                .GroupBy(a => PortionCvrByAccount.GetValueOrDefault(a, "") is { Length: > 0 } cvr ? "cvr:" + cvr : "own:" + a,
                    StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

            return seeds.ToDictionary(
                s => s,
                s => byRoot.First(kvp => kvp.Value.Contains(s, StringComparer.Ordinal)).Value,
                StringComparer.Ordinal);
        }

        var closure = CosStatePlanner.BuildCvrClosure(PortionSeeds, PortionCvrByAccount, PortionAccountsByCvr);
        var whole = MembersOf(closure, PortionSeeds);

        var portioned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var portion in Portions(2))
            foreach (var kvp in MembersOf(portion.Accounts, portion.SeedAccounts))
                portioned[kvp.Key] = kvp.Value;

        Assert.Equal(whole.Count, portioned.Count);
        foreach (var (seed, members) in whole)
            Assert.Equal<string[]>([.. members], [.. portioned[seed]]);
    }

    // --- per-customer five-year floor (calendar-year aligned: Jan 1 of cos.Year - historyYears) ---

    [Fact]
    public void History_floor_is_jan_1st_five_years_before_the_customers_own_cos_year()
    {
        var cos = new DateOnly(2026, 4, 30);
        Assert.Equal(new DateTime(2021, 1, 1), CosStatePlanner.HistoryCutoffFor(cos));
        Assert.Equal(5, Invoice.ExportHistoryYears);
    }

    [Fact]
    public void Invoice_on_the_floor_is_inside_the_window_and_the_day_before_is_not()
    {
        var cos = new DateOnly(2026, 4, 30);
        Assert.True(CosStatePlanner.IsWithinHistory(new DateTime(2021, 1, 1), cos));
        Assert.False(CosStatePlanner.IsWithinHistory(new DateTime(2020, 12, 31), cos));
        Assert.True(CosStatePlanner.IsWithinHistory(new DateTime(2021, 1, 2), cos));
    }

    [Fact]
    public void History_floor_honours_an_explicit_history_years_override()
    {
        var cos = new DateOnly(2026, 4, 30);
        Assert.Equal(new DateTime(2023, 1, 1), CosStatePlanner.HistoryCutoffFor(cos, historyYears: 3));
    }

    [Fact]
    public void Invoice_inside_the_five_year_window_but_outside_a_three_year_override_is_excluded()
    {
        var cos = new DateOnly(2026, 4, 30);
        var invoiceDate = new DateTime(2022, 1, 1); // inside 5y (floor 2021-01-01), outside 3y (floor 2023-01-01)

        Assert.True(CosStatePlanner.IsWithinHistory(invoiceDate, cos));
        Assert.False(CosStatePlanner.IsWithinHistory(invoiceDate, cos, historyYears: 3));
    }

    [Fact]
    public void Two_customers_migrated_in_different_waves_get_different_windows()
    {
        var early = new DateOnly(2025, 12, 31);
        var late = new DateOnly(2026, 6, 30);
        var invoice = new DateTime(2020, 6, 1);

        // Inside the earlier customer's window (floor 2020-01-01), outside the later one's
        // (floor 2021-01-01) — the very case a single flat reference date gets wrong.
        Assert.True(CosStatePlanner.IsWithinHistory(invoice, early));
        Assert.False(CosStatePlanner.IsWithinHistory(invoice, late));
    }

    // --- which accounts contribute invoices ---

    private static Invoice Inv(string account, DateTime date, BillingCategory category) =>
        new(account, account, date, date, string.Empty, string.Empty, category, PaymentMedia.None, InvoiceStatus.None, 0m, 0m);

    private static readonly Invoice[] MixedInvoices =
    [
        Inv("100001", new DateTime(2025, 1, 1), BillingCategory.Aconto),
        Inv("100001", new DateTime(2025, 6, 1), BillingCategory.Final),   // closed
        Inv("100002", new DateTime(2025, 1, 1), BillingCategory.Final),
        Inv("100002", new DateTime(2025, 6, 1), BillingCategory.Aconto),  // still active
    ];

    [Fact]
    public void By_default_only_accounts_closed_with_a_slutafregning_contribute_invoices()
    {
        var result = CosStatePlanner.EligibleInvoiceAccounts(MixedInvoices, includeActive: false);

        Assert.Contains("100001", result);
        Assert.DoesNotContain("100002", result);
    }

    [Fact]
    public void Include_active_lets_every_account_holding_an_invoice_contribute()
    {
        var result = CosStatePlanner.EligibleInvoiceAccounts(MixedInvoices, includeActive: true);

        Assert.Contains("100001", result);
        Assert.Contains("100002", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Include_active_over_an_empty_invoice_set_is_empty()
    {
        Assert.Empty(CosStatePlanner.EligibleInvoiceAccounts([], includeActive: true));
    }

    // --- histogram ---

    [Fact]
    public void Histogram_counts_per_date_most_populous_first()
    {
        var histogram = CosStatePlanner.Histogram(
        [
            new DateOnly(2026, 4, 30), new DateOnly(2026, 4, 30), new DateOnly(2026, 4, 30),
            new DateOnly(2026, 6, 30),
        ]);

        Assert.Equal(2, histogram.Count);
        Assert.Equal((new DateOnly(2026, 4, 30), 3), histogram[0]);
        Assert.Equal((new DateOnly(2026, 6, 30), 1), histogram[1]);
    }
}
