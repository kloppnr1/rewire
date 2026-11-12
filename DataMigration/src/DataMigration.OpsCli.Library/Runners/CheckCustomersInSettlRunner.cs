using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Runners;

public sealed class CheckCustomersInSettlRunOptions
{
    public string[] Accounts { get; init; } = [];
    public string Environment { get; init; } = "featuretest";
    public string DataAreaId { get; init; } = "hol";
    public DateOnly ReferenceDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public bool LegacyTg4Merge { get; init; }
    public int Parallelism { get; init; } = 8;
}

/// <summary>Outcome for one input account.</summary>
public enum CustomerPresence
{
    /// <summary>The representative's CustomerNumber (padded for the env) is in Settl.</summary>
    Found,
    /// <summary>Found only under the un-padded legacy number (production customers created before pad-9).</summary>
    FoundUnpadded,
    NotFound,
    /// <summary>The Accountnum is not in Xellent's CustTable for the data area; Settl was not asked.</summary>
    NotInXellent,
    /// <summary>Settl answered with an error, or the number could not be normalised for the env.</summary>
    Error,
}

public sealed record CustomerPresenceRow(
    string Account,
    string? Representative,
    string? SearchedCustomerNumber,
    CustomerPresence Status,
    Guid? SettlCustomerId,
    string? Detail)
{
    /// <summary>Xellent contracts on this account that are still open on the reference date. Expected to be 0.</summary>
    public int ActiveXellentContracts { get; init; }

    /// <summary>Delivery categories of those contracts, distinct and sorted; "(none)" for a contract without one (umbrella contract).</summary>
    public string[] ActiveContractCategories { get; init; } = [];
}

/// <summary>
/// Read-only: for each Xellent Accountnum, says whether its Settl customer exists.
///
/// <para>A merged account has no Settl customer of its own; only the representative's number is
/// in Settl. So every account is first resolved to its representative with
/// <see cref="AccountRepresentativeResolver"/>, over the account's whole CVR class (the resolver
/// only merges over the accounts it is given, and a group never spans two CVRs). Then the
/// representative is looked up by CustomerNumber, padded per <see cref="CustomerNumberPolicy"/>,
/// with the un-padded number as fallback, as create does.</para>
///
/// <para>Output holds identifiers and status only, no names.</para>
/// </summary>
public sealed class CheckCustomersInSettlRunner
{
    public static readonly string[] Header =
        ["Account", "Representative", "SearchedCustomerNumber", "Status", "SettlCustomerId",
         "ActiveXellentContracts", "ActiveContractCategories", "Detail"];

    private readonly Func<XellentDbContext> _contextFactory;
    private readonly ISettlCustomerClient _customerClient;
    private readonly IReadOnlyList<string> _ignoredGroups;
    private readonly Action<string> _log;

    public CheckCustomersInSettlRunner(
        Func<XellentDbContext> contextFactory,
        ISettlCustomerClient customerClient,
        IReadOnlyList<string>? ignoredGroups = null,
        Action<string>? log = null)
    {
        _contextFactory = contextFactory;
        _customerClient = customerClient;
        _ignoredGroups = ignoredGroups ?? [];
        _log = log ?? (_ => { });
    }

