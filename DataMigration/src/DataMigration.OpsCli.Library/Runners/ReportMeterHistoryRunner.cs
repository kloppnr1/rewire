using DataMigration.Core;
using DataMigration.Domain.Engros;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Runners;

public class ReportMeterHistoryRunOptions
{
    public string ProfileName { get; set; } = "";
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public bool IncludeSingleResolution { get; set; }
}

public sealed record MeterResolutionPeriod(int Seriesresolution, DateTime Start, DateTime? End);

/// <summary>
/// Read-only Xellent rapport: for hver el-ekstern MP under en profils repræsentant-accounts
/// udledes resolution-historikken (Hourly/QuarterHourly-perioder) fra
/// EXU_TIMEREADINGSPERIOD. Bruges af Settl-dev til manuelt at oprette
/// MeterCharacteristics-historik på MP'er der har skiftet aflæsnings-resolution.
/// </summary>
public class ReportMeterHistoryRunner
{
    private const string DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity;

    private static readonly string[] CsvHeader =
    [
        "MeteringPointIdentification", "MeterReadingOccurrence", "Start", "End"
    ];

    private readonly string _connectionString;
    // Bygger sit eget Xellent-lag udenom XellentDomainLoader, så listen skal med her.
    private readonly IReadOnlyList<string> _ignoredGroups;

    public ReportMeterHistoryRunner(string connectionString, IReadOnlyList<string>? ignoredGroups = null)
    {
        _connectionString = connectionString;
        _ignoredGroups = ignoredGroups ?? [];
    }

    public async Task<int> RunAsync(
        ReportMeterHistoryRunOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (options.Accounts.Length == 0)
        {
            Console.Error.WriteLine("ERROR: profile leverede ingen accounts");
            return 1;
        }

        var contextOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(_connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(contextOptions);

        Console.Error.WriteLine($"=== report-meter-history: profile='{options.ProfileName}' accounts={options.Accounts.Length} ===");

        var resolution = await AccountRepresentativeResolver.ResolveAsync(
            context, options.Accounts, options.DataAreaId, cancellationToken: cancellationToken,
            ignoredGroupNumbers: IgnoredGroupsLoader.ToSet(_ignoredGroups));
        if (resolution.MergedCount > 0)
            Console.Error.WriteLine($"  Accounts merged: {resolution.MergedCount} (non-representative group members)");

        var representativeAccounts = resolution.Accounts.ToArray();

        Console.Error.WriteLine($"Scanning Xellent for resolution-historik...");

        // Join chain: ContractTable → ContractPartTable → AgreementTable → Delpoint.
        // Only el-ekstern is delpoint-based and tracks ExuTimereadingsPeriod.
        var delpoints = await (
            from c in context.ExuContractTables
            join cp in context.ExuContractPartTables
                on new { c.Contractnum, c.Dataareaid, c.Companyid }
                equals new { cp.Contractnum, cp.Dataareaid, cp.Companyid }
            join ag in context.ExuAgreementTables
                on new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid }
                equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
            join d in context.ExuDelpoints
                on new { Attachmentnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
            where c.Dataareaid == options.DataAreaId
                && representativeAccounts.Contains(c.Custaccount)
                && d.Deliverycategory == DeliveryCategory
            select new { d.Meteringpoint, d.Num, d.Attachmentnum, d.Companyid }
        ).Distinct().ToListAsync(cancellationToken);

        if (delpoints.Count == 0)
        {
            Console.Error.WriteLine("  No el-ekstern delpoints found for representative accounts.");
            await output.WriteLineAsync(string.Join(",", CsvHeader));
            return 0;
        }

        var delpointNums = delpoints.Select(d => d.Num).Distinct().ToList();
        var rawPeriods = await context.ExuTimereadingsPeriods
            .Where(t => t.Dataareaid == options.DataAreaId
                && t.Deliverycategory == DeliveryCategory
                && delpointNums.Contains(t.Delpointnum))
            .Select(t => new { t.Delpointnum, t.Attachmentnum, t.Companyid, t.Startdate, t.Seriesresolution })
            .ToListAsync(cancellationToken);

        // Map delpoint composite key → MP identification. A given (Delpointnum, Attachmentnum)
        // should be unique per company; first match wins if duplicates somehow exist.
        var identificationByKey = delpoints
            .GroupBy(d => (d.Num, d.Attachmentnum, d.Companyid))
            .ToDictionary(g => g.Key, g => g.First().Meteringpoint);

        var rowsByMp = rawPeriods
            .Where(r => identificationByKey.ContainsKey((r.Delpointnum, r.Attachmentnum, r.Companyid)))
            .GroupBy(r => identificationByKey[(r.Delpointnum, r.Attachmentnum, r.Companyid)])
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (r.Startdate, r.Seriesresolution)).ToList());

        await output.WriteLineAsync(string.Join(",", CsvHeader));

        int mpCount = 0, rowCount = 0, skippedSingle = 0;
        foreach (var (identification, raw) in rowsByMp.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var periods = BuildResolutionPeriods(raw);
            var distinctResolutions = periods.Select(p => p.Seriesresolution).Distinct().Count();
            if (distinctResolutions < 2 && !options.IncludeSingleResolution)
            {
                skippedSingle++;
                continue;
            }

            mpCount++;
            foreach (var p in periods)
            {
                var occurrence = ExtractionMappings.MapMeterReadingOccurrence(p.Seriesresolution) ?? "";
                var startStr = p.Start.ToString("yyyy-MM-dd");
                var endStr = p.End?.ToString("yyyy-MM-dd") ?? "";
                await output.WriteLineAsync($"{identification},{occurrence},{startStr},{endStr}");
                rowCount++;
            }
        }

        Console.Error.WriteLine($"  {mpCount} MPs with resolution-history -> {rowCount} rows (skipped {skippedSingle} single-resolution MPs)");
        return 0;
    }

    /// <summary>
    /// Collapses ordered (Startdate, Seriesresolution) rows into contiguous periods per resolution.
    /// Consecutive rows with the same resolution merge into one period starting at the earliest Startdate.
    /// End = next period's Start; the last period's End is null (open).
    /// </summary>
    public static List<MeterResolutionPeriod> BuildResolutionPeriods(IEnumerable<(DateTime Startdate, int Seriesresolution)> rows)
    {
        var ordered = rows.OrderBy(r => r.Startdate).ToList();
        var result = new List<MeterResolutionPeriod>();
        if (ordered.Count == 0) return result;

        var currentStart = ordered[0].Startdate;
        var currentRes = ordered[0].Seriesresolution;

        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Seriesresolution == currentRes)
                continue;
            result.Add(new MeterResolutionPeriod(currentRes, currentStart, ordered[i].Startdate));
            currentStart = ordered[i].Startdate;
            currentRes = ordered[i].Seriesresolution;
        }
        result.Add(new MeterResolutionPeriod(currentRes, currentStart, null));
        return result;
    }
}
