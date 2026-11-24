using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using DataMigration.SettlApi.Models;
using DataMigration.Migration.Configuration;

namespace DataMigration.OpsCli.Services;

internal sealed record MigrationContractWindow(string ContractName, string Product, DateOnly Start, DateOnly End, bool IsJoined);
internal sealed record MigrationObservation(string Report, string RunId, DateTimeOffset WrittenAt,
    string[] Accounts, List<MigrationContractWindow> Contracts, Dictionary<string, JsonElement> Evidence, string[] Findings)
{ public List<MigrationRelationship> Relationships { get; init; } = []; }
internal sealed record MigrationRelationship(string Account, string CustomerNumber, string PayerNumber,
    DateOnly CustomerStart, DateOnly PayerStart, DateOnly LinkStart);
internal sealed record MigrationPointModel(string MeteringPoint, List<MigrationObservation> Observations);
internal sealed record MigrationRunSummary(string Report, string RunId, bool? Success, Dictionary<string, long> ActionCounts)
{ public string? DetailsFile { get; init; } public string? DetailsSha256 { get; init; } }
internal sealed record MigrationSourceFile(string Name, string Sha256, long Bytes, bool Included, string Reason);
internal sealed record MigrationModelManifest(int Version, bool Complete, DateTimeOffset BuiltAt,
    string ModelSha256, int MeteringPoints, List<MigrationSourceFile> Sources);
internal sealed record MigrationModel(int Version, DateTimeOffset BuiltAt,
    Dictionary<string, MigrationPointModel> MeteringPoints, List<MigrationRunSummary> Runs);
internal sealed record LoadedMigrationModel(MigrationModel Model, string Sha256, string? Directory = null);