    /// <summary>Reads accounts from text: one per line, or separated by comma, semicolon, tab or space. Blank and duplicate entries are dropped.</summary>
    public static string[] ParseAccounts(string text) =>
        text.Split(['\r', '\n', ',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Runs the check and writes <paramref name="outputPath"/>: an Excel workbook, or a TSV when the
    /// path ends in <c>.tsv</c>.
    /// </summary>
    public async Task<int> RunToFileAsync(CheckCustomersInSettlRunOptions options, string outputPath, CancellationToken ct = default)
    {
        if (outputPath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            await using var output = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
            return await RunAsync(options, output, ct);
        }

        var rows = await CheckAsync(options, ct);
        if (rows == null) return 1;
        WriteExcel(outputPath, rows, options);
        return ExitCode(rows);
    }

    /// <summary>Runs the check and writes it as TSV.</summary>
    public async Task<int> RunAsync(CheckCustomersInSettlRunOptions options, TextWriter output, CancellationToken ct = default)
    {
        var rows = await CheckAsync(options, ct);
        if (rows == null) return 1;

        await output.WriteLineAsync(string.Join('\t', Header));
        foreach (var row in rows)
            await output.WriteLineAsync(string.Join('\t', Cells(row)));
        return ExitCode(rows);
    }

    private static int ExitCode(IEnumerable<CustomerPresenceRow> rows) =>
        rows.Any(r => r.Status == CustomerPresence.Error) ? 2 : 0;

    private static string[] Cells(CustomerPresenceRow row) =>
    [
        row.Account, row.Representative ?? "", row.SearchedCustomerNumber ?? "", row.Status.ToString(),
        row.SettlCustomerId?.ToString() ?? "",
        row.ActiveXellentContracts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Join(',', row.ActiveContractCategories), Clean(row.Detail),
    ];

    /// <summary>
    /// Writes a workbook with a "Customers" sheet (one row per input account, as a filterable table;
    /// account numbers as text) and a "Summary" sheet (accounts per status, with active contracts).
    /// </summary>
    public static void WriteExcel(string path, IReadOnlyList<CustomerPresenceRow> rows, CheckCustomersInSettlRunOptions options)
    {
        using var book = new ClosedXML.Excel.XLWorkbook();
        var sheet = book.AddWorksheet("Customers");
        for (var c = 0; c < Header.Length; c++)
            sheet.Cell(1, c + 1).Value = Header[c];
        sheet.Range(2, 1, Math.Max(2, rows.Count + 1), 3).Style.NumberFormat.Format = "@";

        var r = 2;
        foreach (var row in rows)
        {
            var cells = Cells(row);
            for (var c = 0; c < cells.Length; c++)
            {
                if (c == 5) sheet.Cell(r, c + 1).Value = row.ActiveXellentContracts;
                else sheet.Cell(r, c + 1).Value = cells[c];
            }
            r++;
        }
        sheet.Range(1, 1, Math.Max(2, rows.Count + 1), Header.Length).CreateTable("Customers");
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        var summary = book.AddWorksheet("Summary");
        summary.Cell(1, 1).Value = "Environment";
        summary.Cell(1, 2).Value = options.Environment;
        summary.Cell(2, 1).Value = "Data area";
        summary.Cell(2, 2).Value = options.DataAreaId;
        summary.Cell(3, 1).Value = "Reference date";
        summary.Cell(3, 2).Value = options.ReferenceDate.ToString("yyyy-MM-dd");
        summary.Cell(5, 1).Value = "Status";
        summary.Cell(5, 2).Value = "Accounts";
        summary.Cell(5, 3).Value = "With active Xellent contracts";
        var s = 6;
        foreach (var group in rows.GroupBy(x => x.Status).OrderBy(g => g.Key))
        {
            summary.Cell(s, 1).Value = group.Key.ToString();
            summary.Cell(s, 2).Value = group.Count();
            summary.Cell(s, 3).Value = group.Count(x => x.ActiveXellentContracts > 0);
            s++;
        }
        summary.Cell(s, 1).Value = "Total";
        summary.Cell(s, 2).Value = rows.Count;
        summary.Cell(s, 3).Value = rows.Count(x => x.ActiveXellentContracts > 0);
        summary.Row(5).Style.Font.Bold = true;
        summary.Row(s).Style.Font.Bold = true;
        summary.Columns().AdjustToContents();

        book.SaveAs(path);
    }

    /// <summary>Resolves, looks up and logs a summary. Null when no accounts were given.</summary>
    public async Task<List<CustomerPresenceRow>?> CheckAsync(CheckCustomersInSettlRunOptions options, CancellationToken ct = default)
    {
        if (options.Accounts.Length == 0)
        {
            _log("ERROR: no accounts given");
            return null;
        }

        _log($"=== check-customers-in-settl: env={options.Environment} dataArea={options.DataAreaId} accounts={options.Accounts.Length} ===");

        Dictionary<string, string> representatives;
        Dictionary<string, List<string>> openContracts;
        await using (var context = _contextFactory())
        {
            representatives = await ResolveRepresentativesAsync(context, options, ct);
            openContracts = await LoadActiveContractsAsync(context, options.Accounts, options.DataAreaId, options.ReferenceDate, ct);
        }

        var rows = (await LookupAsync(options.Accounts, representatives, options.Environment, options.Parallelism, ct))
            .Select(r => openContracts.TryGetValue(r.Account, out var categories)
                ? r with
                {
                    ActiveXellentContracts = categories.Count,
                    ActiveContractCategories = categories
                        .Select(c => string.IsNullOrWhiteSpace(c) ? "(none)" : c.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                }
                : r)
            .ToList();

        foreach (var group in rows.GroupBy(r => r.Status).OrderBy(g => g.Key))
            _log($"  {group.Key}: {group.Count()}");

        var withActive = rows.Count(r => r.ActiveXellentContracts > 0);
        if (withActive > 0)
            _log($"  WARNING: {withActive} account(s) still have active Xellent contracts on {options.ReferenceDate:yyyy-MM-dd} (expected none); see ActiveXellentContracts");
        else
            _log($"  No account has an active Xellent contract on {options.ReferenceDate:yyyy-MM-dd}");

        return rows;
    }

    /// <summary>
    /// Input account → representative, for the accounts that exist in Xellent. Accounts missing
    /// from CustTable are left out of the map.
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveRepresentativesAsync(
        XellentDbContext context, CheckCustomersInSettlRunOptions options, CancellationToken ct)
    {
        var area = options.DataAreaId;

        // Input accounts and their CVR.
        var cvrByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in options.Accounts.Chunk(1000))
        {
            var batch = await context.CustTables.AsNoTracking()
                .Where(c => c.Dataareaid == area && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 })
                .ToListAsync(ct);
            foreach (var c in batch)
            {
                found.Add(c.Accountnum);
                if (!string.IsNullOrWhiteSpace(c.ExuCvrDisponent1)) cvrByAccount[c.Accountnum] = c.ExuCvrDisponent1;
            }
        }
        _log($"  In Xellent: {found.Count}/{options.Accounts.Length}");

        // Every account sharing those CVRs, so a member's representative is resolved even when
        // the representative itself is not in the input list.
        var accountsByCvr = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var chunk in cvrByAccount.Values.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var batch = await context.CustTables.AsNoTracking()
                .Where(c => c.Dataareaid == area && chunk.Contains(c.ExuCvrDisponent1))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 })
                .ToListAsync(ct);
            foreach (var c in batch)
            {
                cvrByAccount[c.Accountnum] = c.ExuCvrDisponent1!;
                var key = c.ExuCvrDisponent1!.Trim();
                if (!accountsByCvr.TryGetValue(key, out var members)) accountsByCvr[key] = members = [];
                members.Add(c.Accountnum);
            }
        }

        var ignored = IgnoredGroupsLoader.ToSet(_ignoredGroups);
        var representatives = new Dictionary<string, string>(StringComparer.Ordinal);
        var portions = CosStatePlanner.BuildCvrPortions(
            found.Order(StringComparer.Ordinal), cvrByAccount, accountsByCvr, RepresentativePortioning.PortionSize);
        foreach (var portion in portions)
        {
            var resolution = await AccountRepresentativeResolver.ResolveAsync(
                context, portion.Accounts, area,
                legacyTg4Merge: options.LegacyTg4Merge,
                referenceDate: options.ReferenceDate,
                cancellationToken: ct,
                ignoredGroupNumbers: ignored);
            foreach (var seed in portion.SeedAccounts)
                representatives[seed] = resolution.RepresentativeByAccount.GetValueOrDefault(seed, seed);
        }

        var merged = representatives.Count(kv => kv.Key != kv.Value);
        _log($"  Merged into another representative: {merged}");
        return representatives;
    }

    /// <summary>
    /// Account → delivery category of each Xellent contract still open on <paramref name="referenceDate"/>:
    /// no end date (the 1900-01-01 sentinel) or an end date on or after it. A contract that starts
    /// later also counts. Contracts of every delivery category count, umbrella contracts included.
    /// Accounts with none are left out.
    /// </summary>
    public static async Task<Dictionary<string, List<string>>> LoadActiveContractsAsync(
        XellentDbContext context, IReadOnlyList<string> accounts, string dataAreaId, DateOnly referenceDate, CancellationToken ct)
    {
        var day = referenceDate.ToDateTime(TimeOnly.MinValue);
        var noDate = DataMigration.Core.MigrationConstants.EndDateNull;
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var chunk in accounts.Chunk(1000))
        {
            var batch = await context.ExuContractTables.AsNoTracking()
                .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.Custaccount)
                    && (c.Contractenddate == noDate || c.Contractenddate >= day))
                .Select(c => new { c.Custaccount, c.Deliverycategory })
                .ToListAsync(ct);
            foreach (var c in batch)
            {
                if (!result.TryGetValue(c.Custaccount, out var list)) result[c.Custaccount] = list = [];
                list.Add(c.Deliverycategory ?? "");
            }
        }
        return result;
    }

    /// <summary>
    /// Looks up each account's representative in Settl. Each representative is asked once.
    /// Output order follows <paramref name="accounts"/>.
    /// </summary>
    public async Task<List<CustomerPresenceRow>> LookupAsync(
        IReadOnlyList<string> accounts,
        IReadOnlyDictionary<string, string> representativeByAccount,
        string environment,
        int parallelism,
        CancellationToken ct)
    {
        var length = CustomerNumberPolicy.LengthForEnv(environment);
        var reps = accounts
            .Where(representativeByAccount.ContainsKey)
            .Select(a => representativeByAccount[a])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var byRep = new System.Collections.Concurrent.ConcurrentDictionary<string, (string? Searched, CustomerPresence Status, Guid? Id, string? Detail)>(StringComparer.Ordinal);
        var done = 0;
        await Parallel.ForEachAsync(reps, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct },
            async (rep, token) =>
            {
                byRep[rep] = await LookupOneAsync(rep, length, token);
                var n = Interlocked.Increment(ref done);
                if (n % 500 == 0) _log($"  Settl lookups: {n}/{reps.Length}");
            });

        return accounts.Select(account =>
        {
            if (!representativeByAccount.TryGetValue(account, out var rep))
                return new CustomerPresenceRow(account, null, null, CustomerPresence.NotInXellent, null, null);
            var r = byRep[rep];
            return new CustomerPresenceRow(account, rep, r.Searched, r.Status, r.Id, r.Detail);
        }).ToList();
    }

    private async Task<(string? Searched, CustomerPresence Status, Guid? Id, string? Detail)> LookupOneAsync(
        string representative, int? length, CancellationToken ct)
    {
        string padded;
        bool wasPadded;
        try
        {
            padded = CustomerNumberPolicy.Pad(representative, length, out wasPadded);
        }
        catch (Exception ex)
        {
            return (null, CustomerPresence.Error, null, ex.Message);
        }

        var (customer, error) = await _customerClient.SearchByCustomerNumberAsync(padded, cancellationToken: ct);
        if (error != null) return (padded, CustomerPresence.Error, null, error);
        if (customer != null) return (padded, CustomerPresence.Found, customer.Id, null);

        if (wasPadded)
        {
            var (legacy, legacyError) = await _customerClient.SearchByCustomerNumberAsync(representative, cancellationToken: ct);
            if (legacyError != null) return (representative, CustomerPresence.Error, null, legacyError);
            if (legacy != null) return (representative, CustomerPresence.FoundUnpadded, legacy.Id, null);
        }

        return (padded, CustomerPresence.NotFound, null, null);
    }

    private static string Clean(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        var flat = detail.ReplaceLineEndings(" ").Replace('\t', ' ');
        return flat.Length > 200 ? flat[..200] : flat;
    }
}
