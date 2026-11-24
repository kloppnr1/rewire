using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Internal;

/// <summary>
/// Builds Settl <see cref="MeteringPointDto"/> from a Domain <see cref="MeteringPoint"/> and its
/// owning <see cref="Customer"/>. Pure 1:1 mapping — no I/O, no domain decisions.
///
/// <para>Electricity MPs (<c>Grid != null</c>) emit Grid + SettlementMethod sections;
/// installation-based MPs (water/heat) omit them. That is Settl-DTO shape, not domain logic.
/// Meter-shape fields (Decimals/DigitsOfPrecision/ReadingOccurrence) and InstallationAddress
/// are populated by the Domain mapper per commodity — the translator just reads them.</para>
///
/// <para>Grid UUID resolution (MarketBalanceAreaId / MeteringGridAreaId) is NOT performed
/// here: the [JsonIgnore] string fields <c>MarketBalanceAreaName</c> and <c>GridAreaCode</c>
/// are populated; the caller (<see cref="SettlClient"/>) resolves UUIDs before sending.</para>
/// </summary>
public static class MeteringPointTranslator
{
    public static MeteringPointDto Build(MeteringPoint mp, Customer customer)
    {
        var mpStart = mp.Start;
        var isElectricity = mp.Grid != null;
        var installationNumber = mp.Identification.Length >= 10
            ? mp.Identification[^10..]
            : mp.Identification;

        return new MeteringPointDto
        {
            Identification = new MeteringPointIdentifiersDto
            {
                Start = mpStart,
                MeteringPointIdentification = mp.Identification,
                InstallationNumber = installationNumber
            },
            InstallationAddress = BuildInstallationAddress(mp, mpStart),
            Consumer = BuildConsumer(mp, customer, mpStart),
            MeterCharacteristics = BuildMeterCharacteristics(mp, mpStart),
            Grid = isElectricity ? BuildGrid(mp.Grid!, mpStart) : null,
            SettlementMethod = isElectricity
                ? new MeteringPointSettlementMethodDto { SettlementMethod = MapSettlementMethod(mp.Grid!.SettlementMethod) }
                : null,
            ConnectionInfo = new MeteringPointConnectionInfoDto
            {
                Start = mpStart,
                ConnectionStatus = MapConnectionStatus(mp.ConnectionStatus)
            },
            DeliveryStatus = new MeteringPointDeliveryStatusDto
            {
                Start = mpStart,
                InDelivery = mp.InDelivery
            },
            JointInvoiceNumber = !string.IsNullOrEmpty(mp.JointInvoiceGroupId) ? mp.JointInvoiceGroupId : null,
            AdditionalInfo1 = mp.BillingName
        };
    }

    private static MeteringPointInstallationAddressDto BuildInstallationAddress(MeteringPoint mp, DateOnly start)
    {
        return new MeteringPointInstallationAddressDto
        {
            Start = start,
            Address = MapPhysicalAddress(mp.InstallationAddress)
        };
    }

    private static MeteringPointConsumerDto BuildConsumer(MeteringPoint mp, Customer customer, DateOnly start)
    {
        var firstConsumerParty = new MeteringPointConsumerPartyDto
        {
            Name = new ConsumerNameDto { FullName = customer.Name?.Trim() },
            VatIdentificationNumber = !string.IsNullOrEmpty(customer.Cvr) ? customer.Cvr : null,
            NationalIdentificationNumber = string.IsNullOrEmpty(customer.Cvr) ? customer.Cpr : null
        };
        // Company: SecondConsumerParty = kopi af FirstConsumerParty (erhverv-konvention).
        // Person: SecondConsumerParty kun sat når disponent 2 (Cvr2/Cpr2) er udfyldt.
        MeteringPointConsumerPartyDto? secondConsumerParty;
        if (customer.Type == CustomerType.Company)
        {
            secondConsumerParty = firstConsumerParty;
        }
        else if (!string.IsNullOrEmpty(customer.Cvr2) || !string.IsNullOrEmpty(customer.Cpr2))
        {
            secondConsumerParty = new MeteringPointConsumerPartyDto
            {
                Name = !string.IsNullOrEmpty(customer.AdditionalName)
                    ? new ConsumerNameDto { FullName = customer.AdditionalName.Trim() }
                    : null,
                VatIdentificationNumber = !string.IsNullOrEmpty(customer.Cvr2) ? customer.Cvr2 : null,
                NationalIdentificationNumber = string.IsNullOrEmpty(customer.Cvr2) ? customer.Cpr2 : null
            };
        }
        else
        {
            secondConsumerParty = null;
        }
        return new MeteringPointConsumerDto
        {
            Start = start,
            CustomerType = !string.IsNullOrEmpty(customer.Cvr) ? "Company" : "Person",
            FirstConsumerParty = firstConsumerParty,
            SecondConsumerParty = secondConsumerParty,
            ContactPoints = mp.ContactPoints is { Count: > 0 }
                ? mp.ContactPoints.Select(BuildContactPoint).ToList()
                : null
        };
    }

