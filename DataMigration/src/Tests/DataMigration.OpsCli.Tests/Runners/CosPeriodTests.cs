using DataMigration.OpsCli.Services.Reconcile;

namespace DataMigration.OpsCli.Tests.Runners;

public class CosPeriodTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Default_is_unbounded_and_contains_everything()
    {
        var p = default(CosPeriod);
        Assert.True(p.IsUnbounded);
        Assert.True(p.Contains(D(2026, 1, 1)));
        Assert.True(p.Contains(D(2030, 12, 31)));
    }

    [Fact]
    public void Both_bounds_are_inclusive()
    {
        var p = new CosPeriod(D(2026, 5, 1), D(2026, 5, 31));
        Assert.False(p.IsUnbounded);
        Assert.True(p.Contains(D(2026, 5, 1)));   // lower bound inclusive
        Assert.True(p.Contains(D(2026, 5, 15)));
        Assert.True(p.Contains(D(2026, 5, 31)));  // upper bound inclusive
        Assert.False(p.Contains(D(2026, 4, 30)));
        Assert.False(p.Contains(D(2026, 6, 1)));
    }

    [Fact]
    public void Open_lower_bound_only_caps_the_top()
    {
        var p = new CosPeriod(null, D(2026, 5, 31));
        Assert.False(p.IsUnbounded);
        Assert.True(p.Contains(D(2000, 1, 1)));
        Assert.True(p.Contains(D(2026, 5, 31)));
        Assert.False(p.Contains(D(2026, 6, 1)));
    }

    [Fact]
    public void Open_upper_bound_only_floors_the_bottom()
    {
        var p = new CosPeriod(D(2026, 5, 1), null);
        Assert.False(p.IsUnbounded);
        Assert.False(p.Contains(D(2026, 4, 30)));
        Assert.True(p.Contains(D(2026, 5, 1)));
        Assert.True(p.Contains(D(2099, 1, 1)));
    }
}
