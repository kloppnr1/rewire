using DataMigration.Domain.Common;
using DataMigration.Domain.Engros;

namespace DataMigration.Domain.Engros.Tests;

public class SettlementBasisShapeTests
{
    [Fact]
    public void Can_construct_minimal_basis()
    {
        var period = new Period(new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1));
        var basis = new SettlementBasis(
            MeteringPointId: "571313171234567890",
            Period: period,
            Hours: [],
            Components: []);

        Assert.Equal("571313171234567890", basis.MeteringPointId);
        Assert.Empty(basis.Hours);
        Assert.Empty(basis.Components);
    }

    [Fact]
    public void Can_construct_basis_with_components_of_each_kind()
    {
        var period = new Period(new DateOnly(2024, 1, 1), null);
        var rates = HourlyRates.Flat(1m);

        var basis = new SettlementBasis(
            MeteringPointId: "MP1",
            Period: period,
            Hours: [new ConsumptionHour(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0.5m, 0.42m, 0.65m)],
            Components: [
                new PriceComponent(PriceComponentKind.GridTariff, "E-51", "DSO tariff", "5790000000001", rates, period),
                new PriceComponent(PriceComponentKind.StateTax, "ELAFGIFT", "El-afgift", null, rates, period),
                new PriceComponent(PriceComponentKind.ProductMargin, "VARI", "Variabel margin", null, rates, period)
            ]);

        Assert.Equal(3, basis.Components.Count);
        Assert.Equal(PriceComponentKind.GridTariff, basis.Components[0].Kind);
        Assert.Equal(PriceComponentKind.ProductMargin, basis.Components[2].Kind);
    }

    [Fact]
    public void ConsumptionHour_exposes_kwh_spot_and_energy_rate_separately()
    {
        var hour = new ConsumptionHour(
            Hour: new DateTime(2024, 1, 1, 14, 0, 0, DateTimeKind.Utc),
            Kwh: 0.5m,
            SpotPrice: 0.42m,
            EnergyRate: 0.65m);

        Assert.Equal(0.5m, hour.Kwh);
        Assert.Equal(0.42m, hour.SpotPrice);
        Assert.Equal(0.65m, hour.EnergyRate);
    }
}
