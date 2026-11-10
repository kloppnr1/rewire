using DataMigration.Domain.MeteringPoints;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Static Xellent → Settl value mappings: contact type, grid area, price area, settlement method.
/// Pure functions with no external dependencies — kept separate so mapping changes don't force
/// unrelated extractor edits.
/// </summary>
public static class ExtractionMappings
{
    /// <summary>
    /// Xellent EmsNetarea → Settl grid-area code. Xellent carries some legacy
    /// grid-area codes that Settl/DataHub doesn't recognise — those are remapped
    /// here. Unknown values pass through unchanged.
    /// </summary>
    private static readonly Dictionary<string, string> GridAreaOverrides = new()
    {
        ["755"] = "791",
        ["42"] = "331"
    };

    public static string MapGridArea(string gridArea) =>
        GridAreaOverrides.TryGetValue(gridArea, out var mapped) ? mapped : gridArea;

    /// <summary>
    /// Normalises Xellent's free-text power exchange area to Settl's canonical "DK1"/"DK2".
    /// Returns empty string for unrecognised inputs.
    /// </summary>
    public static string MapPowerExchangeArea(string powerExchangeArea)
    {
        if (string.IsNullOrEmpty(powerExchangeArea)) return "";
        var normalized = powerExchangeArea.Trim().ToUpperInvariant();
        return normalized switch
        {
            "DK VEST" or "DK1" or "VEST" or "JYLLAND" or "JYLLAND/FYN" => "DK1",
            "DK ØST" or "DK OST" or "DK2" or "ØST" or "SJÆLLAND" => "DK2",
            _ => ""
        };
    }

    /// <summary>Maps Xellent Meterpointtypecode to Settl's MeteringPointType enum.</summary>
    public static string? MapMeteringPointType(string? meterTypeCode) =>
        meterTypeCode switch
        {
            "E17/Forb" or "FORB" => "Consumption",
            "PROD" => "Production",
            "D05 M1" => "NetProduction",
            "D06 M2" => "SupplyToGrid",
            "D07 M3" => "ConsumptionFromGrid",
            "D08 Afregg" => "WholesaleServicesInformation",
            "D09 EP" => "OwnProduction",
            "D14 EV" => "ElectricalHeating",
            "D15" => "Netconsumption",
            "D17" => "OtherConsumption",
            "D18" => "OtherProduction",
            "D19" => "Effect",
            _ => null
        };

    /// <summary>
    /// Reverse-lookup: DataHub-kategori (E17/E18/D05.../D19) → set af rå Xellent
    /// Meterpointtypecode-værdier. Bruges af profil-filteret <c>MeterPointTypeCodes</c>
    /// så brugeren skriver `["E17","E18"]` i profilen mens SQL filtrerer på de rå
    /// Xellent-værdier (`E17/Forb`, `FORB`, `PROD`, ...).
    /// </summary>
    private static readonly Dictionary<string, string[]> DataHubToXellentCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["E17"] = ["E17/Forb", "FORB"],
        ["E18"] = ["PROD"],
        ["D05"] = ["D05 M1"],
        ["D06"] = ["D06 M2"],
        ["D07"] = ["D07 M3"],
        ["D08"] = ["D08 Afregg"],
        ["D09"] = ["D09 EP"],
        ["D14"] = ["D14 EV"],
        ["D15"] = ["D15"],
        ["D17"] = ["D17"],
        ["D18"] = ["D18"],
        ["D19"] = ["D19"],
    };

    /// <summary>
    /// Ekspanderer en allow-list af DataHub-kategorier til de rå Xellent
    /// Meterpointtypecode-værdier de mapper til. Throws hvis input indeholder
    /// en ukendt DataHub-kode. Returnerer null for null/tom input.
    /// </summary>
    public static string[]? ExpandDataHubCodesToXellent(IEnumerable<string>? dataHubCodes)
    {
        if (dataHubCodes == null) return null;
        var trimmed = dataHubCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToArray();
        if (trimmed.Length == 0) return null;

        var unknown = trimmed.Where(c => !DataHubToXellentCodes.ContainsKey(c)).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException(
                $"MeterPointTypeCodes indeholder ukendt DataHub-kode(r): {string.Join(", ", unknown)}. " +
                $"Tilladte: {string.Join(", ", DataHubToXellentCodes.Keys)}.");

        return trimmed.SelectMany(c => DataHubToXellentCodes[c]).Distinct().ToArray();
    }

    /// <summary>Maps Xellent Meterpointtypecode to <see cref="MeteringPointKind"/>. Throws on unknown.</summary>
    public static MeteringPointKind MapMeteringPointKind(string? meterTypeCode) =>
        meterTypeCode switch
        {
            "E17/Forb" or "FORB" => MeteringPointKind.Consumption,
            "PROD" => MeteringPointKind.Production,
            "D05 M1" => MeteringPointKind.NetProduction,
            "D06 M2" => MeteringPointKind.SupplyToGrid,
            "D07 M3" => MeteringPointKind.ConsumptionFromGrid,
            "D08 Afregg" => MeteringPointKind.WholesaleServicesInformation,
            "D09 EP" => MeteringPointKind.OwnProduction,
            "D14 EV" => MeteringPointKind.ElectricalHeating,
            "D15" => MeteringPointKind.NetConsumption,
            "D17" => MeteringPointKind.OtherConsumption,
            "D18" => MeteringPointKind.OtherProduction,
            "D19" => MeteringPointKind.Effect,
            _ => throw new InvalidOperationException($"Unknown Meterpointtypecode '{meterTypeCode}'")
        };

    /// <summary>Maps Xellent Meterpointtypecode to <see cref="SettlementMethod"/>. Throws on unknown.</summary>
    public static SettlementMethod MapSettlementMethodKind(string? meterTypeCode) =>
        meterTypeCode switch
        {
            "E17/Forb" or "FORB" => SettlementMethod.Flex,
            "PROD" => SettlementMethod.NonProfiled,
            _ => throw new InvalidOperationException($"Unexpected MeterTypeCode '{meterTypeCode}' for SettlementMethod — expected E17 or E18")
        };

    /// <summary>Maps EXU_BILLINGMETHODPERIOD.BILLINGMETHOD integer to <see cref="SettlementMethod"/>. Throws on unknown.</summary>
    public static SettlementMethod MapBillingMethod(int billingMethod) =>
        billingMethod switch
        {
            1 => SettlementMethod.Flex,
            2 => SettlementMethod.NonProfiled,
            3 => SettlementMethod.Flex,
            _ => throw new InvalidOperationException($"Unknown BILLINGMETHOD value: {billingMethod}")
        };

    /// <summary>Maps EXU_TIMEREADINGSPERIOD.SERIESRESOLUTION to Settl's MeterReadingOccurrence. 0 = Hourly, 1 = QuarterHourly.</summary>
    public static string MapMeterReadingOccurrence(int? seriesResolution) =>
        seriesResolution == 1 ? "QuarterHourly" : "Hourly";

    /// <summary>Maps EXU_METERTABLETRADE.METERINGPOINTDHSUBTYPE to Settl's MeteringPointSubType. 1 = Physical, 2 = Virtual, 3 = Calculated.</summary>
    public static string? MapMeteringPointSubTypeFromDhCode(int dhSubType) =>
        dhSubType switch
        {
            1 => "Physical",
            2 => "Virtual",
            3 => "Calculated",
            _ => null
        };
}
