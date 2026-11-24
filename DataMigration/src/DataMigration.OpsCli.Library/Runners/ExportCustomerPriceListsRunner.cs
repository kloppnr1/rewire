using System.Diagnostics;
using System.Text.Json;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;

namespace DataMigration.OpsCli.Runners;

public class ExportCustomerPriceListsRunOptions
{
    public string[] Accounts { get; set; } = [];
    public string OutputPath { get; set; } = string.Empty;
    public string DataAreaId { get; set; } = "hol";
    public string DeliveryCategory { get; set; } = MigrationConstants.DeliveryCategoryElectricity;
    public DateTime ReferenceDate { get; set; } = DateTime.Today;

    /// <summary>
    /// Uniform Start date written for every association row. All rows for a given
    /// (customer, ProductElementCode, PriceList) are collapsed to one row starting here,
    /// avoiding Settl's "overlapping price list associations" rejection.
    /// </summary>
    public DateTime ChangeOfSupplierDate { get; set; }
    public bool LegacyTg4Merge { get; set; }

    /// <summary>Turns CVR+fingerprint merging off: every Xellent account becomes its own Settl
    /// customer, so the export emits rows per account instead of per representative. Matches
    /// all-active runs, where <c>create</c> does not merge either. Overrides
    /// <see cref="LegacyTg4Merge"/> — the precedence lives in <c>MapperOptions</c>, not here.
    /// Default false, so existing callers behave exactly as before.</summary>
    public bool DisableRepresentativeMerge { get; set; }

    /// <summary>
    /// Ordered Xellent-product → Settl ProductElementCode rules, from the
    /// <c>ProductElementMappings</c> appsettings section. First match wins.
    /// </summary>
    public IReadOnlyList<ProductElementMappingRule> ProductElementMappings { get; set; } = [];

    /// <summary>Target Settl environment ("production" padder til 6 cifre; alle andre er no-op). See <see cref="CustomerNumberPolicy"/>.</summary>
    public string? Env { get; set; }

    /// <summary>
    /// Where to write the JSON document describing what was left out of the Excel — the unmapped
    /// products and rate codes, grouped, plus the run's totals. Null (the default) writes no file at
    /// all, so a caller that does not ask for it behaves exactly as before. The Excel is unaffected
    /// either way: this reports on the export, it does not change it.
    /// </summary>
    public string? DiagnosticsPath { get; set; }
}

