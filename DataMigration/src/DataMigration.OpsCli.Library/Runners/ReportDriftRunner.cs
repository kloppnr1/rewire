using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Runners;

public class ReportDriftRunOptions
{
    public string DataAreaId { get; set; } = "hol";
    /// <summary>Xellent snapshot date for the fan-out expectation (merge + active contracts).</summary>
    public DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }
    /// <summary>Optional narrowing: when set, the fan-out check loads exactly these accounts
    /// (profile semantics) instead of deriving the candidate set from Settl reps + CVR.</summary>
    public string[] Accounts { get; set; } = [];
    public bool NoFanOut { get; set; }
    /// <summary>Include MPs no longer in delivery in the MP scan. Default false — see
    /// <see cref="ReportMeteringPointsRunOptions.IncludeNotInDelivery"/>.</summary>
    public bool IncludeNotInDelivery { get; set; }
    public bool Verbose { get; set; }
    public bool ShowAll { get; set; }
}

/// <summary>Per-entity drift lookup scope: exactly one of <see cref="CustomerNumber"/> /
/// <see cref="MeteringPointIdentification"/> is set.</summary>
public class ReportDriftEntityOptions
{
    public string DataAreaId { get; set; } = "hol";
    public DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }
    public string? CustomerNumber { get; set; }
    public string? MeteringPointIdentification { get; set; }
    public bool ShowAll { get; set; }
}

/// <summary>
/// Unified "forkert-i-Settl" drift report. Aggregates the two genuinely Settl-driven scans
/// (<see cref="ReportMeteringPointsRunner"/> + <see cref="ReportPayersRunner"/>) and adds a
/// payer fan-out drift check (expected 1 default + N-per-unique-contract-EAN vs the actual
/// Settl payers per customer). Reads only — never mutates Settl.
///
/// <para>Fan-out is CVR-scoped: the merge key is <c>{CVR}|{Fingerprint}</c>, so a representative's
/// members always share its CVR. The candidate Xellent load is therefore scoped to the CVRs of the
/// migrated reps (derived from Settl), not the whole dataarea, while still capturing every member
/// — and it reuses the same verified merge/fan-out logic as <c>create</c>.</para>
/// </summary>
public class ReportDriftRunner
{
    private static readonly string[] CsvHeader =
        ["EntityType", "Identification", "SettlId", "Status", "Årsager", "Detalje"];

    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlPayerClient _payerClient;
    private readonly IXellentDomainLoader _loader;
    private readonly string _connectionString;
    // Payer-fan-out-scanningen bygger sit eget Xellent-lag udenom _loader, så listen skal med her.
    private readonly IReadOnlyList<string> _ignoredGroups;

    /// <summary>Phase/summary log sink (in addition to stderr) — used by the dashboard async job.</summary>
    public Action<string>? OnLog { get; set; }
    /// <summary>Progress (done, total) during the slow MP scan — used by the dashboard async job.</summary>
    public Action<int, int>? OnProgress { get; set; }

    public ReportDriftRunner(
        ISettlMeteringPointClient meteringPointClient,
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        IXellentDomainLoader loader,
        string connectionString,
        IReadOnlyList<string>? ignoredGroups = null)
    {
        _meteringPointClient = meteringPointClient;
        _customerClient = customerClient;
        _payerClient = payerClient;
        _loader = loader;
        _connectionString = connectionString;
        _ignoredGroups = ignoredGroups ?? [];
    }

    private void Log(string line)
    {
        Console.Error.WriteLine(line);
        OnLog?.Invoke(line);
    }

    public async Task<int> RunAsync(ReportDriftRunOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var allRows = await ScanAllAsync(options, cancellationToken);
        var emitted = allRows.Where(r => options.ShowAll || r.Status != "OK").ToList();

        await output.WriteLineAsync(string.Join(";", CsvHeader.Select(CsvQuote)));
        foreach (var r in emitted)
            await output.WriteLineAsync(string.Join(";",
                CsvQuote(r.EntityType), CsvQuote(r.Identification), CsvQuote(r.SettlId),
                CsvQuote(r.Status), CsvQuote(r.Årsager), CsvQuote(r.Detalje)));

        return 0;
    }

