using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;

namespace DataMigration.OpsCli.Runners;

public class ReportMeterInfoRunOptions
{
    public string ProfileName { get; set; } = string.Empty;
    public string[] Accounts { get; set; } = [];
    public string[] MeteringPoints { get; set; } = [];
    public bool Scan { get; set; }
    public string DataAreaId { get; set; } = "hol";
    public DateTime? ReferenceDate { get; set; }
    public bool IncludeChildren { get; set; }
    /// <summary>Include MPs no longer in delivery in <c>--scan</c>. Default false (see
    /// <c>ReportMeteringPointsRunOptions.IncludeNotInDelivery</c>).</summary>
    public bool IncludeNotInDelivery { get; set; }
    public bool MatchTypeOnly { get; set; }
    public bool ShowAll { get; set; }
    /// <summary>Target Settl environment ("production" padder til 6 cifre; alle andre er no-op). See <see cref="CustomerNumberPolicy"/>.</summary>
    public string? Env { get; set; }
}

public class ReportMeterInfoRunner
{
    private const string DeliveryCategory = "El-ekstern";

    private static readonly string[] TsvHeader =
    [
        "Identification", "Status", "Relation", "Årsager", "ReferenceDato",
        "Settl_MeteringPointType", "Settl_MeteringPointSubType",
        "Xellent_MeteringPointType", "Xellent_MeteringPointSubType",
        "Settl_MeterReadingOccurrence", "Xellent_MeterReadingOccurrence",
        "Settl_EstimatedAnnualVolume", "Xellent_EstimatedAnnualVolume",
        "Settl_Meters", "Xellent_Meters"
    ];

    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly IXellentDomainLoader _loader;

    public ReportMeterInfoRunner(
        ISettlCustomerClient customerClient,
        ISettlMeteringPointClient meteringPointClient,
        IXellentDomainLoader loader)
    {
        _customerClient = customerClient;
        _meteringPointClient = meteringPointClient;
        _loader = loader;
        _meteringPointClient.SetDeliveryCategory(DeliveryCategory);
    }

    public async Task<int> RunAsync(ReportMeterInfoRunOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (options.Scan)
            return await RunScanAsync(options, output, cancellationToken);

        Console.Error.WriteLine($"=== report-meter-info: {options.ProfileName} ===");
        Console.Error.WriteLine($"Accounts: {options.Accounts.Length}");
        Console.Error.WriteLine();

        var resolution = await _loader.ResolveRepresentativesAsync(options.Accounts, options.DataAreaId, cancellationToken);

        if (resolution.MergedCount > 0)
            Console.Error.WriteLine($"  Accounts merged: {resolution.MergedCount} (non-representative group members)");

        await output.WriteLineAsync(string.Join(";", TsvHeader.Select(CsvQuote)));

        var failed = 0;
        foreach (var accountNum in resolution.Accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var settlCustomerNumber = CustomerNumberPolicy.PadForEnv(accountNum, options.Env, out var wasPadded);
            if (wasPadded)
                Console.Error.WriteLine($"  WARNING: CustomerNumber {accountNum} padded to {settlCustomerNumber} for env");

            var (customer, custErr) = await _customerClient.SearchByCustomerNumberAsync(settlCustomerNumber, cancellationToken: cancellationToken);
            if (customer == null)
            {
                Console.Error.WriteLine($"  Customer {accountNum}: not found in Settl ({custErr})");
                continue;
            }

            var (mps, mpErr) = await _customerClient.GetMeteringPointsAsync(customer.Id, cancellationToken);
            if (mps == null)
            {
                Console.Error.WriteLine($"  Customer {accountNum}: failed to list MPs: {mpErr}");
                failed++;
                continue;
            }

            foreach (var custMp in mps)
                if (!await ProcessMeteringPointAsync(custMp.MeteringPointId, options, output, cancellationToken))
                    failed++;
        }

        Console.Error.WriteLine();
        return failed > 0 ? 1 : 0;
    }

