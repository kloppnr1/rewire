using System.Text.Json;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class ReportDeliveryStartRunner
{
    private const char Separator = ';';

    private static readonly string[] CsvHeader =
    [
        "Målepunkts-ID",
        "Installationsnr",
        "Landekode",
        "I levering",
        "Leveringsstart (DK)",
        "Leveringsstart (UTC)",
        "Leveringsslut (DK)",
        "Leveringsslut (UTC)",
        "Seneste afsluttede levering - start (DK)",
        "Seneste afsluttede levering - start (UTC)",
        "Seneste afsluttede levering - slut (DK)",
        "Seneste afsluttede levering - slut (UTC)",
        "Afregningsmetode",
        "Tilslutningsstatus",
        "Målepunktstype",
        "Målepunkts-undertype",
        "Relationstype",
        "Sidst opdateret (UTC)",
        "Estimeret årsforbrug (værdi)",
        "Estimeret årsforbrug (enhed)",
        "Ekstern ID",
        "Markedsbalanceområde-ID",
        "Net-område-ID",
        "Parent-målepunkt-ID",
        "Tillægsinfo 1",
        "Tillægsinfo 2",
        "Brændselstype",
        "Anlægsnavn",
        "Vedrører produktion",
        "Installation - vej",
        "Installation - postnr",
        "Installation - by",
        "Installation - landekode",
        "Målere (JSON)",
    ];

    private static readonly TimeZoneInfo DkTz = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,

        // Ryger ned i en rapportcelle, ikke i HTML — se JsonOutput.
        Encoder = Services.JsonOutput.HumanReadable,
    };

    private readonly ISettlMeteringPointClient _meteringPointClient;

    public ReportDeliveryStartRunner(ISettlMeteringPointClient meteringPointClient)
    {
        _meteringPointClient = meteringPointClient;
    }

    public async Task<int> RunAsync(bool includeChildren, TextWriter output, CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine("=== report-delivery-start: SCAN (paginated Market list, all fields) ===");
        Console.Error.WriteLine();

        const int pageSize = 200;
        var allMps = new List<MeteringPointInfoDto>();

        Console.Error.Write("  Loading metering points...");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, _, pageErr) = await _meteringPointClient.FindMeteringPointsAsync(pageSize, allMps.Count, false, cancellationToken);
            if (pageErr != null)
            {
                Console.Error.WriteLine($"\n  FAILED at offset {allMps.Count}: {pageErr}");
                return 1;
            }
            allMps.AddRange(page);
            Console.Error.Write($"\r  Loading metering points... {allMps.Count}");
            if (page.Count < pageSize) break;
        }
        Console.Error.WriteLine($"\r  Loaded {allMps.Count} metering points              ");
        Console.Error.WriteLine();

        var rows = allMps
            .Where(mp => !string.IsNullOrEmpty(mp.MeteringPointIdentification))
            .Where(mp => includeChildren || mp.ParentMeteringPoint?.Id is null || mp.ParentMeteringPoint.Id == Guid.Empty)
            .OrderBy(mp => mp.MeteringPointIdentification, StringComparer.Ordinal)
            .ToList();

        Console.Error.WriteLine($"  Writing {rows.Count} rows (skipped {allMps.Count - rows.Count} children/uidentified)");

        await output.WriteLineAsync(string.Join(Separator, CsvHeader.Select(CsvQuote)));

        foreach (var mp in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = FormatPeriod(mp.CurrentOrNextDeliveryPeriod);
            var prev = FormatPeriod(mp.MostRecentCompletedDeliveryPeriod);
            var addr = mp.InstallationAddress;

            var cells = new[]
            {
                mp.MeteringPointIdentification ?? "",
                mp.InstallationNo ?? "",
                mp.CountryCode ?? "",
                mp.InDelivery?.ToString() ?? "",
                current.startDk, current.startUtc, current.endDk, current.endUtc,
                prev.startDk, prev.startUtc, prev.endDk, prev.endUtc,
                mp.SettlementMethodCode ?? "",
                mp.ConnectionStatus ?? "",
                mp.MeteringPointType ?? "",
                mp.MeteringPointSubType ?? "",
                mp.MeteringPointRelationType ?? "",
                mp.LastUpdated?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "",
                mp.EstimatedAnnualVolume?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                mp.EstimatedAnnualVolume?.Unit ?? "",
                mp.ExternalIdentifier ?? "",
                mp.MarketBalanceAreaId?.ToString() ?? "",
                mp.MeteringGridAreaId?.ToString() ?? "",
                mp.ParentMeteringPoint?.Id?.ToString() ?? "",
                mp.AdditionalInfo1 ?? "",
                mp.AdditionalInfo2 ?? "",
                mp.FuelType ?? "",
                mp.PlantName ?? "",
                mp.RelatesToProduction?.ToString() ?? "",
                FormatStreet(addr),
                addr?.PostalCode ?? "",
                addr?.City ?? "",
                addr?.CountryCode ?? "",
                mp.Meters is { Count: > 0 } ? JsonSerializer.Serialize(mp.Meters, CompactJson) : "",
            };

            await output.WriteLineAsync(string.Join(Separator, cells.Select(CsvQuote)));
        }

        Console.Error.WriteLine();
        return 0;
    }

    private static (string startDk, string startUtc, string endDk, string endUtc) FormatPeriod(TimePeriodDto? period)
    {
        if (period is null) return ("", "", "", "");
        var (sDk, sUtc) = FormatDateTimeOffset(period.Start);
        var (eDk, eUtc) = FormatDateTimeOffset(period.End);
        return (sDk, sUtc, eDk, eUtc);
    }

    private static (string dk, string utc) FormatDateTimeOffset(DateTimeOffset value)
    {
        if (value == default) return ("", "");
        var utc = value.UtcDateTime;
        var utcStr = utc.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var dk = TimeZoneInfo.ConvertTimeFromUtc(utc, DkTz);
        var dkStr = utc.TimeOfDay == TimeSpan.Zero
            ? dk.ToString("yyyy-MM-dd")
            : dk.ToString("yyyy-MM-dd HH:mm:ss");
        return (dkStr, utcStr);
    }

    private static string FormatStreet(PhysicalAddressDto? addr)
    {
        if (addr is null) return "";
        var parts = new[] { addr.StreetName, addr.BuildingNumber, addr.BuildingFloor, addr.RoomIdentification }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" ", parts);
    }

    private static string CsvQuote(string? value)
    {
        var s = value ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
