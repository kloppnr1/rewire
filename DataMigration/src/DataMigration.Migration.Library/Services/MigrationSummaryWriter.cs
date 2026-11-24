using DataMigration.Core;
using DataMigration.Migration.Runners;

namespace DataMigration.Migration.Services;

/// <summary>
/// Writes the end-of-run migration summary (counts, errors, warnings) to the log callback
/// and copies collector state into the <see cref="MigrationReport"/>.
/// </summary>
public static class MigrationSummaryWriter
{
    public static bool WriteAndFinalize(
        MigrationReport report,
        IWarningCollector warnings,
        List<string> logLines,
        Action<string> logLine)
    {
        logLine("");
        logLine("=== Migration Summary ===");
        if (report.CustomersExtracted > 0)
            logLine($"  Customers:       {report.CustomersExtracted} extracted, {report.CustomersCreated} created, {report.CustomersExisting} existing, {report.CustomersFailed} failed");
        if (report.PayersExtracted > 0)
            logLine($"  Payers:          {report.PayersExtracted} extracted, {report.PayersCreated} created, {report.PayersExisting} existing, {report.PayersFailed} failed");
        if (report.MeteringPointsExtracted > 0)
            logLine($"  Metering Points: {report.MeteringPointsExtracted} extracted, {report.MeteringPointsCreated} created, {report.MeteringPointsExisting} existing, {report.MeteringPointsFailed} failed");
        if (report.CustomerMpsLinked > 0 || report.PayerMpsLinked > 0 || report.CustomerMpsFailed > 0 || report.PayerMpsFailed > 0)
        {
            logLine($"  Customer MPs:    {report.CustomerMpsLinked} linked, {report.CustomerMpsFailed} failed");
            logLine($"  Payer MPs:       {report.PayerMpsLinked} linked, {report.PayerMpsFailed} failed");
        }
        if (report.ContractMpsLinked > 0 || report.ContractMpsFailed > 0)
            logLine($"  Contract MPs:    {report.ContractMpsLinked} linked, {report.ContractMpsFailed} failed");
        if (report.BillingExemptionsApplied > 0 || report.BillingExemptionsSkipped > 0 || report.BillingExemptionsFailed > 0)
            logLine($"  Billing exempt.: {report.BillingExemptionsApplied} applied, {report.BillingExemptionsSkipped} skipped, {report.BillingExemptionsFailed} failed");

        if (report.Errors.Count > 0)
            WriteErrors(report.Errors, logLine);

        var collectorErrors = warnings.GetErrors().ToList();
        if (collectorErrors.Count > 0)
        {
            logLine("");
            logLine($"Errors ({collectorErrors.Count}):");
            foreach (var e in collectorErrors)
                logLine($"  - {e}");
        }

        var warningsList = warnings.GetWarnings().ToList();
        if (warningsList.Count > 0)
        {
            // Grouped by normalised template so hundreds of "{Entity} {id}: …" lines collapse
            // into a few meaningful buckets. Same algorithm as the dashboard (WarningGrouping).
            var groups = WarningGrouping.Group(warningsList);
            logLine("");
            logLine($"Warnings ({warningsList.Count} in {groups.Count} groups):");
            foreach (var g in groups)
            {
                logLine($"  {g.Count}x  {g.Template}");
                if (g.Ids.Count > 0)
                {
                    const int maxIds = 25;
                    var shown = string.Join(", ", g.Ids.Take(maxIds));
                    var extra = g.Ids.Count - maxIds;
                    logLine($"        {shown}{(extra > 0 ? $" (+{extra})" : "")}");
                }
            }
        }

        report.Log = [.. logLines];
        report.CollectorInfos = warnings.GetInfos().ToList();
        report.CollectorWarnings = warningsList;
        report.CollectorErrors = collectorErrors;
        report.CollectorErrorCount = warnings.ErrorCount;
        report.CollectorWarningCount = warnings.WarningCount;
        report.ValidationEntries = warnings.GetValidationEntries().ToList();

        return WriteFailedFlag(report);
    }

    /// <summary>
    /// The run's errors, with the ones that are the same error said once. Forty-two metering points
    /// refused for the same empty postal code is one problem to act on, not forty-two — and printed
    /// one by one it also pushed everything else out of the twenty lines there was room for.
    ///
    /// <para>A message that stands alone is printed as it is. The grouped form replaces ids with
    /// placeholders, which is a step backwards when there is only one of them.</para>
    /// </summary>
    private static void WriteErrors(IReadOnlyList<string> errors, Action<string> logLine)
    {
        // First appearance decides the order within equal counts, so two runs of the same batch
        // report in the same sequence.
        var groups = errors
            .Select((message, index) => (message, index))
            .GroupBy(e => WarningGrouping.Template(e.message), StringComparer.Ordinal)
            .Select(g => (
                Template: g.Key,
                Count: g.Count(),
                First: g.Min(e => e.index),
                Sample: g.First().message,
                Ids: g.SelectMany(e => WarningGrouping.Ids(e.message)).Distinct(StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.First)
            .ToList();

        logLine($"  Errors ({errors.Count} in {groups.Count} groups):");
        const int maxGroups = 20;
        foreach (var group in groups.Take(maxGroups))
        {
            if (group.Count == 1)
            {
                logLine($"    - {group.Sample}");
                continue;
            }

            logLine($"    {group.Count}x  {group.Template}");
            if (group.Ids.Count > 0)
            {
                const int maxIds = 25;
                var shown = string.Join(", ", group.Ids.Take(maxIds));
                var extra = group.Ids.Count - maxIds;
                logLine($"          {shown}{(extra > 0 ? $" (+{extra})" : "")}");
            }
        }
        if (groups.Count > maxGroups)
            logLine($"    ... and {groups.Count - maxGroups} more groups");
    }

    private static bool WriteFailedFlag(MigrationReport report)
    {
        return report.CustomersFailed > 0 || report.PayersFailed > 0
            || report.MeteringPointsFailed > 0 || report.CustomerMpsFailed > 0
            || report.PayerMpsFailed > 0 || report.ContractMpsFailed > 0
            || report.Errors.Count > 0;
    }
}