    private async Task<int> RunScanAsync(ReportMeterInfoRunOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        List<Guid> mpIds;

        if (options.MeteringPoints.Length > 0)
        {
            Console.Error.WriteLine("=== report-meter-info: SCAN (metering point filter) ===");
            Console.Error.WriteLine($"Målepunkter: {options.MeteringPoints.Length}");
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
            Console.Error.WriteLine("=== report-meter-info: SCAN (all metering points in delivery) ===");
            Console.Error.WriteLine();

            Console.Error.Write("  Loading metering points...");
            var selection = await SettlMeteringPointScanner.SelectAsync(
                _meteringPointClient, options.IncludeChildren, options.IncludeNotInDelivery,
                Console.Error.Write, cancellationToken);
            Console.Error.WriteLine($"\r  Scanned {selection.Scanned} metering points → {selection.MeteringPointIds.Count} selected              ");
            if (selection.SkippedChildren > 0)
                Console.Error.WriteLine($"    skipped {selection.SkippedChildren} child-MP(s) (Settl relations)");
            if (selection.SkippedNotInDelivery > 0)
                Console.Error.WriteLine($"    skipped {selection.SkippedNotInDelivery} MP(s) not in delivery (use --include-not-in-delivery to include)");
            Console.Error.WriteLine();

            mpIds = selection.MeteringPointIds;
        }

        await output.WriteLineAsync(string.Join(";", TsvHeader.Select(CsvQuote)));

        var failed = 0;
        foreach (var id in mpIds)
            if (!await ProcessMeteringPointAsync(id, options, output, cancellationToken))
                failed++;

        Console.Error.WriteLine();
        return failed > 0 ? 1 : 0;
    }

