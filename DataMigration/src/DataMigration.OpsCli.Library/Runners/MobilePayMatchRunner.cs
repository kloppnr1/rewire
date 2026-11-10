using DataMigration.Core;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.Reporting;
using DataMigration.SettlApi.Clients;

namespace DataMigration.OpsCli.Runners;

/// <summary>
/// Generates the MobilePay handover sheet for the payment service: takes the Xellent MobilePay
/// agreements of the customers Settl holds, looks their Settl customer number (E-number) up in the
/// payment agreement CSV by metering point (or joint invoice number as fallback), and writes the
/// result as an Excel file pairing the E-number with the MobilePayAftale ID.
///
/// <para>What the sheet delivers is the pair (E-number, MobilePay agreement id) that lets the payment
/// service create the MobilePay agreement on the Settl side. It therefore does not care which payment
/// method supplied the E-number — and it is not a check on whether MobilePay already exists in Settl,
/// which for nearly every customer it does not. That is the work the sheet hands over.</para>
///
/// <para>One row per agreement. A MobilePay agreement can sit on any contract under the customer
/// number — main, sub or standalone — so every row carries the provenance of its agreement: which
/// contract, of which kind, when it ended, and whether that contract is the one that was migrated.
/// Rows that need a human decision are called out in the Bemærkning column and coloured.</para>
/// </summary>
public class MobilePayMatchRunner
{
    private readonly string _connectionString;
    private readonly ISettlMeteringPointClient _mpClient;

    public MobilePayMatchRunner(string connectionString, ISettlMeteringPointClient mpClient)
    {
        _connectionString = connectionString;
        _mpClient = mpClient;
    }

    /// <param name="profile">Profile to narrow the rows to, or <c>null</c> for every migrated
    /// account holding an active MobilePay agreement. The profile only ever supplied the account
    /// list and the data area — nothing else about it reaches the sheet.</param>
    /// <param name="accounts">Accounts to keep. The scan itself is unaffected: Settl decides the
    /// population either way, and this filters the rows afterwards. <c>null</c> keeps them
    /// all.</param>
    public async Task<int> RunAsync(
        ProfileOptions? profile,
        string[]? accounts,
        string settlCsvPath,
        string? outputDirectory = null,
        bool includeTerminated = false)
    {
        var scopeLabel = profile?.Name ?? "alle migrerede konti med aktiv MobilePay-aftale";
        Console.WriteLine($"=== Mobile Pay Match ({scopeLabel}) ===");
        if (includeTerminated)
            Console.WriteLine("  Inkluderer opsagte MobilePay-aftaler opsagt efter COS (--include-terminated)");
        Console.WriteLine();

        if (!File.Exists(settlCsvPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: Settl CSV file not found: {settlCsvPath}");
            Console.ResetColor();
            return 1;
        }

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
        // What the export can actually be searched by. A run where every row comes back without an
        // E-number looks identical whether Settl holds no payment agreements or the uploaded export
        // simply carries no metering points — and only these two numbers tell them apart.
        Console.WriteLine(
            $"    heraf {settlRecords.Count(r => !string.IsNullOrEmpty(r.MeteringpointId))} med målepunkt, "
            + $"{settlRecords.Count(r => string.IsNullOrEmpty(r.MeteringpointId) && !string.IsNullOrEmpty(r.JointInvoiceNumber))} "
            + "med fællesfakturanr. (opslagsnøglerne)");

        var dataAreaId = profile?.Extraction.DataAreaId ?? MigrationConstants.DataAreaId;

        // Settl drives: metering points + COS resolve the owning account, exactly as reconcile does.
        // The profile, when given, only narrows the result afterwards.
        var service = new MobilePaySettlMatchService(_mpClient, _connectionString, Console.WriteLine);
        var result = await service.MatchAsync(dataAreaId, settlRecords, default, includeTerminated);
        var rows = result.Rows;

        if (accounts is not null)
        {
            var wanted = accounts.ToHashSet(StringComparer.Ordinal);
            rows = [.. rows.Where(r => wanted.Contains(r.Account))];
            // Recount from the filtered rows: the summary below printed the whole environment's
            // tallies next to the profile's row count, which read as if the profile had thousands
            // of unmigrated customers. The scan facts are carried through unchanged — narrowing to
            // a profile does not change how much of Settl was read.
            result = MobilePayMatchResult.From(rows, result.Scan);
            Console.WriteLine($"  {rows.Count} aftaler inden for profilens konti");
        }

        var outDir = outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "output");
        Directory.CreateDirectory(outDir);
        var outputPath = Path.Combine(outDir, $"MobileMatchReport_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

        PaymentMatchWorkbookWriter.Write(
            outputPath, "MobilePay", PaymentMatchColumns.MobilePay, MobilePayMatchSheet.Build(rows));

        Console.WriteLine();
        Console.WriteLine("Results:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  E-nummer fundet (MP):      {result.Matched}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"  E-nummer fundet (fælles):  {result.MatchedJoint}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  INTET E-nummer:            {result.NoSettlCustomerNumber}   <- migreret, men ingen betalingsaftale i Settl at slå op på");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Kræver gennemsyn:          {result.NeedsReview}  (se Bemærkning-kolonnen)");
        Console.ResetColor();
        Console.WriteLine($"  Total:                     {rows.Count}");
        if (result.Scan is { } scan)
        {
            Console.WriteLine(
                $"  Scannet:                   {scan.SettlMeteringPointsScanned} Settl-målepunkter " +
                $"→ {scan.MigratedAccountsResolved} konti → {scan.AgreementsRead} aftaler");
            // Name the population's boundary. Only electricity metering points are paginated, so
            // customers migrated on water or heat alone are outside this report entirely — not
            // reported as unmigrated, simply not covered. MobilePay sits on those contracts as
            // readily as on electricity ones, so the gap is worth stating every run.
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(
                "  BEMÆRK: kun el-målepunkter i levering scannes. Kunder migreret på vand/varme "
                + "alene, og målepunkter der er ude af levering, er uden for rapportens dækning.");
            Console.ResetColor();
        }
        Console.WriteLine();

        // Break the review count down so the operator knows what kind of work is waiting
        // without opening the sheet first.
        foreach (var group in rows.SelectMany(r => r.Remarks)
                                  .GroupBy(r => r, StringComparer.Ordinal)
                                  .OrderByDescending(g => g.Count()))
            Console.WriteLine($"    {group.Count(),5}x {group.Key}");

        Console.WriteLine();
        Console.WriteLine($"  Excel: {outputPath}");
        Console.WriteLine();
        Console.WriteLine("Done.");
        return 0;
    }


}