/// <summary>
/// Read-only generator: produces a Settl bulk-import Excel
/// (Type=CustomerPriceListAssociation, Version=2) from a profile's active ContractParts.
/// One row per (representative customer × Amrcode × ContractPart window).
/// PriceListIdentifier is resolved by translating <c>ExuContractPartTable.AMRCODE</c>
/// (prefix "KLI " → "ST ") and matching against the <c>Name</c> of the Settl price-list
/// catalog (<c>GET /api/v1/priceLists</c>); the catalog row's <c>Id</c> becomes the
/// PriceListIdentifier. This is the same catalog source the reconcile path uses.
/// ContractParts with no matching catalog row are silently skipped.
/// With <see cref="ExportCustomerPriceListsRunOptions.DisableRepresentativeMerge"/> set,
/// no accounts merge, so the row shape becomes one group per Xellent account rather than
/// one per representative customer — matching what an all-active migration created in Settl.
/// </summary>
public class ExportCustomerPriceListsRunner
{
    /// <summary>Same shape as every other JSON this repo emits (export-settlement-config, reconcile):
    /// camelCase, indented, relaxed escaping so Danish letters stay readable in the file.</summary>
    private static readonly JsonSerializerOptions DiagnosticsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JsonOutput.HumanReadable
    };

    private readonly IXellentDomainLoader _loader;
    private readonly ISettlPriceListClient _priceListClient;
    private readonly Action<string> _log;

    public ExportCustomerPriceListsRunner(IXellentDomainLoader loader, ISettlPriceListClient priceListClient, Action<string>? log = null)
    {
        _loader = loader;
        _priceListClient = priceListClient;
        _log = log ?? Console.Error.WriteLine;
    }

    public async Task<int> RunAsync(ExportCustomerPriceListsRunOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            _log("ERROR: --output is required");
            return 1;
        }
        if (options.Accounts.Length == 0)
        {
            _log("ERROR: --profile (or --account) required");
            return 1;
        }
        var (priceLists, plErr) = await _priceListClient.ListPriceListsAsync("DK", cancellationToken);
        if (plErr != null || priceLists == null)
        {
            _log($"ERROR: failed to fetch Settl price-list catalog: {plErr ?? "no data"}");
            return 1;
        }
        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var priceListsByGuid = new Dictionary<string, (string? Name, string? DisplayId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in priceLists.Where(p => !string.IsNullOrEmpty(p.Name)))
        {
            var id = pl.Id.ToString();
            nameToId[pl.Name!] = id;
            priceListsByGuid[id] = (pl.Name, pl.PriceListIdentifier);
        }
        _log($"Loaded {nameToId.Count} pricelist(s) from Settl catalog");

        var productElementResolver = new ProductElementCodeResolver(options.ProductElementMappings);
        _log($"Loaded {options.ProductElementMappings.Count} ProductElementCode mapping rule(s)");

        var sw = Stopwatch.StartNew();
        var warnings = new WarningCollector();
        _log($"Loading domain model for {options.Accounts.Length} account(s)...");
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: Commodity.Electricity,
            LegacyTg4Merge: options.LegacyTg4Merge,
            DisableRepresentativeMerge: options.DisableRepresentativeMerge);
        var loadResult = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);
        _log($"  [LoadAsync: {sw.Elapsed:mm\\:ss\\.fff}]");

        sw.Restart();
        var stillActiveOnCos = await StillActiveCustomerFilter.ResolveAsync(
            _loader, options.Accounts, options.ChangeOfSupplierDate,
            Commodity.Electricity, options.DataAreaId, options.LegacyTg4Merge,
            warnings, cancellationToken, options.DisableRepresentativeMerge);
        _log($"  [StillActiveOnCos: {sw.Elapsed:mm\\:ss\\.fff}] {stillActiveOnCos.Count} repr(s) stadig aktive i Xellent på COS — skippes");

        var extractionService = new SettlMappingService(
            loadResult, warnings, options.ReferenceDate, options.DeliveryCategory);

        sw.Restart();
        _log("Extracting account → MP mapping (representative-keyed)...");
        var accountMpMapping = await extractionService.ExtractAccountMeteringPointMappingAsync(options.Accounts);
        _log($"  [ExtractMapping: {sw.Elapsed:mm\\:ss\\.fff}]");

        var allContractNums = accountMpMapping.Values
            .SelectMany(list => list)
            .Select(mp => mp.ContractNum)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        sw.Restart();
        _log($"Looking up end dates and delivery rate codes for {allContractNums.Length} contract(s)...");
        var contractEndDates = await _loader.LookupContractEndDatesAsync(allContractNums, options.DataAreaId, cancellationToken);
        _log($"  [LookupContractEndDates: {sw.Elapsed:mm\\:ss\\.fff}]");
        sw.Restart();
        var deliveryRateCodes = await _loader.LookupDeliveryGroupRateCodesAsync(allContractNums, options.DataAreaId, cancellationToken);
        _log($"  [LookupDeliveryRateCodes: {sw.Elapsed:mm\\:ss\\.fff}]");
        sw.Restart();
        var contractPartLogEndDates = await _loader.LookupContractPartLogEndDatesAsync(allContractNums, options.DataAreaId, cancellationToken);
        _log($"  [LookupContractPartLogEndDates: {sw.Elapsed:mm\\:ss\\.fff}]");
        _log($"  → {deliveryRateCodes.Count} delivery rate code row(s), {contractPartLogEndDates.Count} log end date row(s)");

        var allAmrcodes = accountMpMapping.Values
            .SelectMany(list => list)
            .Select(mp => mp.AmrCode)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
        _log($"Unique Amrcode from Xellent ({allAmrcodes.Count}): {string.Join(", ", allAmrcodes)}");

        var allSettlNames = allAmrcodes.Select(TranslateAmrcode).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var unmatched = allSettlNames.Where(n => !nameToId.ContainsKey(n)).ToList();
        if (unmatched.Count > 0)
        {
            _log($"  → No match in pricelist CSV ({unmatched.Count}): {string.Join(", ", unmatched)}");
            const int examplesPerName = 3;
            foreach (var settlName in unmatched)
            {
                var examples = accountMpMapping
                    .SelectMany(kv => kv.Value.Select(mp => (Account: kv.Key, Mp: mp)))
                    .Where(x => !string.IsNullOrEmpty(x.Mp.AmrCode) && TranslateAmrcode(x.Mp.AmrCode) == settlName)
                    .Take(examplesPerName)
                    .Select(x => $"{x.Account} | {x.Mp.ContractNum} | {x.Mp.AmrCode} | {x.Mp.MeteringPointId}")
                    .ToList();
                foreach (var line in examples)
                    _log($"      {settlName}: {line}");
            }
        }
        var matchedNames = allSettlNames.Where(nameToId.ContainsKey).ToList();
        if (matchedNames.Count > 0)
            _log($"  → Matched in pricelist CSV ({matchedNames.Count}): {string.Join(", ", matchedNames)}");

        // Project the extraction tuple to the builder's input and delegate the association logic
        // (main loop + go-forward log + collapse) to the shared, unit-tested builder. The price-list
        // name→id resolver is the Settl catalog here — same source the reconcile path uses.
        var mpsByRepresentative = accountMpMapping.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(mp => new PriceListMpInput(
                mp.MeteringPointId, mp.ContractNum, mp.ProductNum, mp.AmrCode,
                mp.ContractPartStartdate, mp.ContractPartEnddate)).ToList(),
            StringComparer.Ordinal);

        var result = CustomerPriceListAssociationBuilder.Build(
            mpsByRepresentative,
            contractEndDates,
            deliveryRateCodes,
            contractPartLogEndDates,
            name => nameToId.TryGetValue(name, out var id) ? id : null,
            productElementResolver,
            stillActiveOnCos,
            options.ChangeOfSupplierDate,
            options.ReferenceDate,
            options.Env);

        _log($"Go-forward (slettede fremtidige dele fra log): {result.GoForwardEmitted} række(r) emitteret");

        // Built whether or not a file is asked for, so the log lines below and the JSON document
        // carry exactly the same grouping and the same counts.
        var diagnostics = CustomerPriceListDiagnostics.Build(
            result,
            representatives: accountMpMapping.Count,
            representativesStillActiveOnCos: stillActiveOnCos.Count,
            excelOutputPath: options.OutputPath,
            dataAreaId: options.DataAreaId,
            environment: options.Env,
            changeOfSupplierDate: options.ChangeOfSupplierDate,
            referenceDate: options.ReferenceDate,
            generatedAt: DateTime.UtcNow);

        if (diagnostics.UnmatchedContracts.Count > 0)
        {
            _log($"Kontrakter uden pricelist-match ({diagnostics.Totals.RowsWithoutPriceListMatch} kontrakt(er), {diagnostics.Totals.ContractCombinations} unik(ke) kombination(er)):");
            foreach (var c in diagnostics.UnmatchedContracts)
                _log($"  {c.AmrCode} | {c.DgRateCode} | {c.Contracts}");
        }

        if (diagnostics.UnmatchedProducts.Count > 0)
        {
            _log($"Udeladt af Excel — produkter uden ProductElementCode-mapping ({diagnostics.Totals.RowsWithoutProductElementCode} række(r), {diagnostics.Totals.DistinctProductsWithoutProductElementCode} produkt(er), {diagnostics.Totals.ProductCombinations} unik(ke) kombination(er)):");
            foreach (var p in diagnostics.UnmatchedProducts)
                _log($"  {p.Product} | {p.AmrCode} | {p.DgRateCode} | {p.Rows}");
        }

        sw.Restart();
        CustomerPriceListAssociationExcelWriter.Write(options.OutputPath, result.Rows);
        _log($"  [ExcelWrite: {sw.Elapsed:mm\\:ss\\.fff}]");

        _log($"Wrote {result.Rows.Count} row(s) ({result.EmittedBeforeGrouping} before grouping, {accountMpMapping.Count} representative(s)) to {options.OutputPath}");

        // Sidecar CSV with the Settl catalog Name + short PriceListIdentifier code next to the GUID
        // the Excel carries — the GUID alone can't be checked against the Settl UI's Price List
        // screen (Name / ID columns), the Name + code pair can.
        var verificationPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)) ?? ".",
            $"{Path.GetFileNameWithoutExtension(options.OutputPath)}-verify.csv");
        CustomerPriceListVerificationCsvWriter.Write(verificationPath, result.Rows, priceListsByGuid);
        _log($"Wrote verification CSV to {verificationPath}");

        if (!string.IsNullOrWhiteSpace(options.DiagnosticsPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.DiagnosticsPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                options.DiagnosticsPath,
                JsonSerializer.Serialize(diagnostics, DiagnosticsJsonOptions),
                cancellationToken);
            _log($"Wrote diagnostics ({diagnostics.Totals.ProductCombinations} product + {diagnostics.Totals.ContractCombinations} contract combination(s)) to {options.DiagnosticsPath}");
        }

        return 0;
    }

    // Xellent Amrcode prefix "KLI " maps to Settl pricelist name prefix "ST ".
    // Shared with the create-report price-list resolution (single source of truth).
    private static string TranslateAmrcode(string amrcode) =>
        PriceListNameResolver.TranslateAmrcode(amrcode);
}
