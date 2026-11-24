using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Data;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using OpsAccountGrouping = DataMigration.OpsCli.Data.AccountGrouping;
using MigrationAccountGrouping = DataMigration.Migration.Runners.AccountGrouping;

[assembly: InternalsVisibleTo("DataMigration.OpsCli.Tests")]

namespace DataMigration.OpsCli.Runners;

public class GenerateStateRunOptions
{
    public string ProfilesDirectory { get; set; } = string.Empty;
    public string[] ProfileNames { get; set; } = [];
    public string ConnectionString { get; set; } = string.Empty;
    public string OutputPath { get; set; } = "migration-state.json";
    public DateTime ReferenceDate { get; set; }
    public bool NoInvoices { get; set; }
    public bool SkipActive { get; set; }
    public bool Overwrite { get; set; }
    /// <summary>Target Settl environment ("production" padder til 6 cifre; alle andre er no-op). Padded representative
    /// account is written as SettlAccountNumber so state matches Settl. See <see cref="CustomerNumberPolicy"/>.</summary>
    public string? Env { get; set; }
    /// <summary>
    /// How many years back of invoice history to keep, from <see cref="ReferenceDate"/>. Configured
    /// via <c>Invoicing:HistoryYears</c> in appsettings; defaults to <see cref="Invoice.ExportHistoryYears"/>
    /// when not set.
    /// </summary>
    public int HistoryYears { get; set; } = Invoice.ExportHistoryYears;
}

