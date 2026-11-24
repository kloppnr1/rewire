using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlClient.Internal;

namespace DataMigration.SettlClient.Tests;

/// <summary>
/// What the meter characteristics section of the create payload tells Settl about the metering
/// point itself. The type comes from the grid connection and the sub type from the meter; on
/// water/heat there is neither, and Settl must be told nothing rather than something wrong.
/// </summary>
public class MeteringPointMeterCharacteristicsTests
{
    [Fact]
    public void A_consumption_metering_point_is_sent_as_Consumption()
    {
        var dto = MeteringPointTranslator.Build(ElectricityMp(MeteringPointKind.Consumption), MakeCustomer());

        Assert.Equal("Consumption", dto.MeterCharacteristics!.MeteringPointType);
    }

    [Fact]
    public void A_production_metering_point_is_sent_as_Production()
    {
        var dto = MeteringPointTranslator.Build(ElectricityMp(MeteringPointKind.Production), MakeCustomer());

        Assert.Equal("Production", dto.MeterCharacteristics!.MeteringPointType);
    }

    [Fact]
    public void Settl_spells_net_consumption_with_a_lowercase_c()
    {
        var dto = MeteringPointTranslator.Build(ElectricityMp(MeteringPointKind.NetConsumption), MakeCustomer());

        Assert.Equal("Netconsumption", dto.MeterCharacteristics!.MeteringPointType);
    }

    [Theory]
    [InlineData("Physical")]
    [InlineData("Virtual")]
    [InlineData("Calculated")]
    public void The_sub_type_from_the_meter_goes_through_untouched(string subType)
    {
        var mp = ElectricityMp(MeteringPointKind.Consumption, subType);

        var dto = MeteringPointTranslator.Build(mp, MakeCustomer());

        Assert.Equal(subType, dto.MeterCharacteristics!.MeteringPointSubType);
    }

    [Fact]
    public void A_metering_point_with_no_sub_type_in_Xellent_leaves_it_out()
    {
        var dto = MeteringPointTranslator.Build(ElectricityMp(MeteringPointKind.Consumption), MakeCustomer());

        Assert.Null(dto.MeterCharacteristics!.MeteringPointSubType);
    }

    [Fact]
    public void Water_and_heat_carry_neither_type_nor_sub_type()
    {
        var dto = MeteringPointTranslator.Build(InstallationMp(Commodity.Water), MakeCustomer());

        Assert.Null(dto.MeterCharacteristics!.MeteringPointType);
        Assert.Null(dto.MeterCharacteristics.MeteringPointSubType);
        // The rest of the section is unchanged — the meter is still described.
        Assert.Single(dto.MeterCharacteristics.Meters);
    }

    // === helpers ===

    private static MeteringPoint ElectricityMp(MeteringPointKind kind, string? subType = null) => new(
        Identification: "571313100000000011",
        InstallationAddress: Address(),
        Commodity: Commodity.Electricity,
        Meter: new Meter("M-1", 1m, ReadingInterval: null) { MeteringPointSubType = subType },
        ContactPoints: [],
        JointInvoiceGroupId: null,
        ConnectionStatus: ConnectionStatus.Connected,
        InDelivery: true,
        Grid: new GridConnection(PriceArea.Dk2, "031", SettlementMethod.Flex, kind, ""),
        Start: new DateOnly(2024, 1, 1),
        EstimatedAnnualVolumeKwh: 5000,
        BillingName: "Gribskov Kommune",
        BuyerInvoicing: null);

    private static MeteringPoint InstallationMp(Commodity commodity) => new(
        Identification: "0012345678",
        InstallationAddress: Address(),
        Commodity: commodity,
        Meter: new Meter("M-2", 1m, ReadingInterval: null),
        ContactPoints: [],
        JointInvoiceGroupId: null,
        ConnectionStatus: ConnectionStatus.Connected,
        InDelivery: true,
        Grid: null,
        Start: new DateOnly(2024, 1, 1),
        EstimatedAnnualVolumeKwh: null,
        BillingName: "Gribskov Kommune",
        BuyerInvoicing: null);

    private static Address Address() =>
        new("Rådhusvej", "3", null, null, "3200", "Helsinge", "0001", "Helsinge", "270", "DK", Dar: null!);

    private static Customer MakeCustomer() => new(
        AccountNumber: "352639", Name: "Gribskov Kommune", AdditionalName: "",
        Type: CustomerType.Company, Cvr: "29188440", Cpr: "",
        Address: Address(), Contact: new ContactInfo("test@example.com", "", "", ""),
        Billing: new CustomerBilling(DefaultEan: "5798008161063", Pbs: null, MobilePay: null),
        Contracts: []);
}
