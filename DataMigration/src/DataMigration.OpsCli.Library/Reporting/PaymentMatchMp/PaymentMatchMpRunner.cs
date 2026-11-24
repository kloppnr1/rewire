using Microsoft.Extensions.Configuration;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

/// <summary>
/// The dashboard's "payment match by metering point list" (US 46004) as a command: reads an MP list
/// and a Settl payment-agreement export, matches them against Xellent and writes the report,
/// coverage and agreements CSVs. Read-only: it reads Xellent and the two files, and writes only
/// the CSVs. It never calls Settl.
/// </summary>
public sealed class PaymentMatchMpRunner(IPaymentMatchMpReader reader, IConfiguration? configuration, Action<string> log)
{
    public async Task<int> RunAsync(string mpListPath, string settlCsvPath, string mode, DateOnly cutover,
        string environment, string outputDirectory, CancellationToken ct)
    {
        if (mode is not ("pbs" or "mb"))
            throw new ArgumentException($"--type skal være 'pbs' eller 'mb', fik: '{mode}'");
        if (cutover == DateOnly.MinValue)
            throw new ArgumentException("Angiv en gyldig --cutover-dato.");

        var input = PaymentMatchMpList.Parse(await File.ReadAllTextAsync(mpListPath, ct));
        if (input.Unique == 0 && input.Invalid == 0)
            throw new ArgumentException("MP-listen er tom.");
        if (input.Invalid > 0)
            throw new ArgumentException($"MP-listen indeholder {input.Invalid} ugyldige værdier. Brug 18-cifrede MP-ID'er. "
                + "Første: " + string.Join(", ", input.InvalidValues.Take(5)));

        var records = SettlPaymentCsvParser.ParseContent(await File.ReadAllTextAsync(settlCsvPath, ct));
        log($"MP-liste: {input.Supplied} angivet, {input.Unique} unikke, {input.Duplicates} dubletter. "
            + $"Settl-eksport: {records.Count} rækker. Type: {mode}. Cutover: {cutover:yyyy-MM-dd}.");

        var lastStage = "";
        var result = await new PaymentMatchMpEngine(reader, configuration).RunAsync(input, records, environment, mode,
            cutover, includeTerminated: false, Path.GetFileName(mpListPath), Path.GetFileName(settlCsvPath),
            (stage, done, total) =>
            {
                if (stage == lastStage) return;
                lastStage = stage;
                log($"{stage} ({done}/{total})");
            }, ct);

        Directory.CreateDirectory(outputDirectory);
        var report = Path.Combine(outputDirectory, result.FileName("report"));
        var coverage = Path.Combine(outputDirectory, result.FileName("coverage"));
        var agreements = Path.Combine(outputDirectory, result.FileName("agreements"));
        await File.WriteAllBytesAsync(report, CsvDownload.ForExcel(result.ReportCsv()), ct);
        await File.WriteAllBytesAsync(coverage, CsvDownload.ForExcel(result.CoverageCsv()), ct);
        await File.WriteAllBytesAsync(agreements, CsvDownload.ForExcel(result.AgreementsCsv()), ct);

        var c = result.Coverage;
        log($"Match: {c.Count(r => r.Outcome == "Matched")}, ikke matchet: {c.Count(r => r.Outcome == "Unmatched")}, "
            + $"ingen relevant aftale: {c.Count(r => r.Outcome == "NoAgreement")}, "
            + $"kræver undersøgelse: {c.Count(r => r.Outcome == "Unresolved")}, "
            + $"fejl (kun vand/varme): {c.Count(r => r.ErrorCode == PaymentMpSelection.BusinessErrorCode)}.");
        log($"Rapport: {report}");
        log($"Dækning: {coverage}");
        log($"Aftaler: {agreements}");
        return 0;
    }
}
