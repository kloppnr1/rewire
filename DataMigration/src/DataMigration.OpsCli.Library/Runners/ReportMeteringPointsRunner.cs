using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class ReportMeteringPointsRunOptions
{
    public string DataAreaId { get; set; } = "hol";
    public DateTime? ReferenceDate { get; set; }
    public bool UseDeliveryStart { get; set; }
    public bool Verbose { get; set; }
    public string[] MeteringPoints { get; set; } = [];
    public bool IncludeChildren { get; set; }
    /// <summary>
    /// Include MPs that are no longer in delivery. Default false, matching <c>reconcile</c>: the scan
    /// covers the live population, since drift on an MP that has left delivery is historic rather
    /// than actionable. Only affects the env-wide scan — an explicit <see cref="MeteringPoints"/>
    /// filter is always honoured as given.
    /// </summary>
    public bool IncludeNotInDelivery { get; set; }
    public bool MatchTypeOnly { get; set; }
    public bool ShowAll { get; set; }
}

/// <summary>
/// Read-only scan of all billing metering points in Settl: checks currency, EAN,
/// joint invoice number, grid, type, subtype, occurrence, EAV and meters.
/// El-ekstern MPs are cross-referenced against Xellent. Non-El-ekstern MPs get
/// billing-only checks (currency, EAN, joint).
/// </summary>
public class ReportMeteringPointsRunner
{
    private static readonly string[] CsvHeader =
    [
        "EntityType", "Identification", "SettlId", "Status", "Årsager", "ReferenceDato",
        "Currency", "ElectronicNumber",
        "JointInvoiceNumber_Settl", "JointInvoiceNumber_Xellent",
        "Grid_MB_Settl", "Grid_MB_Expected", "Grid_GA_Settl", "Grid_GA_Expected",
        "MeteringPointType_Settl", "MeteringPointType_Xellent",
        "MeteringPointSubType_Settl", "MeteringPointSubType_Xellent",
        "MeterReadingOccurrence_Settl", "MeterReadingOccurrence_Xellent",
        "EAV_Settl", "EAV_Xellent", "Meters_Settl", "Meters_Xellent"
    ];

    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly IXellentDomainLoader _loader;

    /// <summary>Optional progress sink (done, total) invoked per MP — used by the report-drift dashboard job.</summary>
    public Action<int, int>? OnProgress { get; set; }

    /// <summary>
    /// Optional map of expected per-MP PaymentConfiguration overrides (keyed by metering point
    /// identification), supplied by <c>report-drift</c> from the verified domain mapper. When set,
    /// the four override fields (EAN, OrderId, AgreementId, BuyerReference) are diffed against the
    /// expectation using the same rule as <c>fix-payment-config</c> (<see cref="PaymentConfigDiff"/>),
    /// replacing the legacy "EAN must be blank on MP" heuristic. When null (standalone
    /// <c>report-meteringpoints</c>), the legacy EAN-present check is kept.
    /// </summary>
    public IReadOnlyDictionary<string, BuyerInvoicingReferences>? ExpectedMpOverrideByIdentification { get; set; }

    public ReportMeteringPointsRunner(
        ISettlMeteringPointClient meteringPointClient,
        IXellentDomainLoader loader)
    {
        _meteringPointClient = meteringPointClient;
        _loader = loader;
    }

    public async Task<int> RunAsync(ReportMeteringPointsRunOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var rows = new List<string>();
        var driftRows = new List<DriftRow>();
        var exitCode = await ScanAsync(options, rows, driftRows, cancellationToken);
        if (exitCode != 0) return exitCode;

        await output.WriteLineAsync(string.Join(";", CsvHeader.Select(CsvQuote)));
        foreach (var row in rows)
            await output.WriteLineAsync(row);

        return 0;
    }

    /// <summary>
    /// Runs the same MP scan as <see cref="RunAsync"/> but returns the structured
    /// <see cref="DriftRow"/> projection (no per-entity CSV side-output) for the unified
    /// <c>report-drift</c> aggregator. Same source of truth: both paths build their rows
    /// from the identical reason/status computation in <see cref="ProcessMeteringPointAsync"/>.
    /// </summary>
    public async Task<List<DriftRow>> ScanDriftAsync(ReportMeteringPointsRunOptions options, CancellationToken cancellationToken = default)
    {
        var rows = new List<string>();
        var driftRows = new List<DriftRow>();
        await ScanAsync(options, rows, driftRows, cancellationToken);
        return driftRows;
    }

