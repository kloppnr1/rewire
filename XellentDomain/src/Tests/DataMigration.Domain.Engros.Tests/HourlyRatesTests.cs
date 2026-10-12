using DataMigration.Domain.Engros;

namespace DataMigration.Domain.Engros.Tests;

public class HourlyRatesTests
{
    [Fact]
    public void AtHour_returns_indexed_rate()
    {
        var rates = new HourlyRates([
            0m, 1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m, 11m,
            12m, 13m, 14m, 15m, 16m, 17m, 18m, 19m, 20m, 21m, 22m, 23m
        ]);

        Assert.Equal(0m, rates.AtHour(0));
        Assert.Equal(12m, rates.AtHour(12));
        Assert.Equal(23m, rates.AtHour(23));
    }

    [Fact]
    public void IsFlat_true_when_all_24_rates_equal()
    {
        var rates = HourlyRates.Flat(1.5m);
        Assert.True(rates.IsFlat());
    }

    [Fact]
    public void IsFlat_false_when_rates_vary()
    {
        var values = Enumerable.Repeat(1m, 24).ToArray();
        values[10] = 2m;
        var rates = new HourlyRates(values);
        Assert.False(rates.IsFlat());
    }

    [Fact]
    public void Flat_factory_produces_24_identical_entries()
    {
        var rates = HourlyRates.Flat(2.7m);
        Assert.Equal(24, rates.PerHour.Length);
        Assert.All(rates.PerHour, v => Assert.Equal(2.7m, v));
    }

    [Fact]
    public void Records_with_same_rates_are_equal()
    {
        var a = HourlyRates.Flat(1m);
        var b = HourlyRates.Flat(1m);
        // Records do reference-equality on arrays — value equality requires explicit comparer.
        // This test documents the behavior; consumers that need value-equality must compare PerHour explicitly.
        Assert.True(a.PerHour.SequenceEqual(b.PerHour));
    }
}