/// <summary>Read each original report once. Subsequent consumers need only the model and manifest.</summary>
internal static class MigrationRunModel
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] PointFields = ["MpContractNames", "MpProductNums", "MpPaymentTerms",
        "MpContractWindows", "MpContractRules", "MpChargerContractRules", "EvChargersByMeteringPoint"];

    internal static async Task BuildAsync(string reports, string output, CancellationToken ct, Action<string>? progress = null)
    {
        var files = Directory.GetFiles(reports, "*.report.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No migration reports found.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Model output directory must be empty. Existing models are never overwritten.");
        var points = new Dictionary<string, MigrationPointModel>(StringComparer.Ordinal);
        var runs = new List<MigrationRunSummary>();
        var sources = new List<MigrationSourceFile>();
        Directory.CreateDirectory(Path.Combine(output, "run-details"));
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            progress?.Invoke($"Reading report {sources.Count + 1}/{files.Length}: {name}");
            var before = new FileInfo(file);
            var length = before.Length; var modified = before.LastWriteTimeUtc;
            using var hash = SHA256.Create();
            await using var stream = File.OpenRead(file);
            await using var hashing = new CryptoStream(stream, hash, CryptoStreamMode.Read);
            using var document = await JsonDocument.ParseAsync(hashing, cancellationToken: ct);
            // Finish hashing trailing whitespace without reopening the original file.
            await hashing.CopyToAsync(Stream.Null, ct);
            var checksum = Convert.ToHexString(hash.Hash!);
            var after = new FileInfo(file);
            if (after.Length != length || after.LastWriteTimeUtc != modified)
                throw new IOException($"Migration report changed while reading: {name}");
            var root = document.RootElement;
            var report = Get(root, "Report"); var result = Get(root, "Result");
            var partial = Bool(root, "Partial"); var dry = Bool(result, "IsDryRun"); var reportDry = Bool(report, "DryRun");
            var reason = partial == true ? "Unfinished report" : dry == true || reportDry == true ? "Dry run"
                : partial is null || dry is null || reportDry is null ? "Completion flags missing"
                : dry != reportDry ? "Contradictory completion flags" : "";
            var included = reason.Length == 0;
            sources.Add(new(name, checksum, length, included, reason));
            if (!included) continue;
            if (report.ValueKind != JsonValueKind.Object || result.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Completed report has no report/result section: {name}");
            var runId = Text(root, "RunId") ?? throw new InvalidOperationException($"Completed report lacks run ID: {name}");
            if (!DateTimeOffset.TryParse(Text(root, "WrittenAt"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var written))
                throw new InvalidOperationException($"Completed report lacks write date: {name}");
            var counts = result.EnumerateObject().Where(p => p.Name.EndsWith("Created", StringComparison.Ordinal)
                || p.Name.EndsWith("Failed", StringComparison.Ordinal) || p.Name.EndsWith("Linked", StringComparison.Ordinal)
                || p.Name.EndsWith("Existing", StringComparison.Ordinal)).Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetInt64());
            // Retain the entire completed report as model data. Later consumers can use
            // fields not needed by today's repair without rescanning the original folder.
            var detailsFile = "run-details/" + name + ".gz";
            await using (var target = File.Create(Path.Combine(output, detailsFile) + ".pending"))
            await using (var compressed = new GZipStream(target, CompressionLevel.Fastest))
            {
                await using var writer = new Utf8JsonWriter(compressed);
                root.WriteTo(writer); await writer.FlushAsync(ct);
            }
            runs.Add(new(name, runId, Bool(result, "Success"), counts)
                { DetailsFile = detailsFile, DetailsSha256 = await HashFileAsync(Path.Combine(output, detailsFile) + ".pending", ct) });
            var accounts = new Dictionary<string, HashSet<string>>();
            AddAccounts(Get(report, "AccountMeteringPoints"));
            AddAccounts(Get(result, "AccountMeteringPoints"));
            var mpResults = new Dictionary<string, List<JsonElement>>();
            var relationships = new Dictionary<string, List<MigrationRelationship>>();
            var customers = Get(result, "Customers");
            if (customers.ValueKind == JsonValueKind.Array)
                foreach (var customer in customers.EnumerateArray())
                {
                    var account = Text(customer, "AccountNum");
                    var mps = Get(customer, "MeteringPoints");
                    if (mps.ValueKind != JsonValueKind.Array) continue;
                    foreach (var mp in mps.EnumerateArray())
                    {
                        var id = Text(mp, "MeteringPointId");
                        if (!IsMp(id)) continue;
                        if (!accounts.TryGetValue(id!, out var owners)) accounts[id!] = owners = [];
                        if (account is not null) owners.Add(account);
                        if (!mpResults.TryGetValue(id!, out var values)) mpResults[id!] = values = [];
                        values.Add(mp);
                        var rawAccounts = CanonicalAccounts(owners);
                        var original = rawAccounts.SingleOrDefault(a => a == account || CustomerPayerRepair.PadOriginalAccount(a) == account);
                        var number = original is null ? null : CustomerPayerRepair.PadOriginalAccount(original);
                        var customerPayers = Get(customer, "Payers");
                        var payerNumbers = customerPayers.ValueKind == JsonValueKind.Array
                            ? customerPayers.EnumerateArray().Select(p => Text(p, "PayerIdentifier")).Where(p => p is not null).Distinct().ToArray() : [];
                        if (original is not null && (payerNumbers.Contains(number) || payerNumbers.Contains(original))
                            && TryDate(Text(customer, "AddressStart"), out var customerStart)
                            && TryDate(Text(mp, "IdentificationStart"), out var linkStart))
                        {
                            if (!relationships.TryGetValue(id!, out var relationRows)) relationships[id!] = relationRows = [];
                            // Both customer and payer version starts in this migration use
                            // CustomerTranslator.EarliestStart, displayed as AddressStart.
                            relationRows.Add(new(original, number!, number!, customerStart, customerStart, linkStart));
                        }
                    }
                }
            var ids = accounts.Keys.Concat(PointFields.SelectMany(field => Keys(Get(report, field))))
                .Concat(Keys(Get(result, "MpContractNames"))).Where(IsMp).Distinct().Order(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var evidence = new Dictionary<string, JsonElement>();
                foreach (var field in PointFields)
                {
                    var value = Get(Get(report, field), id);
                    if (value.ValueKind != JsonValueKind.Undefined) evidence[field] = value.Clone();
                }
                var windows = new List<MigrationContractWindow>(); var findings = new List<string>();
                var raw = Get(Get(report, "MpContractWindows"), id);
                if (raw.ValueKind == JsonValueKind.Array) ReadWindows(raw);
                // Older reports carry the same windows under the extracted MP.
                else if (mpResults.TryGetValue(id, out var results))
                    foreach (var mp in results) ReadWindows(Get(mp, "Contracts"));
                if (windows.Count == 0) findings.Add("Contract periods missing from migration report.");
                if (mpResults.TryGetValue(id, out var pointResults))
                    evidence["ExtractedContractResults"] = JsonSerializer.SerializeToElement(pointResults.Select(p => new
                    {
                        ContractName = Text(p, "ContractName"), Product = Text(p, "ProductNum"),
                        IsJoined = Bool(p, "IsJoined"), ExistsInSettl = Bool(p, "ExistsInSettl"),
                        Contracts = Get(p, "Contracts").ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : Get(p, "Contracts")
                    }));
                var owners = CanonicalAccounts(accounts.TryGetValue(id, out var ownerSet) ? ownerSet : []);
                if (owners.Length != 1) findings.Add("Original account missing or ambiguous in migration report.");
                var relations = relationships.GetValueOrDefault(id, []).Distinct().ToList();
                if (relations.Count != 1) findings.Add("Customer/payer relationship dates missing or ambiguous in migration report.");
                if (!points.TryGetValue(id, out var point)) points[id] = point = new(id, []);
                point.Observations.Add(new(name, runId, written, owners,
                    windows.Distinct().OrderBy(w => w.ContractName, StringComparer.Ordinal).ThenBy(w => w.Start).ThenBy(w => w.End).ThenBy(w => w.Product).ToList(), evidence, findings.Distinct().ToArray())
                    { Relationships = relations });

                void ReadWindows(JsonElement array)
                {
                    if (array.ValueKind != JsonValueKind.Array) return;
                    foreach (var row in array.EnumerateArray())
                    {
                        var contract = Text(row, "ContractName"); var product = Text(row, "ProductNum");
                        if (string.IsNullOrWhiteSpace(contract) || string.IsNullOrWhiteSpace(product)
                            || !TryDate(Text(row, "Start"), out var start))
                        { findings.Add("Contract name, product or start missing from migration report."); continue; }
                        var end = DateOnly.MaxValue; var endText = Text(row, "End");
                        if (endText is not null && !TryDate(endText, out end))
                        { findings.Add("Invalid contract end in migration report."); continue; }
                        if (end.Year <= 1900) end = DateOnly.MaxValue;
                        if (end < start) { findings.Add("Invalid contract period in migration report."); continue; }
                        windows.Add(new(contract, product, start, end, Bool(row, "IsJoined") ?? false));
                    }
                }
            }
            void AddAccounts(JsonElement mapping)
            {
                if (mapping.ValueKind != JsonValueKind.Object) return;
                foreach (var property in mapping.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var value in property.Value.EnumerateArray())
                    {
                        var id = value.ValueKind == JsonValueKind.String ? value.GetString() : Text(value, "Item1") ?? Text(value, "MeteringPointId");
                        if (!IsMp(id)) continue;
                        if (!accounts.TryGetValue(id!, out var owners)) accounts[id!] = owners = [];
                        owners.Add(property.Name);
                    }
                }
            }
        }
        if (runs.Count == 0) throw new InvalidOperationException("No completed non-dry-run reports found.");
        var built = DateTimeOffset.UtcNow;
        var model = new MigrationModel(2, built, points.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value), runs);
        Directory.CreateDirectory(output);
        var modelFile = Path.Combine(output, "migration-model.json"); var manifestFile = Path.Combine(output, "manifest.json");
        try
        {
            await using (var target = File.Create(modelFile + ".pending")) await JsonSerializer.SerializeAsync(target, model, Json, ct);
            var modelHash = await HashFileAsync(modelFile + ".pending", ct);
            var manifest = new MigrationModelManifest(2, true, built, modelHash, points.Count, sources);
            await File.WriteAllTextAsync(manifestFile + ".pending", JsonSerializer.Serialize(manifest, Json), ct);
            File.Move(modelFile + ".pending", modelFile);
            foreach (var run in runs) File.Move(Path.Combine(output, run.DetailsFile!) + ".pending", Path.Combine(output, run.DetailsFile!));
            File.Move(manifestFile + ".pending", manifestFile); // Manifest is the completion marker.
        }
        catch
        {
            foreach (var path in new[] { modelFile, manifestFile, modelFile + ".pending", manifestFile + ".pending" }) File.Delete(path);
            throw;
        }
        progress?.Invoke($"Model complete: {runs.Count} included reports, {sources.Count - runs.Count} excluded, {points.Count} MPs.");
    }

    internal static async Task<LoadedMigrationModel> LoadAsync(string file, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize<MigrationModelManifest>(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "manifest.json"), ct))
            ?? throw new InvalidOperationException("Migration model manifest missing.");
        if (!manifest.Complete || manifest.Version is not (1 or 2) || !string.Equals(manifest.ModelSha256, await HashFileAsync(file, ct), StringComparison.Ordinal))
            throw new InvalidOperationException("Migration model incomplete, unsupported or checksum mismatch.");
        await using var stream = File.OpenRead(file);
        var model = await JsonSerializer.DeserializeAsync<MigrationModel>(stream, cancellationToken: ct)
            ?? throw new InvalidOperationException("Migration model missing.");
        if (model.Version != manifest.Version || model.BuiltAt != manifest.BuiltAt || model.MeteringPoints.Count != manifest.MeteringPoints
            || model.Runs.Count != manifest.Sources.Count(s => s.Included)) throw new InvalidOperationException("Migration model and manifest disagree.");
        // Version 1 also accepts the first model build, which retained both raw and padded
        // account aliases. Normalise that representation in memory without rereading reports.
        model = model with { MeteringPoints = model.MeteringPoints.ToDictionary(p => p.Key, p => p.Value with
        {
            Observations = p.Value.Observations.Select(o =>
            {
                var accounts = CanonicalAccounts(o.Accounts);
                return o with { Accounts = accounts, Findings = accounts.Length == 1
                    ? o.Findings.Where(f => f != "Original account missing or ambiguous in migration report.").ToArray() : o.Findings };
            }).ToList()
        }) };
        return new(model, manifest.ModelSha256, Path.GetDirectoryName(Path.GetFullPath(file)));
    }

    internal static RepairXellentEvidence ApplyForImport(RepairXellentEvidence source, LoadedMigrationModel loaded, IReadOnlyList<string> targets, MappingOptions? mappings = null)
    {
        var selected = new Dictionary<string, MigrationPointModel>();
        foreach (var mp in targets)
            if (loaded.Model.MeteringPoints.TryGetValue(mp, out var point))
                selected[mp] = point with { Observations = [point.Observations.OrderByDescending(o => o.WrittenAt)
                    .ThenByDescending(o => o.Report, StringComparer.Ordinal).First()] };
        var comparison = Apply(source, loaded, targets);
        var selectedModel = loaded with { Model = loaded.Model with { MeteringPoints = selected } };
        var applied = Apply(source with { ContractMappingErrors = [] }, selectedModel, targets);
        var warnings = new Dictionary<string, string[]>();
        var errors = new Dictionary<string, string>();
        foreach (var mp in targets)
        {
            var messages = new List<string>();
            if (comparison.ContractMappingErrors.TryGetValue(mp, out var finding)) messages.Add(finding);
            if (applied.ContractMappingErrors.TryGetValue(mp, out var selectedFinding))
            {
                if (selectedFinding is "MP missing from completed migration reports." or "Migration report information missing or ambiguous.")
                    errors[mp] = selectedFinding;
                else messages.Add(selectedFinding);
            }
            if (comparison.ContractMappingErrors.GetValueOrDefault(mp) == "Completed migration reports disagree.")
                messages.Add("Latest completed migration report selected.");
            warnings[mp] = messages.Distinct().ToArray();
        }
        var expected = applied.ExpectedContracts!.Select(contract =>
        {
            var observation = selected[contract.MeteringPoint].Observations.Single();
            if (contract.ContractName == contract.Product && mappings?.ProductNum?.TryGetValue(contract.Product, out var entry) == true
                && entry.Resolve(TextValue(observation.Evidence.GetValueOrDefault("MpPaymentTerms"))) is { } resolved && resolved != contract.ContractName)
                return contract with { ContractName = resolved, MappingNote = $"Bekræftet produktregel: {contract.ContractName} → {resolved}." };
            var chargerProducts = observation.Evidence.GetValueOrDefault("EvChargersByMeteringPoint");
            if (chargerProducts.ValueKind != JsonValueKind.Array) return contract;
            var products = chargerProducts.EnumerateArray().SelectMany(charger => Get(charger, "Parts").ValueKind == JsonValueKind.Array
                    ? Get(charger, "Parts").EnumerateArray().Select(part => Text(part, "ProductCode")).ToArray() : [])
                .Where(product => product is not null && ElectricityChargerContractRules.ResolveConfirmedHomeCharge(contract.ContractName, product) is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (products.Length == 0) return contract;
            var target = ElectricityChargerContractRules.ResolveConfirmedHomeCharge(contract.ContractName, products[0]!)!;
            if (contract.ContractName.Equals("V LadVarUA", StringComparison.OrdinalIgnoreCase))
                warnings[contract.MeteringPoint] = warnings[contract.MeteringPoint].Append(
                    $"Manual UA follow-up after import: {contract.Start:dd-MM-yyyy} – {(contract.End == DateOnly.MaxValue ? "Åben" : contract.End.ToString("dd-MM-yyyy"))}.").ToArray();
            return contract with { ContractName = target,
                MappingNote = $"Bekræftet laderregel: {string.Join(" | ", products)} + {contract.ContractName} → {target}." };
        }).ToList();
        return applied with { ExpectedContracts = expected, IncludeAllMigrationRecords = true, MigrationWarnings = warnings, ContractMappingErrors = errors };
        static string? TextValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    internal static RepairXellentEvidence Apply(RepairXellentEvidence source, LoadedMigrationModel loaded, IReadOnlyList<string> targets)
    {
        var expected = new List<RepairExpectedContract>();
        var errors = new Dictionary<string, string>(source.ContractMappingErrors);
        var reports = new Dictionary<string, string[]>();
        var relationships = new Dictionary<string, MigrationRelationship>();
        var migrationAccounts = new Dictionary<string, string[]>();
        if (loaded.Model.Version < 2) throw new InvalidOperationException("Migration model lacks relationship dates. Rebuild with version 2.");
        foreach (var mp in targets)
        {
            if (!loaded.Model.MeteringPoints.TryGetValue(mp, out var point)) { errors[mp] = "MP missing from completed migration reports."; continue; }
            reports[mp] = point.Observations.Select(o => o.Report).Distinct().Order(StringComparer.Ordinal).ToArray();
            migrationAccounts[mp] = point.Observations.SelectMany(o => o.Accounts).Distinct().Order(StringComparer.Ordinal).ToArray();
            // Preserve report proposals in evidence even when comparison holds the MP.
            foreach (var group in point.Observations.SelectMany(o => o.Contracts.Select(w => (Window: w, o.Report))).GroupBy(p => p.Window))
                expected.Add(new(mp, group.Key.ContractName, group.Key.Start, group.Key.End, group.Key.Product,
                    "Migration reports: " + string.Join(" | ", group.Select(p => p.Report).Distinct().Order(StringComparer.Ordinal))));
            if (point.Observations.Any(o => o.Findings.Length > 0)) { errors[mp] = "Migration report information missing or ambiguous."; continue; }
            var groups = point.Observations.GroupBy(o => JsonSerializer.Serialize(new { o.Accounts, o.Contracts, o.Relationships })).ToArray();
            if (groups.Length != 1) { errors[mp] = "Completed migration reports disagree."; continue; }
            var observation = groups[0].First();
            relationships[mp] = observation.Relationships.Single();
            var localAccounts = source.Routes.Where(r => r.MeteringPoint == mp).Select(r => r.SourceAccount).Distinct().ToArray();
            if (!localAccounts.Contains(observation.Accounts.Single(), StringComparer.Ordinal))
            { errors[mp] = "Migration report and Xellent account disagree."; continue; }
            var local = (source.ExpectedContracts ?? []).Where(c => c.MeteringPoint == mp)
                .Select(c => new MigrationContractWindow(c.ContractName, c.Product, c.Start, c.End.Year <= 1900 ? DateOnly.MaxValue : c.End, false)).ToArray();
            // IsJoined remains in the model; it is not an import-column comparison.
            if (local.Length == 0 || !observation.Contracts.Select(Key).ToHashSet().SetEquals(local.Select(Key)))
            { errors.TryAdd(mp, "Migration report and Xellent contracts or dates disagree."); continue; }
        }
        return source with { ExpectedContracts = expected, ContractMappingErrors = errors,
            MigrationReports = reports, MigrationModelVersion = loaded.Model.Version, MigrationModelSha256 = loaded.Sha256,
            MigrationRelationships = relationships, MigrationAccounts = migrationAccounts };
        static object Key(MigrationContractWindow w) => (w.ContractName, w.Product, w.Start, w.End);
    }

    internal static async Task<RepairXellentEvidence> ApplyDetailsAsync(RepairXellentEvidence source, LoadedMigrationModel loaded,
        IReadOnlyList<string> targets, CancellationToken ct)
    {
        var candidates = new Dictionary<string, List<RepairSourceCustomer>>();
        var confirmedNoCharger = new HashSet<string>();
        var errors = new Dictionary<string, string>(source.ContractMappingErrors);
        var needed = targets.Where(source.MigrationRelationships.ContainsKey).SelectMany(mp => source.MigrationReports[mp]).ToHashSet();
        foreach (var run in loaded.Model.Runs.Where(r => needed.Contains(r.Report)).OrderByDescending(r =>
            loaded.Model.MeteringPoints.Values.SelectMany(p => p.Observations).Where(o => o.Report == r.Report).Max(o => o.WrittenAt))
            .ThenByDescending(r => r.Report, StringComparer.Ordinal))
        {
            if (loaded.Directory is null || run.DetailsFile is null || run.DetailsSha256 is null)
                throw new InvalidOperationException("Migration model lacks archived report details.");
            var rootDirectory = Path.GetFullPath(loaded.Directory) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(rootDirectory, run.DetailsFile));
            if (!file.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase)
                || await HashFileAsync(file, ct) != run.DetailsSha256) throw new InvalidOperationException("Archived migration report checksum mismatch.");
            await using var stream = File.OpenRead(file);
            await using var compressed = new GZipStream(stream, CompressionMode.Decompress);
            using var document = await JsonDocument.ParseAsync(compressed, cancellationToken: ct);
            var customers = Get(Get(document.RootElement, "Result"), "Customers");
            if (customers.ValueKind != JsonValueKind.Array) continue;
            foreach (var mp in targets.Where(mp => source.MigrationRelationships.ContainsKey(mp) && source.MigrationReports[mp].Contains(run.Report)))
            {
                var relation = source.MigrationRelationships[mp];
                var report = Get(document.RootElement, "Report");
                var byAccount = Get(report, "EvChargersByAccount");
                var byPoint = Get(report, "EvChargersByMeteringPoint");
                if (byAccount.ValueKind == JsonValueKind.Object && byPoint.ValueKind == JsonValueKind.Object
                    && EmptyChargerList(Get(byAccount, relation.Account)) && EmptyChargerList(Get(byAccount, relation.CustomerNumber))
                    && EmptyChargerList(Get(byPoint, mp))) confirmedNoCharger.Add(mp);
                var records = customers.EnumerateArray().Where(c => Text(c, "AccountNum") == relation.Account || Text(c, "AccountNum") == relation.CustomerNumber).ToArray();
                if (records.Length != 1) { errors[mp] = "Migration customer/payer record missing or ambiguous."; continue; }
                var raw = records[0];
                var payers = Get(raw, "Payers");
                var payerRecords = payers.ValueKind == JsonValueKind.Array ? payers.EnumerateArray()
                    .Where(p => Text(p, "PayerIdentifier") == relation.Account || Text(p, "PayerIdentifier") == relation.PayerNumber).ToArray() : [];
                if (payerRecords.Length != 1) { errors[mp] = "Migration customer/payer record missing or ambiguous."; continue; }
                var prior = source.Customers.SingleOrDefault(c => c.SourceAccount == relation.Account);
                var customerId = prior?.Customer.Id ?? Guid.NewGuid();
                var customer = new CreateCustomerDto
                {
                    Id = customerId, CustomerNumber = relation.CustomerNumber,
                    CountryCode = Text(raw, "CountryCode") ?? "", CustomerType = Text(raw, "CustomerType") ?? "",
                    VatIdentificationNumber = Text(raw, "Cvr"), NationalIdentificationNumber = Text(raw, "Cpr"),
                    Address = new() { Start = relation.CustomerStart, PostalAddress = Postal(raw, "", "Name", "SupplementaryName"),
                        Phone = Text(raw, "Phone"), Cellphone = Text(raw, "Cellphone"), Fax = Text(raw, "Fax"), Email = Text(raw, "Email") }
                };
                var payerRaw = payerRecords[0];
                var payer = new CreatePayerDto
                {
                    Id = prior?.Payers.SingleOrDefault(p => p.PayerInformation?.PayerIdentifier == relation.PayerNumber)?.Id ?? Guid.NewGuid(),
                    CustomerId = customerId, CountryCode = customer.CountryCode,
                    PayerInformation = new() { Start = relation.PayerStart, PayerIdentifier = relation.PayerNumber,
                        PostalAddress = Postal(payerRaw, "Payer", "PayerName", "PayerSupplementaryName", customer.CountryCode) },
                    PayerBilling = new() { Start = relation.PayerStart, Email = Text(payerRaw, "Email"),
                        InvoiceChannel = Text(payerRaw, "InvoiceChannel") ?? "", PaymentChannel = Text(payerRaw, "PaymentChannel") ?? "",
                        PayerType = Text(payerRaw, "PayerType"), NationalIdentificationNumber = Text(payerRaw, "NationalIdentificationNumber"),
                        VatIdentificationNumber = Text(payerRaw, "OrganizationIdentifier") ?? Text(payerRaw, "VatIdentificationNumber"), ElectronicNumber = Text(payerRaw, "ElectronicNumber"),
                        BuyerReferenceIdentifier = Text(payerRaw, "BuyerReferenceIdentifier"), AgreementIdentifier = Text(payerRaw, "AgreementIdentifier"),
                        OrderIdentifier = Text(payerRaw, "OrderIdentifier") }
                };
                if (!candidates.TryGetValue(mp, out var list)) candidates[mp] = list = [];
                list.Add(new(customer, [payer], relation.Account));
            }
        }
        var chosen = new Dictionary<string, RepairSourceCustomer>();
        foreach (var mp in source.MigrationRelationships.Keys)
        {
            if (!candidates.TryGetValue(mp, out var rows) || rows.Count == 0) { errors[mp] = "Migration customer/payer record missing or ambiguous."; continue; }
            var signatures = rows.GroupBy(ContentKey).ToArray();
            if (signatures.Length != 1 && !source.IncludeAllMigrationRecords) { errors[mp] = "Completed migration customer/payer records disagree."; continue; }
            chosen[mp] = rows[0];
        }
        // Shared accounts cannot receive different import values depending on the MP.
        foreach (var account in chosen.GroupBy(p => p.Value.SourceAccount))
            if (account.Select(p => ContentKey(p.Value)).Distinct().Count() > 1)
                foreach (var item in account)
                    if (source.IncludeAllMigrationRecords)
                        source.MigrationWarnings[item.Key] = source.MigrationWarnings.GetValueOrDefault(item.Key, [])
                            .Append("Completed migration customer/payer records disagree.").Append("Latest completed migration report selected.").Distinct().ToArray();
                    else errors[item.Key] = "Completed migration customer/payer records disagree.";
        var preparedCustomers = chosen.OrderByDescending(p => loaded.Model.MeteringPoints[p.Key].Observations
                .Where(o => source.MigrationReports[p.Key].Contains(o.Report)).Max(o => o.WrittenAt))
            .ThenByDescending(p => source.MigrationReports[p.Key].Max(), StringComparer.Ordinal)
            .Select(p => p.Value).DistinctBy(c => c.SourceAccount).ToList();
        var routes = source.MigrationRelationships.Select(p => new RepairRoute(p.Key, p.Value.CustomerNumber, p.Value.PayerNumber,
            string.Join(" | ", source.Routes.Where(r => r.MeteringPoint == p.Key).Select(r => r.ContractNumber).Distinct()),
            p.Value.LinkStart, DateOnly.MaxValue) { SourceAccount = p.Value.Account }).ToList();
        var contracts = source.ExpectedContracts?.Select(c => source.IncludeAllMigrationRecords && confirmedNoCharger.Contains(c.MeteringPoint)
                && c.ContractName == "V LadVariP" ? c with { ContractName = ElectricityChargerContractRules.PendingInstallationContract,
                    MappingNote = "Bekræftet laderregel: Ingen lader i migrationsrapporten → LadeAbo_Afventer_Installation." } : c).ToList();
        if (source.IncludeAllMigrationRecords && contracts is not null) contracts = AborabatRepair.ResolveModelDiscounts(contracts);
        return source with { Customers = preparedCustomers, Routes = routes, ExpectedContracts = contracts, ContractMappingErrors = errors };

        static bool EmptyChargerList(JsonElement value) => value.ValueKind == JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;

        static string ContentKey(RepairSourceCustomer record) => JsonSerializer.Serialize(new
        {
            record.SourceAccount, record.Customer.CustomerNumber, record.Customer.CountryCode, record.Customer.CustomerType,
            record.Customer.VatIdentificationNumber, record.Customer.NationalIdentificationNumber, record.Customer.Address,
            Payers = record.Payers.Select(p => new { p.CountryCode, p.PayerInformation, p.PayerBilling })
        });
    }

    private static PostalAddressDto Postal(JsonElement raw, string prefix, string name, string extraName, string? country = null) => new()
    {
        Name1 = new() { FullName = Text(raw, name) ?? "" }, Name2 = new() { FullName = Text(raw, extraName) ?? "" },
        PhysicalAddress = new()
        {
            StreetCode = Text(raw, prefix + "StreetCode"), StreetName = Text(raw, prefix + "StreetName"),
            BuildingNumber = Text(raw, prefix + "BuildingNumber"), BuildingFloor = Text(raw, prefix + "BuildingFloor"),
            RoomIdentification = Text(raw, prefix + "RoomIdentification"), CitySubDivisionName = Text(raw, prefix + "CitySubDivisionName"),
            PostalCode = Text(raw, prefix + "PostalCode"), City = Text(raw, prefix + "City"),
            MunicipalityCode = Text(raw, prefix + "MunicipalityCode"), CountryCode = country ?? Text(raw, "CountryCode") ?? "",
            AddressIdentifier = Text(raw, prefix + "AddressIdentifier") is { Length: > 0 } id ? new() { Identification = id } : null
        }
    };

    private static JsonElement Get(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement element, string name) => Get(element, name) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Bool(JsonElement element, string name) => Get(element, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static IEnumerable<string> Keys(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().Select(p => p.Name) : [];
    private static bool IsMp(string? value) => value is { Length: 18 } && value.All(char.IsAsciiDigit);
    private static string[] CanonicalAccounts(IEnumerable<string> accounts)
    {
        var set = accounts.ToHashSet(StringComparer.Ordinal);
        return set.Where(a => !(a is { Length: 6 } && a[0] == '9' && set.Contains(a[1..])))
            .Order(StringComparer.Ordinal).ToArray();
    }
    private static bool TryDate(string? value, out DateOnly date) => DateOnly.TryParseExact(value?.Split('T')[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
    private static async Task<string> HashFileAsync(string file, CancellationToken ct)
    { await using var stream = File.OpenRead(file); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
}
