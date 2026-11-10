using DataMigration.Domain.Common;

namespace DataMigration.Domain.Engros.Tests;

public class PeriodTests
{
    [Fact]
    public void Contains_inclusive_From()
    {
        var period = new Period(new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 1));
        Assert.True(period.Contains(new DateOnly(2024, 1, 1)));
    }

    [Fact]
    public void Contains_exclusive_To()
    {
        var period = new Period(new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 1));
        Assert.False(period.Contains(new DateOnly(2024, 4, 1)));
    }

    [Fact]
    public void Contains_inside()
    {
        var period = new Period(new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 1));
        Assert.True(period.Contains(new DateOnly(2024, 2, 15)));
    }

    [Fact]
    public void Contains_before_From_is_false()
    {
        var period = new Period(new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 1));
        Assert.False(period.Contains(new DateOnly(2023, 12, 31)));
    }

    [Fact]
    public void OpenEnded_period_contains_far_future()
    {
        var period = new Period(new DateOnly(2024, 1, 1), null);
        Assert.True(period.Contains(new DateOnly(9999, 12, 31)));
    }

    [Fact]
    public void OpenEnded_period_excludes_before_From()
    {
        var period = new Period(new DateOnly(2024, 1, 1), null);
        Assert.False(period.Contains(new DateOnly(2023, 12, 31)));
    }
}
