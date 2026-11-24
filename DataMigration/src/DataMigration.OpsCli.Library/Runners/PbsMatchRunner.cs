using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Runners;

/// <summary>
/// Runs the PBS match report: queries Xellent for PBS contracts + metering points,
/// parses Settl CSV, matches by metering point, and generates reports.
/// </summary>
public class PbsMatchRunner
{
    private readonly string _connectionString;

    public PbsMatchRunner(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<int> RunAsync(ProfileOptions profile, string[] accounts, string settlCsvPath, string? outputDirectory = null, DateTime? referenceDate = null, bool includeTerminated = false)
    {
        Console.WriteLine($"=== PBS Match Report ({profile.Name}) ===");
        if (includeTerminated)
            Console.WriteLine("  Inkluderer afmeldte PBS-aftaler (--include-terminated)");
        Console.WriteLine();

        // Validate input file
        if (!File.Exists(settlCsvPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: Settl CSV file not found: {settlCsvPath}");
            Console.ResetColor();
            return 1;
        }

        // Parse Settl CSV
        Console.WriteLine($"Reading Settl CSV: {settlCsvPath}");
        var parser = new SettlCsvParser();
        List<SettlCsvRecord> settlRecords;
        try
        {
            settlRecords = parser.Parse(settlCsvPath);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR parsing Settl CSV: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
        Console.WriteLine($"  Found {settlRecords.Count} Settl records");

        // Query Xellent for PBS contracts + metering points scoped to profile accounts
        var dataAreaId = profile.Extraction.DataAreaId;
        var accountSet = accounts.ToHashSet();
        var cos = profile.Extraction.ChangeOfSupplierDate;

        Console.WriteLine($"Querying Xellent database for PBS contracts and metering points ({accounts.Length} accounts)...");

        var queryResult = await XellentPbsContractReader.FetchAsync(_connectionString, dataAreaId, accountSet, includeTerminated, cos);
        Console.WriteLine($"  Found {queryResult.Contracts.Count} Xellent PBS contracts");

        // Målepunkter hvor profilens kontrakt udløb før cutover tilhører en anden kunde nu. De blev
        // tidligere matchet alligevel — skriv tallet, så det er synligt hvad filtret fjerner.
        if (queryResult.ExpiredBeforeCutoverMeteringPoints > 0)
            Console.WriteLine($"  {queryResult.ExpiredBeforeCutoverMeteringPoints} målepunkter udeladt: kontrakten udløb før cutover");

        if (!cos.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ADVARSEL: profilen har ingen ChangeOfSupplierDate, så fraflyttede");
            Console.WriteLine("  kontrakter kan ikke sorteres fra. Rækker kan pege på målepunkter");
            Console.WriteLine("  der har skiftet indehaver.");
            Console.ResetColor();
        }

        // Match. Annotering sker inde i servicen, saa dashboardet — der kalder den direkte — ikke
        // skal holde sin egen kopi af de samme bemærkninger ved lige.
        Console.WriteLine("Matching PBS agreements with Settl records by metering point...");
        var matchService = new PbsMatchService();
        var results = matchService.Match(queryResult, settlRecords, includeTerminated, cos);

        // Summary
        var matched = results.Count(r => r.MatchStatus == PbsMatchStatus.Matched);
        var matchedJoint = results.Count(r => r.MatchStatus == PbsMatchStatus.MatchedJoint);
        var unmatchedXellent = results.Count(r => r.MatchStatus == PbsMatchStatus.UnmatchedXellent);
        var unmatchedSettl = results.Count(r => r.MatchStatus == PbsMatchStatus.UnmatchedSettl);
        var mismatch = results.Count(r => r.MatchStatus == PbsMatchStatus.Mismatch);

        Console.WriteLine();
        Console.WriteLine("Results:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Matched:             {matched}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"  Matched (Joint):     {matchedJoint}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Unmatched (Xellent): {unmatchedXellent}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Unmatched (Settl):   {unmatchedSettl}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Mismatch:            {mismatch}");
        Console.ResetColor();
        Console.WriteLine($"  Total:               {results.Count}");

        // Uden det her laeses tallet ovenfor som en fejlliste. Rapportens population er profilens
        // konti, ikke Settls — og profiler migreres rutinemaessigt kun delvist.
        if (unmatchedXellent > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(
                "  BEMÆRK: Unmatched (Xellent) dækker både kunder migreret uden betalingsaftale i "
                + "Settl OG kunder der aldrig blev migreret. Rapporten kender kun profilens konti, "
                + "ikke hvilke af dem Settl holder, så de to kan ikke skilles ad her.");
            Console.ResetColor();
        }

        // Generate reports
        var outDir = outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "output");
        Directory.CreateDirectory(outDir);

        // Profilens ene skiftedato. MobilePay har én pr. målepunkt fra Settl; PBS har kun denne, og
        // den skrives på hver række så de to rapporter bærer samme kolonne.
        var cosDate = cos.HasValue ? DateOnly.FromDateTime(cos.Value) : (DateOnly?)null;
        var contractStatusMap = new Dictionary<string, string>(queryResult.ContractStatusMap);
        var reportGenerator = new PbsMatchReportGenerator();
        var htmlPath = await reportGenerator.GenerateHtmlReportAsync(outDir, results, contractStatusMap, cosDate);
        Console.WriteLine($"  HTML report: {htmlPath}");

        var csvPath = await reportGenerator.GenerateCsvReportAsync(outDir, results, contractStatusMap, cosDate);
        Console.WriteLine($"  CSV report:  {csvPath}");

        var xlsxPath = await reportGenerator.GenerateXlsxReportAsync(outDir, results, contractStatusMap, cosDate);
        Console.WriteLine($"  Excel:       {xlsxPath}");

        Console.WriteLine();
        Console.WriteLine("Done.");
        return 0;
    }
}