    private async Task<bool> ProcessMeteringPointAsync(
        Guid meteringPointId,
        ReportMeterInfoRunOptions options,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (currentMp, getErr) = await _meteringPointClient.GetByIdAsync(meteringPointId, cancellationToken: cancellationToken);
        if (currentMp == null)
        {
            Console.Error.WriteLine($"  MP {meteringPointId}: GET fejlede: {getErr}");
            return false;
        }

        var referenceDate = options.ReferenceDate ?? currentMp.DeliveryStatus?.Start;

        if (referenceDate.HasValue)
        {
            (currentMp, getErr) = await _meteringPointClient.GetByIdAsync(meteringPointId, referenceDate.Value, cancellationToken);
            if (currentMp == null)
            {
                Console.Error.WriteLine($"  MP {meteringPointId}: GET (reference date {referenceDate.Value:yyyy-MM-dd}) fejlede: {getErr}");
                return false;
            }
        }

        var identification = currentMp.Identification?.MeteringPointIdentification;
        if (string.IsNullOrEmpty(identification))
        {
            Console.Error.WriteLine($"  MP {meteringPointId}: no identification — skipping");
            return true;
        }

        var hasParent = currentMp.Relation?.ParentMeteringPointId is { } p && p != Guid.Empty;
        var relation = hasParent ? "Child" : "Parent";
        var referenceDateStr = referenceDate.HasValue ? referenceDate.Value.ToString("yyyy-MM-dd") : "";

        if (hasParent && !options.IncludeChildren)
        {
            EmitCsvRow(writer, identification, "Error", relation, "Child MP (brug --include-children)", referenceDateStr, null, null, null, null, null, null, null, null, null, null);
            return true;
        }

        var xellent = await _loader.LoadMeteringPointDataAsync(identification, options.DataAreaId, cancellationToken);

        if (xellent == null)
        {
            EmitCsvRow(writer, identification, "Error", relation, "Ikke fundet i Xellent", referenceDateStr, null, null, null, null, null, null, null, null, null, null);
            return true;
        }

        if (xellent.MeterReadingOccurrence == null)
        {
            EmitCsvRow(writer, identification, "Error", relation, "SeriesResolution mangler", referenceDateStr, null, null, null, null, null, null, null, null, null, null);
            return true;
        }

        var xellentType = xellent.MeteringPointType;
        var xellentSubType = xellent.MeteringPointSubType;

        if (xellentType == null || xellentSubType == null)
        {
            EmitCsvRow(writer, identification, "Error", relation, "Type/SubType mangler i Xellent", referenceDateStr, null, null, null, null, null, null, null, null, null, null);
            return true;
        }

        var xellentOccurrence = xellent.MeterReadingOccurrence;
        var expectedKwh = xellent.EstimatedAnnualVolumeKwh;

        var currentEav = currentMp.MeterCharacteristics?.EstimatedAnnualVolume;
        var settleHasEav = currentEav is { IsEmpty: false };
        var settleMeters = currentMp.MeterCharacteristics?.Meters ?? [];

        var typeOk = currentMp.MeterCharacteristics?.MeteringPointType == xellentType;
        var subTypeOk = currentMp.MeterCharacteristics?.MeteringPointSubType == xellentSubType;
        var settleMeterMap = settleMeters.ToDictionary(m => m.MeterIdentifier ?? "", m => m.MultiplicationFactor);
        var xellentMeterMap = xellent.Meters.ToDictionary(m => m.Id ?? "", m => m.MultiplicationFactor);
        var meterIdsOk = new HashSet<string?>(xellentMeterMap.Keys).SetEquals(settleMeterMap.Keys);
        var multiplicationFactorOk = meterIdsOk && xellentMeterMap.All(kv =>
            settleMeterMap.TryGetValue(kv.Key, out var sf) && sf.HasValue && Math.Abs(sf.Value - kv.Value) < 1e-9);
        var occurrenceOk = settleMeters.All(m => m.MeterReadingOccurrence == xellentOccurrence);
        var eavOk = settleHasEav || expectedKwh == null;

        var alreadyCorrect = options.MatchTypeOnly
            ? typeOk && subTypeOk && occurrenceOk
            : eavOk && meterIdsOk && multiplicationFactorOk && typeOk && subTypeOk && occurrenceOk;

        if (alreadyCorrect && !options.ShowAll)
            return true;

        var reasons = new List<string>();
        if (!typeOk) reasons.Add("Type");
        if (!subTypeOk) reasons.Add("SubType");
        if (!occurrenceOk) reasons.Add("Occurrence");
        if (!options.MatchTypeOnly)
        {
            if (!eavOk) reasons.Add("EAV");
            if (!meterIdsOk) reasons.Add("MeterIds");
            else if (!multiplicationFactorOk) reasons.Add("MultiplicationFactor");
        }

        var status = alreadyCorrect ? "AlreadyCorrect" : "NeedsUpdate";
        var årsager = string.Join(",", reasons);

        var settleEav = settleHasEav ? currentEav!.Value.ToString("F0") : "";
        var xellentEav = expectedKwh.HasValue ? expectedKwh.Value.ToString("F0") : "";
        var settleType = currentMp.MeterCharacteristics?.MeteringPointType ?? "";
        var settleSubType = currentMp.MeterCharacteristics?.MeteringPointSubType ?? "";
        var settleMetersStr = FormatMeters(settleMeters.Select(m => (m.MeterIdentifier, m.MultiplicationFactor ?? 0.0)));
        var xellentMetersStr = FormatMeters(xellent.Meters.Select(m => (m.Id, m.MultiplicationFactor)));
        var settleOccurrence = settleMeters.FirstOrDefault()?.MeterReadingOccurrence ?? "";

        EmitCsvRow(writer, identification, status, relation, årsager, referenceDateStr,
            settleType, settleSubType,
            xellentType, xellentSubType,
            settleOccurrence, xellentOccurrence,
            settleEav, xellentEav,
            settleMetersStr, xellentMetersStr);

        return true;
    }

    private static string FormatMeters(IEnumerable<(string? id, double factor)> meters) =>
        string.Join(", ", meters.Select(m => $"{m.id}×{m.factor}"));

    private static string CsvQuote(string? value)
    {
        var s = value ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static void EmitCsvRow(
        TextWriter writer,
        string identification, string status, string relation, string årsager, string? referenceDato,
        string? settleType, string? settleSubType,
        string? xellentType, string? xellentSubType,
        string? settleOccurrence, string? xellentOccurrence,
        string? settleEav, string? xellentEav,
        string? settleMeters, string? xellentMeters)
    {
        writer.WriteLine(string.Join(";",
            CsvQuote(identification), CsvQuote(status), CsvQuote(relation), CsvQuote(årsager), CsvQuote(referenceDato),
            CsvQuote(settleType), CsvQuote(settleSubType),
            CsvQuote(xellentType), CsvQuote(xellentSubType),
            CsvQuote(settleOccurrence), CsvQuote(xellentOccurrence),
            CsvQuote(settleEav), CsvQuote(xellentEav),
            CsvQuote(settleMeters), CsvQuote(xellentMeters)));
    }
}