    /// <summary>
    /// Runs the full unified scan (MP + intrinsic payer + fan-out) and returns every <see cref="DriftRow"/>
    /// (OK rows included; the caller filters). This is the structured entry point for the dashboard async
    /// job; <see cref="RunAsync"/> wraps it to produce the CSV download.
    /// </summary>
    public async Task<List<DriftRow>> ScanAllAsync(ReportDriftRunOptions options, CancellationToken cancellationToken = default)
    {
        var allRows = new List<DriftRow>();

        // === Payer scan (intrinsic) first — its reps drive the single Xellent domain load that
        // feeds BOTH the MP PaymentConfiguration override diff and the fan-out check. ===
        Log("### report-drift: payers ###");
        var payerRunner = new ReportPayersRunner(_customerClient, _payerClient);
        var payerScan = await payerRunner.ScanDriftAsync(new ReportPayersRunOptions
        {
            Verbose = options.Verbose,
            ShowAll = options.ShowAll,
        }, cancellationToken);
        var actualByRep = BuildActualByRep(payerScan);

        // === Single Xellent domain load → expected payer fan-out + expected per-MP overrides. ===
        Log("### report-drift: loading Xellent expectations ###");
        var expected = await BuildExpectedAsync(
            actualByRep.Keys.ToList(), options.Accounts, options.DataAreaId,
            options.ReferenceDate, options.LegacyTg4Merge, cancellationToken);

        // === MP scan (slow: GetById per MP) — diff the 4 PaymentConfiguration override fields
        // against the Xellent expectation; forward progress to the dashboard. ===
        Log("### report-drift: metering points ###");
        var mpRunner = new ReportMeteringPointsRunner(_meteringPointClient, _loader)
        {
            OnProgress = OnProgress,
            ExpectedMpOverrideByIdentification = expected.OverrideByMp,
        };
        var mpRows = await mpRunner.ScanDriftAsync(new ReportMeteringPointsRunOptions
        {
            DataAreaId = options.DataAreaId,
            Verbose = options.Verbose,
            ShowAll = options.ShowAll,
            IncludeNotInDelivery = options.IncludeNotInDelivery,
        }, cancellationToken);

        // Payer PaymentConfiguration drift (EAN/OrderId/AgreementId/BuyerRef) — same 4 fields and rule
        // as fix-payment-config Phase A. Runs off the same payer load + domain expectation; emitted as
        // its own Payer rows alongside the intrinsic (ghost/orphan/id-format) ones.
        Log("### report-drift: payer payment-config ###");
        var payerBillingRows = await payerRunner.DiffBillingAsync(
            payerScan.Payers, expected.OverrideByPayer, options.ShowAll, cancellationToken);

        // Emit order (unchanged): MP, then Payer (intrinsic + payment-config), then Fan-out.
        allRows.AddRange(mpRows);
        allRows.AddRange(payerScan.DriftRows);
        allRows.AddRange(payerBillingRows);

        // === Payer fan-out drift (pure comparison; the load above is the basis) ===
        if (!options.NoFanOut)
        {
            Log("### report-drift: payer fan-out ###");
            var (fanRows, needsUpdate, noBasis) = BuildFanOutRows(expected.PayersByRep, actualByRep, options.ShowAll);
            Log($"  Fan-out reps checked: {actualByRep.Count} | NeedsUpdate: {needsUpdate} | no Xellent basis: {noBasis}");
            allRows.AddRange(fanRows);
        }
        else
        {
            Log("### report-drift: payer fan-out — skipped (--no-fan-out) ###");
        }

        int Drift(string type) => allRows.Count(r => r.EntityType == type && r.Status != "OK");
        Log($"=== report-drift summary: MP {Drift("MP")} | Payer {Drift("Payer")} | Fan-out {Drift("Fan-out")} | total drift {allRows.Count(r => r.Status != "OK")} ===");

        return allRows;
    }