    private static MeteringPointContactPointDto BuildContactPoint(ContactPoint cp)
    {
        var category = cp.Type == ContactPointType.Legal ? "LegalAddress" : "TechnicalAddress";
        return new MeteringPointContactPointDto
        {
            Category = new MeteringPointContactPointCategoryDto { Category = category },
            Address = new MeteringPointContactPointAddressDto
            {
                PostalAddress = new PostalAddressDto
                {
                    Name1 = !string.IsNullOrEmpty(cp.PrimaryName)
                        ? new ConsumerNameDto { FullName = cp.PrimaryName.Trim() }
                        : null,
                    Name2 = !string.IsNullOrEmpty(cp.SecondaryName)
                        && !string.Equals(cp.SecondaryName.Trim(), cp.PrimaryName?.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? new ConsumerNameDto { FullName = cp.SecondaryName.Trim() }
                        : null,
                    PhysicalAddress = cp.Address != null ? MapPhysicalAddress(cp.Address) : null
                }
            },
            CommunicationLines = cp.CommunicationLines is { Count: > 0 }
                ? cp.CommunicationLines.Select(cl => new MeteringPointCommunicationLineDto { Type = cl.Type, Value = cl.Value }).ToList()
                : null
        };
    }

    private static MeteringPointMeterCharacteristicsDto? BuildMeterCharacteristics(MeteringPoint mp, DateOnly start)
    {
        if (mp.Meter == null) return null;
        // Decimals/DigitsOfPrecision/ReadingOccurrence er populeret af Domain-mapperen per
        // commodity (el = 0/0/fra-ReadingInterval; vand/varme = 1/1/Hourly). Translator læser
        // dem direkte fra mp.Meter — ingen branching her.
        return new MeteringPointMeterCharacteristicsDto
        {
            Start = start,
            // Electricity: the type comes from the grid connection, the sub type from the meter
            // trade row. Water/heat have neither a Grid nor a trade row, so both stay null and the
            // serializer (WhenWritingNull) leaves them out of the JSON.
            MeteringPointType = mp.Grid != null ? MapMeteringPointType(mp.Grid.Kind) : null,
            MeteringPointSubType = mp.Meter.MeteringPointSubType,
            Meters =
            [
                new MeteringPointMeterDto
                {
                    MeterIdentifier = mp.Meter.Number,
                    DigitsOfPrecision = mp.Meter.DigitsOfPrecision,
                    MultiplicationFactor = (double)mp.Meter.MultiplicationFactor,
                    Decimals = mp.Meter.Decimals,
                    MeterReadingOccurrence = MapReadingOccurrence(mp.Meter.ReadingOccurrence),
                    ProductionType = "None"
                }
            ],
            EstimatedAnnualVolume = mp.EstimatedAnnualVolumeKwh is { } eav
                ? new QuantityDto { Unit = "kWh", Kind = "Na", Value = eav }
                : null
        };
    }

    private static MeteringPointGridDto BuildGrid(GridConnection grid, DateOnly start)
    {
        return new MeteringPointGridDto
        {
            Start = start,
            MarketBalanceAreaName = MapPriceArea(grid.PriceArea),
            GridAreaCode = grid.GridAreaCode
        };
    }

    private static PhysicalAddressDto MapPhysicalAddress(Address address)
    {
        // Bevares null'er for felter den gamle SettlExtractionService også satte null —
        // BuildingFloor/RoomIdentification/StreetCode/CitySubDivisionName/MunicipalityCode er kun
        // populated for elektricitet (fra Delpoint); water/heat har dem som null.
        return new PhysicalAddressDto
        {
            StreetName = address.StreetName ?? "",
            BuildingNumber = address.BuildingNumber ?? "",
            BuildingFloor = address.Floor,
            RoomIdentification = address.Apartment,
            PostalCode = address.PostalCode ?? "",
            City = address.City ?? "",
            StreetCode = address.StreetCode,
            CitySubDivisionName = address.CitySubDivision,
            MunicipalityCode = address.MunicipalityCode,
            CountryCode = !string.IsNullOrEmpty(address.CountryCode) ? address.CountryCode : MigrationConstants.CountryCode,
            AddressIdentifier = address.Dar != null
                ? new AddressIdentifierDto { Identification = address.Dar.Identifier.ToString() }
                : null
        };
    }

    private static string MapPriceArea(PriceArea area) => area switch
    {
        PriceArea.Dk1 => "DK1",
        PriceArea.Dk2 => "DK2",
        _ => "DK1"
    };

    private static string MapSettlementMethod(SettlementMethod method) => method switch
    {
        SettlementMethod.Flex => "Flex",
        SettlementMethod.Profiled => "Profiled",
        SettlementMethod.NonProfiled => "NonProfiled",
        _ => "Flex"
    };

    /// <summary>
    /// Domain metering point kind → Settl's MeteringPointType wire value. Only Consumption and
    /// Production reach the create path today (the extraction admits E17/Forb, FORB and PROD only),
    /// but every enum member is mapped so a widened filter does not silently send the wrong value.
    /// Note Settl spells net consumption <c>Netconsumption</c> with a lowercase c — that is why this
    /// is an explicit table and not <c>ToString()</c>.
    /// </summary>
    private static string MapMeteringPointType(MeteringPointKind kind) => kind switch
    {
        MeteringPointKind.Consumption => "Consumption",
        MeteringPointKind.Production => "Production",
        MeteringPointKind.NetProduction => "NetProduction",
        MeteringPointKind.SupplyToGrid => "SupplyToGrid",
        MeteringPointKind.ConsumptionFromGrid => "ConsumptionFromGrid",
        MeteringPointKind.OwnProduction => "OwnProduction",
        MeteringPointKind.ElectricalHeating => "ElectricalHeating",
        MeteringPointKind.NetConsumption => "Netconsumption",
        MeteringPointKind.OtherConsumption => "OtherConsumption",
        MeteringPointKind.OtherProduction => "OtherProduction",
        MeteringPointKind.Effect => "Effect",
        MeteringPointKind.WholesaleServicesInformation => "WholesaleServicesInformation",
        _ => "Consumption"
    };

    private static string MapConnectionStatus(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Disconnected => "Disconnected",
        _ => "Connected"
    };

    private static string MapReadingOccurrence(MeterReadingOccurrence occurrence) => occurrence switch
    {
        MeterReadingOccurrence.QuarterHourly => "QuarterHourly",
        _ => "Hourly"
    };
}