    /// <summary>
    /// Scans an explicit set of Settl metering point GUIDs (e.g. a single customer's MPs) and returns
    /// their <see cref="DriftRow"/>s. Used by the per-entity drift lookup, which already holds the GUIDs
    /// from a customer→MP relation and so skips the identification→GUID resolution that the filtered
    /// <see cref="RunAsync"/> path does.
    /// </summary>
    public async Task<List<DriftRow>> ScanDriftByIdsAsync(IReadOnlyCollection<Guid> meteringPointIds, ReportMeteringPointsRunOptions options, CancellationToken cancellationToken = default)
    {
        var (mbMap, gaMap, mbReverseMap, gaReverseMap, gridExitCode) = await LoadGridMapsAsync(cancellationToken);
        var rows = new List<string>();
        var driftRows = new List<DriftRow>();
        if (gridExitCode != 0) return driftRows;

        foreach (var id in meteringPointIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMeteringPointAsync(id, mbMap, gaMap, mbReverseMap, gaReverseMap, options, rows, driftRows, cancellationToken);
        }
        return driftRows;
    }

    private async Task<int> ScanAsync(ReportMeteringPointsRunOptions options, List<string> rows, List<DriftRow> driftRows, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("=== report-meteringpoints (scan) ===");
        Console.Error.WriteLine();

        var (mbMap, gaMap, mbReverseMap, gaReverseMap, gridExitCode) = await LoadGridMapsAsync(cancellationToken);
        if (gridExitCode != 0) return gridExitCode;

        List<Guid> mpIds;

        if (options.MeteringPoints.Length > 0)
        {
            Console.Error.WriteLine($"=== Metering point filter: {options.MeteringPoints.Length} ===");
            Console.Error.WriteLine();
            mpIds = [];
            foreach (var identification in options.MeteringPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (mp, err) = await _meteringPointClient.SearchBillingByIdentificationAsync(identification, cancellationToken);
                if (mp == null)
                {
                    Console.Error.WriteLine($"  {identification}: ikke fundet i Settl ({err}) — springer over");
                    continue;
                }
                mpIds.Add(mp.Id);
            }
        }
        else
        {
            Console.Error.Write("  Loading metering points...");
            var selection = await SettlMeteringPointScanner.SelectAsync(
                _meteringPointClient, options.IncludeChildren, options.IncludeNotInDelivery,
                Console.Error.Write, cancellationToken);
            Console.Error.WriteLine($"\r  Scanned {selection.Scanned} metering points → {selection.MeteringPointIds.Count} selected              ");
            if (selection.SkippedChildren > 0)
                Console.Error.WriteLine($"    skipped {selection.SkippedChildren} child-MP(s) (Settl relations)");
            if (selection.SkippedNotInDelivery > 0)
                Console.Error.WriteLine($"    skipped {selection.SkippedNotInDelivery} MP(s) not in delivery (use --include-not-in-delivery to include)");
            mpIds = selection.MeteringPointIds;
        }

        Console.Error.WriteLine();

        var mpOk = 0;
        var mpNeedsUpdate = 0;
        var mpError = 0;

        var processed = 0;
        foreach (var id in mpIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ProcessMeteringPointAsync(id, mbMap, gaMap, mbReverseMap, gaReverseMap, options, rows, driftRows, cancellationToken);
            switch (outcome)
            {
                case "needsupdate": mpNeedsUpdate++; break;
                case "error": mpError++; break;
                default: mpOk++; break;
            }
            OnProgress?.Invoke(++processed, mpIds.Count);
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Summary ===");
        Console.Error.WriteLine($"  MPs checked:       {mpIds.Count}");
        Console.Error.WriteLine($"  MPs OK:            {mpOk}");
        Console.Error.WriteLine($"  MPs NeedsUpdate:   {mpNeedsUpdate}");
        Console.Error.WriteLine($"  MPs Error:         {mpError}");
        Console.Error.WriteLine();

        return 0;
    }

    private async Task<string> ProcessMeteringPointAsync(
        Guid meteringPointId,
        Dictionary<string, Guid> mbMap,
        Dictionary<string, Guid> gaMap,
        Dictionary<Guid, string> mbReverseMap,
        Dictionary<Guid, string> gaReverseMap,
        ReportMeteringPointsRunOptions options,
        List<string> rows,
        List<DriftRow> driftRows,
        CancellationToken cancellationToken)
    {
        var (currentMp, getErr) = await _meteringPointClient.GetByIdAsync(meteringPointId, cancellationToken: cancellationToken);
        if (currentMp == null)
        {
            Console.Error.WriteLine($"  MP {meteringPointId}: GET fejlede: {getErr}");
            AddMpRow(rows, driftRows, meteringPointId.ToString(), meteringPointId.ToString(), "Error", "MarketGetFailed", "",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
            return "error";
        }

        var referenceDate = options.ReferenceDate
            ?? (options.UseDeliveryStart ? currentMp.DeliveryStatus?.Start : null);

        if (referenceDate.HasValue)
        {
            (currentMp, getErr) = await _meteringPointClient.GetByIdAsync(meteringPointId, referenceDate.Value, cancellationToken);
            if (currentMp == null)
            {
                Console.Error.WriteLine($"  MP {meteringPointId}: GET (ref-date {referenceDate.Value:yyyy-MM-dd}) fejlede: {getErr}");
                AddMpRow(rows, driftRows, meteringPointId.ToString(), meteringPointId.ToString(), "Error", "MarketGetFailed", "",
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
                return "error";
            }
        }

        var identification = currentMp.Identification?.MeteringPointIdentification;
        if (string.IsNullOrEmpty(identification))
        {
            if (options.Verbose)
                Console.Error.WriteLine($"  MP {meteringPointId}: ingen identification — springer over");
            return "ok";
        }

        var hasParent = currentMp.Relation?.ParentMeteringPointId is { } parentId && parentId != Guid.Empty;
        var referenceDateStr = referenceDate?.ToString("yyyy-MM-dd") ?? "";

        if (hasParent && !options.IncludeChildren)
        {
            // Child MPs are not a drift condition — they are pulled in as relations from Settl and
            // simply skipped (use --include-children to scan them). Emitting nothing keeps them out
            // of report-drift, which would otherwise surface them as a spurious "Error/ChildMP" row.
            if (options.Verbose)
                Console.Error.WriteLine($"  {identification}: child-MP — springer over (brug --include-children)");
            return "ok";
        }

        var (billing, _) = await _meteringPointClient.GetBillingByIdAsync(meteringPointId, null, cancellationToken);
        var currencySettl = billing?.BillingConfiguration?.Currency ?? "";
        var eanSettl = billing?.PaymentConfiguration?.ElectronicNumber ?? "";
        var orderSettl = billing?.PaymentConfiguration?.OrderIdentifier ?? "";
        var agrSettl = billing?.PaymentConfiguration?.AgreementIdentifier ?? "";
        var buyerSettl = billing?.PaymentConfiguration?.BuyerReference ?? "";
        var jointSettl = billing?.BillingConfiguration?.JointInvoiceNumber ?? "";

        var gridMbSettl = currentMp.Grid?.MarketBalanceAreaId is { } mbId
            ? (mbReverseMap.TryGetValue(mbId, out var mbName) ? mbName : mbId.ToString("N"))
            : "";
        var gridGaSettl = currentMp.Grid?.MeteringGridAreaId is { } gaId
            ? (gaReverseMap.TryGetValue(gaId, out var gaName) ? gaName : gaId.ToString("N"))
            : "";

        var settleMeters = currentMp.MeterCharacteristics?.Meters ?? [];
        var typeSettl = currentMp.MeterCharacteristics?.MeteringPointType ?? "";
        var subTypeSettl = currentMp.MeterCharacteristics?.MeteringPointSubType ?? "";
        var occurrenceSettl = settleMeters.FirstOrDefault()?.MeterReadingOccurrence ?? "";
        var currentEav = currentMp.MeterCharacteristics?.EstimatedAnnualVolume;
        var settleHasEav = currentEav is { IsEmpty: false };
        var eavSettl = settleHasEav ? currentEav!.Value.ToString("F0") : "";
        var metersSettl = FormatMeters(settleMeters.Select(m => ((string?)m.MeterIdentifier, m.MultiplicationFactor ?? 0.0)));

        var xellent = await _loader.LoadMeteringPointDataAsync(identification, options.DataAreaId, cancellationToken);

        var gridMbExpected = "";
        var gridGaExpected = "";
        var jointXellent = "";
        var typeXellent = "";
        var subTypeXellent = "";
        var occurrenceXellent = "";
        var eavXellent = "";
        var metersXellent = "";
        Guid? expectedMbId = null;
        Guid? expectedGaId = null;

        if (xellent != null)
        {
            gridMbExpected = xellent.GridMarketBalanceArea;
            gridGaExpected = xellent.GridAreaCode;
            if (!string.IsNullOrEmpty(xellent.GridMarketBalanceArea) && mbMap.TryGetValue(xellent.GridMarketBalanceArea, out var mb)) expectedMbId = mb;
            if (!string.IsNullOrEmpty(xellent.GridAreaCode) && gaMap.TryGetValue(xellent.GridAreaCode, out var ga)) expectedGaId = ga;

            jointXellent = xellent.JointInvoiceGroupId ?? "";

            if (xellent.MeterReadingOccurrence == null)
            {
                Console.Error.WriteLine($"  {identification}  Error  SeriesResolutionMangler");
                AddMpRow(rows, driftRows, identification, meteringPointId.ToString(), "Error", "SeriesResolutionMangler", referenceDateStr,
                    currencySettl, eanSettl, jointSettl, jointXellent,
                    gridMbSettl, gridMbExpected, gridGaSettl, gridGaExpected,
                    typeSettl, null, subTypeSettl, null, occurrenceSettl, null,
                    eavSettl, null, metersSettl, null);
                return "error";
            }

            typeXellent = xellent.MeteringPointType ?? "";
            subTypeXellent = xellent.MeteringPointSubType ?? "";

            if (typeXellent == "" || subTypeXellent == "")
            {
                Console.Error.WriteLine($"  {identification}  Error  TypeSubTypeMangler");
                AddMpRow(rows, driftRows, identification, meteringPointId.ToString(), "Error", "TypeSubTypeMangler", referenceDateStr,
                    currencySettl, eanSettl, jointSettl, jointXellent,
                    gridMbSettl, gridMbExpected, gridGaSettl, gridGaExpected,
                    typeSettl, typeXellent, subTypeSettl, subTypeXellent, occurrenceSettl, null,
                    eavSettl, null, metersSettl, null);
                return "error";
            }

            occurrenceXellent = xellent.MeterReadingOccurrence;
            eavXellent = xellent.EstimatedAnnualVolumeKwh.HasValue ? xellent.EstimatedAnnualVolumeKwh.Value.ToString("F0") : "";
            metersXellent = FormatMeters(xellent.Meters.Select(m => (m.Id, m.MultiplicationFactor)));
        }
        else if (options.Verbose)
        {
            Console.Error.WriteLine($"  MP {identification}: ikke El-ekstern i Xellent — Xellent-checks springes over");
        }

        var reasons = new List<string>();
        string? paymentConfigDetail = null;

        if (billing == null)
        {
            reasons.Add("BillingFetchFailed");
        }
        else
        {
            if (currencySettl != "DKK") reasons.Add("Currency");

            if (ExpectedMpOverrideByIdentification != null)
            {
                // Diff-model (report-drift): compare the four PaymentConfiguration override fields
                // against the Xellent-expected per-MP override via the same rule as fix-payment-config.
                // Expected is null when the MP inherits from its Payer (or isn't in the loaded domain)
                // → blank wanted → no drift, mirroring fix-payment-config exactly.
                ExpectedMpOverrideByIdentification.TryGetValue(identification, out var expected);
                var pcDetail = new List<string>();
                void Diff(string reason, string label, string? settl, string? wanted)
                {
                    var action = PaymentConfigDiff.Classify(settl, wanted);
                    if (action == PaymentConfigDiffAction.None) return;
                    reasons.Add(reason);
                    pcDetail.Add($"{label}='{settl}'→'{wanted}'({action})");
                }
                Diff("ElectronicNumber", "EAN", eanSettl, expected?.ElectronicNumber);
                Diff("OrderIdentifier", "OrderId", orderSettl, expected?.OrderIdentifier);
                Diff("AgreementIdentifier", "AgreementId", agrSettl, expected?.AgreementIdentifier);
                Diff("BuyerReference", "BuyerRef", buyerSettl, expected?.BuyerReferenceIdentifier);
                if (pcDetail.Count > 0) paymentConfigDetail = string.Join(";", pcDetail);
            }
            else
            {
                // Legacy standalone report-meteringpoints: EAN should be blank on MP billing.
                if (!string.IsNullOrEmpty(eanSettl)) reasons.Add("ElectronicNumber");
            }

            if (xellent != null && jointSettl != jointXellent) reasons.Add("JointInvoiceNumber");
        }

        if (xellent != null && currentMp.Grid != null)
        {
            if (currentMp.Grid.MarketBalanceAreaId != expectedMbId
                || currentMp.Grid.MeteringGridAreaId != expectedGaId)
                reasons.Add("Grid");
        }

        if (xellent != null && typeXellent != "" && subTypeXellent != "")
        {
            if (typeSettl != typeXellent) reasons.Add("Type");
            if (subTypeSettl != subTypeXellent) reasons.Add("SubType");

            var occurrenceOk = settleMeters.Count == 0
                || settleMeters.All(m => m.MeterReadingOccurrence == occurrenceXellent);
            if (!occurrenceOk) reasons.Add("Occurrence");

            if (!options.MatchTypeOnly)
            {
                if (!(settleHasEav || string.IsNullOrEmpty(eavXellent))) reasons.Add("EAV");

                var xellentMeterMap = xellent.Meters.ToDictionary(m => m.Id ?? "", m => m.MultiplicationFactor);
                var settleMeterMap = settleMeters.ToDictionary(m => m.MeterIdentifier ?? "", m => m.MultiplicationFactor);
                var meterIdsOk = new HashSet<string>(xellentMeterMap.Keys).SetEquals(settleMeterMap.Keys);
                if (!meterIdsOk)
                {
                    reasons.Add("MeterIds");
                }
                else
                {
                    var multiplicationFactorOk = xellentMeterMap.All(kv =>
                        settleMeterMap.TryGetValue(kv.Key, out var sf)
                        && sf.HasValue
                        && Math.Abs(sf.Value - kv.Value) < 1e-9);
                    if (!multiplicationFactorOk) reasons.Add("MultiplicationFactor");
                }
            }
        }

        var status = reasons.Count > 0 ? "NeedsUpdate" : "OK";
        var årsager = string.Join(",", reasons);

        Console.Error.WriteLine(status == "OK"
            ? $"  {identification}  OK"
            : $"  {identification}  NeedsUpdate  {årsager}");

        if (status == "OK" && !options.ShowAll)
            return "ok";

        var showCurrency = reasons.Contains("Currency") || reasons.Contains("BillingFetchFailed");
        // In diff-model mode the EAN (and the other 3 override fields) are reported via
        // paymentConfigDetail, not the legacy single EAN column.
        var showEan = ExpectedMpOverrideByIdentification == null && reasons.Contains("ElectronicNumber");
        var showJoint = reasons.Contains("JointInvoiceNumber");
        var showGrid = reasons.Contains("Grid");
        var showType = reasons.Contains("Type");
        var showSubType = reasons.Contains("SubType");
        var showOccurrence = reasons.Contains("Occurrence");
        var showEav = reasons.Contains("EAV");
        var showMeters = reasons.Contains("MeterIds") || reasons.Contains("MultiplicationFactor");

        AddMpRow(rows, driftRows, identification, meteringPointId.ToString(), status, årsager, referenceDateStr,
            showCurrency ? currencySettl : null,
            showEan ? eanSettl : null,
            showJoint ? jointSettl : null, showJoint ? jointXellent : null,
            showGrid ? gridMbSettl : null, showGrid ? gridMbExpected : null,
            showGrid ? gridGaSettl : null, showGrid ? gridGaExpected : null,
            showType ? typeSettl : null, showType ? typeXellent : null,
            showSubType ? subTypeSettl : null, showSubType ? subTypeXellent : null,
            showOccurrence ? occurrenceSettl : null, showOccurrence ? occurrenceXellent : null,
            showEav ? eavSettl : null, showEav ? eavXellent : null,
            showMeters ? metersSettl : null, showMeters ? metersXellent : null,
            paymentConfigDetail);

        return status == "NeedsUpdate" ? "needsupdate" : "ok";
    }

    private async Task<(Dictionary<string, Guid> MbMap, Dictionary<string, Guid> GaMap, Dictionary<Guid, string> MbReverseMap, Dictionary<Guid, string> GaReverseMap, int ExitCode)>
        LoadGridMapsAsync(CancellationToken cancellationToken)
    {
        var mbMap = new Dictionary<string, Guid>();
        var gaMap = new Dictionary<string, Guid>();
        var mbReverseMap = new Dictionary<Guid, string>();
        var gaReverseMap = new Dictionary<Guid, string>();

        Console.Error.Write("  Looking up Market Balance Areas... ");
        var (mbAreas, mbErr) = await _meteringPointClient.FindMarketBalanceAreasAsync(cancellationToken);
        if (mbAreas != null)
        {
            foreach (var a in mbAreas.Where(a => a.Name != null))
            {
                mbMap[a.Name!] = a.Id;
                mbReverseMap[a.Id] = a.Name!;
            }
            Console.Error.WriteLine($"{mbMap.Count} found");
        }
        else
        {
            Console.Error.WriteLine($"FAILED: {mbErr}");
            return (mbMap, gaMap, mbReverseMap, gaReverseMap, 1);
        }

        Console.Error.Write("  Looking up Metering Grid Areas... ");
        var (gaAreas, gaErr) = await _meteringPointClient.FindMeteringGridAreasAsync(cancellationToken);
        if (gaAreas != null)
        {
            foreach (var a in gaAreas.Where(a => a.MeteringGridAreaIdentification != null))
            {
                gaMap[a.MeteringGridAreaIdentification!] = a.Id;
                gaReverseMap[a.Id] = a.MeteringGridAreaIdentification!;
            }
            Console.Error.WriteLine($"{gaMap.Count} found");
        }
        else
        {
            Console.Error.WriteLine($"FAILED: {gaErr}");
            return (mbMap, gaMap, mbReverseMap, gaReverseMap, 1);
        }

        Console.Error.WriteLine();
        return (mbMap, gaMap, mbReverseMap, gaReverseMap, 0);
    }

    private static void AddMpRow(List<string> rows, List<DriftRow> driftRows,
        string identification, string settlId, string status, string årsager, string referenceDato,
        string? currency, string? ean,
        string? jointSettl, string? jointXellent,
        string? gridMbSettl, string? gridMbExpected, string? gridGaSettl, string? gridGaExpected,
        string? typeSettl, string? typeXellent, string? subTypeSettl, string? subTypeXellent,
        string? occurrenceSettl, string? occurrenceXellent,
        string? eavSettl, string? eavXellent, string? metersSettl, string? metersXellent,
        string? paymentConfigDetail = null)
    {
        rows.Add(string.Join(";",
            CsvQuote("MP"), CsvQuote(identification), CsvQuote(settlId),
            CsvQuote(status), CsvQuote(årsager), CsvQuote(referenceDato),
            CsvQuote(currency), CsvQuote(ean),
            CsvQuote(jointSettl), CsvQuote(jointXellent),
            CsvQuote(gridMbSettl), CsvQuote(gridMbExpected), CsvQuote(gridGaSettl), CsvQuote(gridGaExpected),
            CsvQuote(typeSettl), CsvQuote(typeXellent),
            CsvQuote(subTypeSettl), CsvQuote(subTypeXellent),
            CsvQuote(occurrenceSettl), CsvQuote(occurrenceXellent),
            CsvQuote(eavSettl), CsvQuote(eavXellent), CsvQuote(metersSettl), CsvQuote(metersXellent)));

        var detail = new List<string>();
        void One(string key, string? value) { if (value != null) detail.Add($"{key}={value}"); }
        void Pair(string key, string? settl, string? expected)
        {
            if (settl != null || expected != null) detail.Add($"{key}={settl}(exp {expected})");
        }
        One("Currency", currency);
        // Diff-model: the four override fields are pre-formatted in paymentConfigDetail.
        // Legacy mode: only the single EAN column feeds the detail.
        if (paymentConfigDetail != null) detail.Add(paymentConfigDetail);
        One("ElectronicNumber", ean);
        Pair("Joint", jointSettl, jointXellent);
        Pair("Grid_MB", gridMbSettl, gridMbExpected);
        Pair("Grid_GA", gridGaSettl, gridGaExpected);
        Pair("Type", typeSettl, typeXellent);
        Pair("SubType", subTypeSettl, subTypeXellent);
        Pair("Occurrence", occurrenceSettl, occurrenceXellent);
        Pair("EAV", eavSettl, eavXellent);
        Pair("Meters", metersSettl, metersXellent);

        driftRows.Add(new DriftRow("MP", identification, settlId, status, årsager, string.Join(";", detail)));
    }

    private static string FormatMeters(IEnumerable<(string? id, double factor)> meters) =>
        string.Join(", ", meters.Select(m => $"{m.id}×{m.factor}"));

    private static string CsvQuote(string? value)
    {
        var s = value ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