public class GenerateStateRunner
{
    private readonly Func<string[], DateTime, string, CancellationToken, Task<List<MigrationAccountGrouping>>> _extractGroupings;
    private readonly Func<string[], CancellationToken, Task<List<Invoice>>> _queryInvoices;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = Services.JsonOutput.HumanReadable
    };

    /// <param name="ignoredGroups">
    /// Kundegrupper der aldrig må merge. Bygger sit eget Xellent-lag udenom XellentDomainLoader,
    /// så listen skal med her — ellers snapshotter state andre merge-grupper end create lavede.
    /// </param>
    public GenerateStateRunner(string connectionString, IReadOnlyList<string>? ignoredGroups = null)
    {
        var ignoredGroupNumbers = IgnoredGroupsLoader.ToSet(ignoredGroups);

        _extractGroupings = async (accounts, referenceDate, deliveryCategory, ct) =>
        {
            var optionsBuilder = new DbContextOptionsBuilder<XellentDbContext>();
            optionsBuilder.UseSqlServer(connectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); });
            await using var context = new XellentDbContext(optionsBuilder.Options);
            var warnings = new WarningCollector();
            var mapperOptions = new MapperOptions(
                ReferenceDate: DateOnly.FromDateTime(referenceDate),
                Commodity: MapDeliveryCategoryToCommodity(deliveryCategory),
                IgnoredGroupNumbers: ignoredGroupNumbers);
            var mapper = new XellentDomainMapper(context, warnings, mapperOptions, MigrationConstants.DataAreaId);
            var loadResult = await mapper.LoadAsync(accounts, ct);
            var svc = new SettlMappingService(loadResult, warnings, referenceDate, deliveryCategory);
            return await svc.ExtractAccountGroupingsAsync(accounts);
        };

        _queryInvoices = async (accounts, ct) =>
        {
            var optionsBuilder = new DbContextOptionsBuilder<XellentDbContext>();
            optionsBuilder.UseSqlServer(connectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); });
            await using var context = new XellentDbContext(optionsBuilder.Options);
            Console.WriteLine("Testing database connection...");
            await context.Database.CanConnectAsync(ct);
            Console.WriteLine("  Connected to Xellent database");
            Console.WriteLine();
            return await XellentInvoiceQueries.QueryAsync(context, accounts, MigrationConstants.DataAreaId, ct);
        };
    }

    internal GenerateStateRunner(
        Func<string[], DateTime, string, CancellationToken, Task<List<MigrationAccountGrouping>>> extractGroupings)
    {
        _extractGroupings = extractGroupings;
        _queryInvoices = (_, _) => Task.FromResult(new List<Invoice>());
    }

    public async Task<int> RunAsync(GenerateStateRunOptions options, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var profilesDir = options.ProfilesDirectory;
        if (!Directory.Exists(profilesDir))
        {
            Console.WriteLine($"Profiles directory not found: {profilesDir}");
            return 1;
        }

        if (options.ProfileNames.Length == 0)
        {
            Console.WriteLine("No profiles specified.");
            return 1;
        }

        var loader = new ProfileLoader(profilesDir);
        var profiles = new List<(string Name, ProfileOptions Profile, string[] Accounts)>();
        foreach (var profileName in options.ProfileNames)
        {
            ProfileOptions profile;
            try
            {
                profile = loader.Load(profileName);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR loading profile '{profileName}': {ex.Message}");
                Console.ResetColor();
                return 1;
            }

            var accounts = profile.Accounts.Select(a => a.Trim()).Distinct().ToArray();
            if (accounts.Length == 0)
            {
                Console.WriteLine($"No accounts in profile '{profileName}'.");
                return 1;
            }

            profiles.Add((profileName, profile, accounts));
        }

        if (options.Overwrite && File.Exists(options.OutputPath))
        {
            File.Delete(options.OutputPath);
            Console.WriteLine($"Overwrite: cleared {options.OutputPath}");
            Console.WriteLine();
        }

        foreach (var (profileName, profile, profileAccounts) in profiles)
        {
            Console.WriteLine($"=== Generate State: {profileName} ({profileAccounts.Length} accounts) ===");
            Console.WriteLine();

            var exitCode = await RunTrackingStepAsync(options, profile, profileAccounts, ct);
            if (exitCode != 0)
                return exitCode;

            if (!options.NoInvoices)
            {
                exitCode = await RunInvoiceStepAsync(options, profileAccounts, ct);
                if (exitCode != 0)
                    return exitCode;
            }
        }

        if (options.NoInvoices)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("--no-invoices: skipping invoice step.");
            Console.ResetColor();
        }

        stopwatch.Stop();
        Console.WriteLine($"\nDone in {stopwatch.Elapsed}. State file: {options.OutputPath}");
        return 0;
    }

    private async Task<int> RunTrackingStepAsync(
        GenerateStateRunOptions options,
        ProfileOptions profile,
        string[] profileAccounts,
        CancellationToken ct)
    {
        Console.WriteLine("--- Step 1: tracking ---");

        var existingState = new MigrationStateDocument();
        if (File.Exists(options.OutputPath))
        {
            var existingJson = await File.ReadAllTextAsync(options.OutputPath, ct);
            existingState = JsonSerializer.Deserialize<MigrationStateDocument>(existingJson, JsonOptions)
                ?? new MigrationStateDocument();
        }

        var accountMap = existingState.Accounts.ToDictionary(a => a.SettlAccountNumber);

        var groupings = await _extractGroupings(
            profileAccounts,
            options.ReferenceDate,
            profile.Extraction.DeliveryCategory,
            ct);

        var deliveryCategory = profile.Extraction.DeliveryCategory;

        foreach (var group in groupings)
        {
            var repAccount = group.RepresentativeAccount;
            // Pad the representative account to the env's CustomerNumber length so SettlAccountNumber
            // matches what was actually written to Settl. SourceAccounts stay raw (Xellent identities).
            var settlAccountNumber = CustomerNumberPolicy.PadForEnv(repAccount, options.Env, out var wasPadded);
            if (wasPadded)
                Console.Error.WriteLine($"  WARNING: SettlAccountNumber {repAccount} padded to {settlAccountNumber} for env");
            var customerName = group.AccountNames.GetValueOrDefault(repAccount, "");
            var members = group.MemberAccounts.Order().ToList();
            var groupKey = group.GroupNum ?? settlAccountNumber;

            // Remove stale solo entries for non-representative members that are now absorbed into this group.
            // Without this, a previously-solo account would still have its own entry and overwrite the
            // correct reverse-lookup in MigrationTrackingJsonService (last-write-wins on sorted iteration).
            foreach (var member in members.Where(m => m != repAccount))
                accountMap.Remove(member);

            if (!accountMap.TryGetValue(settlAccountNumber, out var account))
            {
                account = new MigratedAccount
                {
                    SettlAccountNumber = settlAccountNumber,
                    CustomerName = customerName,
                    SourceAccounts = members,
                    GroupKey = groupKey,
                };
                accountMap[settlAccountNumber] = account;
            }
            else
            {
                account.CustomerName = customerName.Length > 0 ? customerName : account.CustomerName;
                account.SourceAccounts = members;
                account.GroupKey = groupKey;
            }

            if (!account.DeliveryCategories.Contains(deliveryCategory))
                account.DeliveryCategories.Add(deliveryCategory);
        }

        var document = new MigrationStateDocument
        {
            Accounts = accountMap.Values.OrderBy(a => a.SettlAccountNumber).ToList()
        };

        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(options.OutputPath, json, ct);

        Console.WriteLine($"Tracking: {groupings.Count} Settl accounts written to {options.OutputPath}");
        Console.WriteLine();
        return 0;
    }

    private async Task<int> RunInvoiceStepAsync(
        GenerateStateRunOptions options,
        string[] profileAccounts,
        CancellationToken ct)
    {
        Console.WriteLine("--- Step 2: invoices ---");

        try
        {
            var stateJson = await File.ReadAllTextAsync(options.OutputPath, ct);
            var state = JsonSerializer.Deserialize<MigrationStateDocument>(stateJson, JsonOptions) ?? new MigrationStateDocument();

            var relevantSettlAccounts = state.Accounts
                .Where(a => a.SourceAccounts.Any(src => profileAccounts.Contains(src)))
                .ToList();

            var accounts = relevantSettlAccounts
                .SelectMany(a => a.SourceAccounts)
                .Distinct()
                .ToArray();

            var accountToMerged = relevantSettlAccounts
                .SelectMany(a => a.SourceAccounts.Select(src => (src, a)))
                .ToDictionary(x => x.src, x => new OpsAccountGrouping(x.src, x.a.SettlAccountNumber, x.a.GroupKey));

            Console.WriteLine($"Accounts in profile: {accounts.Length}");

            var allInvoices = await _queryInvoices(accounts, ct);
            Console.WriteLine($"Found {allInvoices.Count} invoices across all accounts");

            var eligibleAccounts = InvoiceAccountFilter.GetEligibleAccounts(allInvoices);

            var totalDistinctAccounts = accounts.Select(a => a.Trim()).Distinct().Count();
            var excludedCount = totalDistinctAccounts - eligibleAccounts.Count;

            Console.WriteLine($"Accounts with most recent invoice = Slutafregning: {eligibleAccounts.Count}");
            Console.WriteLine($"Accounts excluded (active — Aconto/Opgørelse): {excludedCount}");

            if (excludedCount > 0 && !options.SkipActive)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: {excludedCount} accounts do not have Slutafregning as their most recent invoice.");
                Console.WriteLine("       These are active customers and should not be included in historical export.");
                Console.WriteLine("       Use --skip-active to exclude them and continue anyway.");
                Console.ResetColor();
                return 1;
            }

            var historyCutoff = InvoiceAccountFilter.GetHistoryCutoffDate(options.ReferenceDate, options.HistoryYears);
            var invoices = allInvoices
                .Where(i => eligibleAccounts.Contains(i.AccountNumber) && i.InvoiceDate >= historyCutoff)
                .ToList();

            Console.WriteLine($"Invoices after filtering (eligible accounts + invoices from {historyCutoff:yyyy-MM-dd} onward, {options.HistoryYears}y calendar-aligned): {invoices.Count}");
            Console.WriteLine();

            var newEntries = BuildManifestEntries(invoices, eligibleAccounts, accountToMerged);
            MergeInvoicesIntoState(state, newEntries);

            var updatedJson = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(options.OutputPath, updatedJson, ct);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Invoices merged successfully!");
            Console.ResetColor();
            Console.WriteLine($"  Settl accounts with invoices: {newEntries.Count}");
            Console.WriteLine($"  Total invoices: {newEntries.Sum(e => e.Invoices.Count)}");

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Cancelled");
            Console.ResetColor();
            return 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: {ex}");
            Console.ResetColor();
            return 1;
        }
    }

    internal static void MergeInvoicesIntoState(MigrationStateDocument state, List<MigratedAccount> newEntries)
    {
        var entriesByAccount = newEntries.ToDictionary(e => e.SettlAccountNumber);
        foreach (var account in state.Accounts)
        {
            if (entriesByAccount.TryGetValue(account.SettlAccountNumber, out var entry))
                account.Invoices = entry.Invoices;
        }
    }

    internal List<MigratedAccount> BuildManifestEntries(
        List<Invoice> invoices,
        HashSet<string> eligibleAccounts,
        Dictionary<string, OpsAccountGrouping> accountToMerged)
    {
        Console.WriteLine("Grouping invoices by Settl account...");
        var invoicesBySettlAccount = new Dictionary<string, List<InvoiceManifestItem>>();
        var groupedCount = 0;
        var totalInvoices = invoices.Count;

        foreach (var invoice in invoices)
        {
            var settlAccount = accountToMerged.TryGetValue(invoice.AccountNumber, out var grouping)
                ? grouping.MergedAccountNumber
                : invoice.AccountNumber;

            if (!invoicesBySettlAccount.TryGetValue(settlAccount, out var invoiceList))
            {
                invoiceList = [];
                invoicesBySettlAccount[settlAccount] = invoiceList;
            }

            groupedCount++;
            if (groupedCount % 100 == 0 || groupedCount == totalInvoices)
                Console.Write($"\r  Processing: {groupedCount:N0}/{totalInvoices:N0}    ");

            invoiceList.Add(new InvoiceManifestItem
            {
                InvoiceId = invoice.InvoiceId,
                SourceAccount = invoice.AccountNumber,
                BillingCategory = invoice.BillingCategory.ToLabel(),
                PaymentMedia = invoice.PaymentMedia.ToLabel(),
                InvoiceDate = invoice.InvoiceDate,
                DueDate = invoice.DueDate,
                DeliveryAddress = invoice.DeliveryAddress,
                InvoicingAddress = invoice.InvoicingAddress,
                InvoiceStatus = invoice.InvoiceStatus.ToLabel(),
                InvoiceAmount = invoice.InvoiceAmount,
                SumTax = invoice.SumTax
            });
        }
        Console.WriteLine();

        var result = new List<MigratedAccount>();
        foreach (var (settlAccount, invoiceList) in invoicesBySettlAccount)
        {
            var sourceAccounts = invoiceList.Select(i => i.SourceAccount).Distinct().OrderBy(a => a).ToList();
            var firstSourceAccount = sourceAccounts.First();
            var groupKey = accountToMerged.TryGetValue(firstSourceAccount, out var grp)
                ? grp.GroupKey ?? settlAccount
                : settlAccount;

            result.Add(new MigratedAccount
            {
                SettlAccountNumber = settlAccount,
                SourceAccounts = sourceAccounts,
                GroupKey = groupKey,
                Invoices = invoiceList
            });
        }

        Console.WriteLine($"Grouped into {result.Count} Settl accounts");
        Console.WriteLine();
        return result;
    }

    private static Commodity MapDeliveryCategoryToCommodity(string deliveryCategory) => deliveryCategory switch
    {
        MigrationConstants.DeliveryCategoryElectricity => Commodity.Electricity,
        MigrationConstants.DeliveryCategoryWater => Commodity.Water,
        MigrationConstants.DeliveryCategoryHeating => Commodity.Heat,
        MigrationConstants.DeliveryCategoryCharge => Commodity.EvCharging,
        _ => Commodity.Electricity
    };

}