    /// <summary>
    /// Per-entity drift lookup (fast, synchronous): scopes the checks to a single Settl customer
    /// (its MPs + payers + fan-out) or a single metering point. Reuses the bulk runners' per-entity
    /// logic via scoped lookups, so there is no full-Settl scan.
    /// </summary>
    public async Task<List<DriftRow>> ScanEntityAsync(ReportDriftEntityOptions options, CancellationToken cancellationToken = default)
    {
        var rows = new List<DriftRow>();
        var mpRunner = new ReportMeteringPointsRunner(_meteringPointClient, _loader);
        var mpOptions = new ReportMeteringPointsRunOptions { DataAreaId = options.DataAreaId, ShowAll = options.ShowAll };

        // Single metering point. The per-entity lookup has no customer/account to load the Xellent
        // basis from, so it cannot assert PaymentConfiguration override drift — pass an empty (non-null)
        // map so the diff-model is active (suppresses the legacy "EAN must be blank" false-positive)
        // but reports no override drift. Use the bulk scan or a customer lookup for override coverage.
        if (!string.IsNullOrWhiteSpace(options.MeteringPointIdentification))
        {
            mpRunner.ExpectedMpOverrideByIdentification = new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal);
            mpOptions.MeteringPoints = [options.MeteringPointIdentification];
            rows.AddRange(await mpRunner.ScanDriftAsync(mpOptions, cancellationToken));
            return rows;
        }

        if (string.IsNullOrWhiteSpace(options.CustomerNumber))
            return rows;

        // Single customer: resolve, then check its MPs + payers + fan-out.
        var (customer, custErr) = await _customerClient.SearchByCustomerNumberAsync(options.CustomerNumber, cancellationToken: cancellationToken);
        if (customer == null)
        {
            rows.Add(new DriftRow("Customer", options.CustomerNumber, "", "Error", "NotFoundInSettl", custErr ?? ""));
            return rows;
        }

        // Load the Xellent expectation once (scoped to this customer) → MP override diff + fan-out basis.
        var expected = string.IsNullOrEmpty(customer.CustomerNumber)
            ? null
            : await BuildExpectedAsync([customer.CustomerNumber], [], options.DataAreaId,
                options.ReferenceDate, options.LegacyTg4Merge, cancellationToken);
        mpRunner.ExpectedMpOverrideByIdentification =
            expected?.OverrideByMp ?? new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal);

        var (mps, _) = await _customerClient.GetMeteringPointsAsync(customer.Id, cancellationToken);
        if (mps is { Count: > 0 })
            rows.AddRange(await mpRunner.ScanDriftByIdsAsync(mps.Select(m => m.MeteringPointId).ToList(), mpOptions, cancellationToken));

        var (payers, _) = await _payerClient.FindPayersByCustomerAsync(customer.Id, cancellationToken: cancellationToken);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in payers ?? [])
        {
            if (p.PayerIdentification == null) continue;
            actual.Add(p.PayerIdentification);
            // Customer is known to exist here, so only the identification-format check applies.
            if (!ReportPayersRunner.IsValidPayerIdentification(p.PayerIdentification))
                rows.Add(new DriftRow("Payer", p.PayerIdentification, p.Id.ToString(), "NeedsUpdate", "InvalidIdentificationFormat",
                    $"CustomerId={p.CustomerId};Customer_Number={customer.CustomerNumber}"));
            else if (options.ShowAll)
                rows.Add(new DriftRow("Payer", p.PayerIdentification, p.Id.ToString(), "OK", "",
                    $"Customer_Number={customer.CustomerNumber}"));
        }

        // Payer PaymentConfiguration drift (same 4 fields as fix-payment-config Phase A).
        var payerRunner = new ReportPayersRunner(_customerClient, _payerClient);
        rows.AddRange(await payerRunner.DiffBillingAsync(
            payers ?? [],
            expected?.OverrideByPayer ?? new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal),
            options.ShowAll, cancellationToken));

        if (!string.IsNullOrEmpty(customer.CustomerNumber) && expected != null)
        {
            var (fanRows, _, _) = BuildFanOutRows(
                expected.PayersByRep,
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [customer.CustomerNumber] = actual },
                options.ShowAll);
            rows.AddRange(fanRows);
        }

        return rows;
    }

    /// <summary>Expected Xellent state for a candidate account set: payer fan-out per representative,
    /// per-MP PaymentConfiguration overrides (keyed by metering point identification, only MPs that
    /// actually override their Payer), and per-Payer PaymentConfiguration expectations (keyed by payer
    /// identification).</summary>
    private sealed record ExpectedFromXellent(
        Dictionary<string, HashSet<string>> PayersByRep,
        Dictionary<string, BuyerInvoicingReferences> OverrideByMp,
        Dictionary<string, BuyerInvoicingReferences> OverrideByPayer);

    /// <summary>Settl payer identifications grouped by representative account (= customer number).</summary>
    private static Dictionary<string, HashSet<string>> BuildActualByRep(ReportPayersRunner.PayerScanResult payerScan)
    {
        var actualByRep = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in payerScan.Payers)
        {
            if (p.PayerIdentification == null) continue;
            if (!payerScan.CustomerNumberById.TryGetValue(p.CustomerId, out var rep) || string.IsNullOrEmpty(rep)) continue;
            if (!actualByRep.TryGetValue(rep, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); actualByRep[rep] = set; }
            set.Add(p.PayerIdentification);
        }
        return actualByRep;
    }

    /// <summary>
    /// Loads the Xellent merge groups via the verified mapper and projects both the expected payer
    /// fan-out per representative AND the expected per-MP PaymentConfiguration overrides. Candidate
    /// accounts are either the explicit <paramref name="explicitAccounts"/> (profile narrowing) or
    /// CVR-scoped from <paramref name="repAccounts"/> (the merged reps seen in Settl) — the merge key
    /// <c>{CVR}|{Fingerprint}</c> guarantees same-CVR scoping captures every member.
    /// </summary>
    private async Task<ExpectedFromXellent> BuildExpectedAsync(
        IReadOnlyCollection<string> repAccounts, string[] explicitAccounts,
        string dataAreaId, DateTime referenceDate, bool legacyTg4Merge, CancellationToken cancellationToken)
    {
        var empty = new ExpectedFromXellent(
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal),
            new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal));

        var contextOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(_connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(contextOptions);

        string[] candidateAccounts;
        if (explicitAccounts.Length > 0)
        {
            candidateAccounts = explicitAccounts.Distinct(StringComparer.Ordinal).ToArray();
            Log($"  Candidate accounts: {candidateAccounts.Length} (explicit --accounts/--profile)");
        }
        else
        {
            var repCvrRows = await InChunksAsync(repAccounts, 1000, chunk => context.CustTables
                .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, Cvr = c.ExuCvrDisponent1 })
                .ToListAsync(cancellationToken));

            var distinctCvrs = repCvrRows
                .Where(r => !string.IsNullOrEmpty(r.Cvr))
                .Select(r => r.Cvr!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var emptyCvrReps = repAccounts.Count - repCvrRows.Count(r => !string.IsNullOrEmpty(r.Cvr));
            if (emptyCvrReps > 0)
                Log($"  {emptyCvrReps} rep(s) with no CVR (private/CPR) — no EAN-driven fan-out, skipped.");

            candidateAccounts = (await InChunksAsync(distinctCvrs, 1000, chunk => context.CustTables
                    .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.ExuCvrDisponent1))
                    .Select(c => c.Accountnum)
                    .ToListAsync(cancellationToken)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Log($"  Candidate accounts: {candidateAccounts.Length} (CVR-scoped from {distinctCvrs.Count} CVR / {repAccounts.Count} reps)");
        }

        if (candidateAccounts.Length == 0)
            return empty;

        // Each representative customer's Payers list IS the expected fan-out (1 default + N per unique contract-EAN).
        var warnings = new WarningCollector();
        var refDateOnly = DateOnly.FromDateTime(referenceDate);
        var mapperOptions = new MapperOptions(
            ReferenceDate: refDateOnly,
            Commodity: Commodity.Electricity,
            LegacyTg4Merge: legacyTg4Merge,
            IgnoredGroupNumbers: IgnoredGroupsLoader.ToSet(_ignoredGroups));
        var mapper = new XellentDomainMapper(context, warnings, mapperOptions, dataAreaId);
        var loadResult = await mapper.LoadAsync(candidateAccounts, cancellationToken);

        var payersByRep = loadResult.Customers.ToDictionary(
            c => c.AccountNumber,
            c => c.Payers.Select(p => p.Identifier).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        // Only MPs that actually override their Payer carry a non-null BuyerInvoicing — exactly the
        // set fix-payment-config Phase B writes. MPs that inherit have no expectation (absent ⇒ no drift).
        var overrideByMp = new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal);
        foreach (var mp in loadResult.MeteringPoints)
            if (mp.BuyerInvoicing != null)
                overrideByMp[mp.Identification] = mp.BuyerInvoicing;

        // Per-Payer expected PaymentConfiguration (EAN/OrderId/AgreementId/BuyerRef) — the same source
        // fix-payment-config Phase A backfills. Keyed by the raw payer identifier (matches Settl on the
        // non-padding environments, consistent with the fan-out comparison above).
        var overrideByPayer = new Dictionary<string, BuyerInvoicingReferences>(StringComparer.Ordinal);
        foreach (var customer in loadResult.Customers)
            foreach (var p in customer.Payers)
                if (p.BuyerInvoicing != null)
                    overrideByPayer[p.Identifier] = p.BuyerInvoicing;

        return new ExpectedFromXellent(payersByRep, overrideByMp, overrideByPayer);
    }

    /// <summary>
    /// Pure comparison of expected payer fan-out (per representative, from Xellent) against the
    /// actual Settl payer identifications (per representative). Flags <c>MissingPayers</c> when
    /// Settl lacks an expected payer (under-migrated) and <c>ExtraPayers</c> when Settl has one
    /// Xellent no longer expects. Reps with no Xellent expectation are counted (NoBasis) and
    /// skipped. OK rows are emitted only when <paramref name="showAll"/> is set.
    /// </summary>
    public static (List<DriftRow> Rows, int NeedsUpdate, int NoBasis) BuildFanOutRows(
        IReadOnlyDictionary<string, HashSet<string>> expectedByRep,
        IReadOnlyDictionary<string, HashSet<string>> actualByRep,
        bool showAll)
    {
        var rows = new List<DriftRow>();
        var needsUpdate = 0;
        var noBasis = 0;

        foreach (var rep in actualByRep.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var actual = actualByRep[rep];
            if (!expectedByRep.TryGetValue(rep, out var expected))
            {
                noBasis++;
                continue;
            }

            var missing = expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var extra = actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var reasons = new List<string>();
            if (missing.Count > 0) reasons.Add("MissingPayers");
            if (extra.Count > 0) reasons.Add("ExtraPayers");
            var status = reasons.Count > 0 ? "NeedsUpdate" : "OK";
            if (status == "NeedsUpdate") needsUpdate++;
            if (status == "OK" && !showAll) continue;

            var detail = $"expected={expected.Count};actual={actual.Count}"
                + (missing.Count > 0 ? $";missing=[{string.Join(",", missing)}]" : "")
                + (extra.Count > 0 ? $";extra=[{string.Join(",", extra)}]" : "");
            rows.Add(new DriftRow("Fan-out", rep, "", status, string.Join(",", reasons), detail));
        }

        return (rows, needsUpdate, noBasis);
    }

    private static async Task<List<TResult>> InChunksAsync<TKey, TResult>(
        IReadOnlyCollection<TKey> keys, int chunkSize, Func<TKey[], Task<List<TResult>>> query)
    {
        var results = new List<TResult>();
        foreach (var chunk in keys.Chunk(chunkSize))
            results.AddRange(await query(chunk));
        return results;
    }

    private static string CsvQuote(string? value)
    {
        var s = value ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
