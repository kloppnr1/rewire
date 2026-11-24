using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using DataMigration.XellentData;
using DataMigration.Core;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.SettlClient.Internal;

namespace DataMigration.OpsCli.Services;

internal sealed record RepairRoute(string MeteringPoint, string CustomerNumber, string PayerIdentification,
    string ContractNumber, DateOnly Start, DateOnly End)
{ public string? SourceAccount { get; init; } }
internal sealed record RepairExpectedContract(string MeteringPoint, string ContractName, DateOnly Start, DateOnly End, string Product, string SourceContracts)
{ public string? MappingNote { get; init; } }
internal sealed record RepairSourceCustomer(CreateCustomerDto Customer, List<CreatePayerDto> Payers, string SourceAccount);
internal sealed record RepairXellentEvidence(DateTimeOffset CapturedAt, DateOnly ReferenceDate,
    List<RepairSourceCustomer> Customers, List<RepairRoute> Routes)
{
    public Dictionary<string, string[]> SourceAccountPoints { get; init; } = [];
    public bool OriginalAccountsPreserved { get; init; }
    public List<RepairExpectedContract>? ExpectedContracts { get; init; }
    public Dictionary<string, string> ContractMappingErrors { get; init; } = [];
    public List<XellentChargerAssignmentRow>? ChargerAssignments { get; init; }
    public Dictionary<string, string[]> MigrationReports { get; init; } = [];
    public int? MigrationModelVersion { get; init; }
    public string? MigrationModelSha256 { get; init; }
    public Dictionary<string, string> SourceValueErrors { get; init; } = [];
    public Dictionary<string, MigrationRelationship> MigrationRelationships { get; init; } = [];
    public Dictionary<string, string[]> MigrationAccounts { get; init; } = [];
    public bool IncludeAllMigrationRecords { get; init; }
    public Dictionary<string, string[]> MigrationWarnings { get; init; } = [];
}
internal sealed record RepairCustomerLink(Guid CustomerId, CustomerMeteringPointDto Link);
internal sealed record RepairPayerLink(Guid PayerId, PayerMeteringPointDto Link);
internal sealed record RepairCustomerInventory(CustomerInfoDto Customer, DateOnly? EarliestVersion);
internal sealed record RepairPayerInventory(PayerInfoDto Payer, DateOnly? EarliestVersion)
{
    public Dictionary<DateOnly, Guid> CustomerAtDate { get; init; } = [];
    public Dictionary<DateOnly, RepairPayerEffectiveVersion>? CapturedVersions { get; init; }
}
internal sealed record RepairPayerEffectiveVersion(Guid CustomerId, DateOnly? InformationStart, DateOnly? BillingStart);
internal sealed record RepairSettlEvidence(DateTimeOffset CapturedAt,
    List<SettlMpWithCos> MeteringPoints, List<GenerateStateOfflineCache.DeliveryHistoryRecord> Histories,
    List<RepairCustomerInventory> Customers, List<RepairPayerInventory> Payers,
    List<RepairCustomerLink> CustomerLinks, List<RepairPayerLink> PayerLinks)
{ public List<ContractInfoDto>? ContractCatalog { get; init; } }

internal static class CustomerPayerRepairCapture
{
    private static string FailureCategory(string? error)
    {
        if (error is null) return "empty or incomplete response";
        var match = System.Text.RegularExpressions.Regex.Match(error, @"^HTTP (\d{3})(?::|$)");
        if (match.Success) return "HTTP " + match.Groups[1].Value;
        return error.StartsWith("Failed to deserialize", StringComparison.Ordinal) ? "response deserialization failed" : "API error";
    }

    /// <summary>The SDK returns zero both for an empty inventory and absent CountResult metadata.
    /// Exhaust pages explicitly; a short page may only mean the server capped Limit.</summary>
    internal static async IAsyncEnumerable<List<T>> InventoryPagesAsync<T>(string kind,
        Func<int, int, CancellationToken, Task<(List<T> Items, int TotalCount, string? Error)>> fetch,
        Func<T, Guid> id, Action<string>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var inventoryTimer = new CaptureStage($"Settl {kind} inventory", progress);
        var pageNumber = 0;
        int? knownTotal = null;
        var seen = new HashSet<Guid>();
        for (var skip = 0; ;)
        {
            ct.ThrowIfCancellationRequested();
            var pageTimer = Stopwatch.StartNew();
            pageNumber++;
            progress?.Invoke($"  Capture {kind} inventory page {pageNumber}: fetching offset={skip}, limit=1000.");
            var (page, total, error) = await fetch(1000, skip, ct);
            progress?.Invoke($"  Capture {kind} inventory page {pageNumber}: fetched rows={page?.Count ?? 0}, reportedTotal={total}, fetchSeconds={pageTimer.Elapsed.TotalSeconds:F2}.");
            var detail = $"{kind} inventory: offset={skip}, pageCount={page?.Count ?? 0}, reportedTotal={total}, previousTotal={knownTotal?.ToString() ?? "unknown"}";
            if (error is not null || page is null) throw new InvalidOperationException($"{detail}: lookup failed ({FailureCategory(error)}); capture remains incomplete.");
            if (total < 0 || total > 0 && knownTotal is not null && total != knownTotal)
                throw new InvalidOperationException($"{detail}: reported positive total changed or is invalid; capture remains incomplete.");
            if (total > 0) knownTotal = total;
            var next = checked(skip + page.Count);
            if (knownTotal is { } expected && (next > expected || page.Count == 0 && skip != expected))
                throw new InvalidOperationException($"{detail}: page contents disagree with the reported total; capture remains incomplete.");
            if (page.Any(item => id(item) == Guid.Empty || !seen.Add(id(item))))
                throw new InvalidOperationException($"{detail}: repeated or missing entity identifiers; capture remains incomplete.");
            if (page.Count == 0)
            {
                inventoryTimer.Complete($"entities={skip}, pages={pageNumber}; terminal empty page verified");
                yield break;
            }
            yield return page;
            progress?.Invoke($"  Capture {kind} inventory page {pageNumber}: inspected rows={page.Count}, pageSeconds={pageTimer.Elapsed.TotalSeconds:F2}.");
            skip = next;
            progress?.Invoke($"  Capture {kind} inventory associations inspected: {skip}/{knownTotal?.ToString() ?? "unknown (count metadata absent)"}");
        }
    }

    internal static async Task<RepairXellentEvidence> PrepareXellentAsync(StateSourceSnapshotStore store,
        XellentDbContext db, RepairRawSettings settings, CancellationToken ct)
    {
        var reference = settings.ReferenceDate;
        var dataArea = settings.DataArea;
        var mappings = settings.Mappings;
        using var captureTimer = new CaptureStage("Offline Xellent customer/payer preparation");
        var warnings = new WarningCollector();
        var options = new MapperOptions(reference, Commodity.Electricity,
            DisableRepresentativeMerge: true);
        using var mappingTimer = new CaptureStage("Xellent customer/payer domain mapping");
        var load = await new XellentDomainMapper(db, warnings,
            options with { IgnoredGroupNumbers = settings.IgnoredGroups.ToHashSet() }, dataArea)
            .LoadAsync(store.Scope?.SupportingAccounts, ct);
        mappingTimer.Complete($"mappedCustomers={load.Customers.Count}");
        var sourceErrors = warnings.GetErrors();
        using var evidenceTimer = new CaptureStage("Xellent customer/payer details and source-account lookup");
        var customers = new List<RepairSourceCustomer>();
        var routes = new List<RepairRoute>();
        foreach (var customer in load.Customers)
        {
            var dto = CustomerTranslator.Build(customer);
            dto.CustomerNumber = CustomerNumberPolicy.PadForEnv(customer.AccountNumber, store.Environment, out _);
            var payers = customer.Payers.Select(p => PayerTranslator.Build(customer, p, dto.Id)).ToList();
            foreach (var p in payers) p.PayerInformation!.PayerIdentifier = CustomerNumberPolicy.PadPayerIdentifierForEnv(p.PayerInformation.PayerIdentifier!, customer.AccountNumber, store.Environment, out _);
            var customerRoutes = new List<RepairRoute>();
            foreach (var contract in customer.Contracts.Where(c => c.Commodity == Commodity.Electricity))
            foreach (var point in contract.MeteringPoints.Where(m => store.Scope is null || store.Scope.MeteringPoints.Contains(m.MeteringPointId)))
            {
                var payer = customer.Payers.Where(p => p.ContractNumbers.Contains(contract.Number)).ToArray();
                // The online migration can fall back to primary. A repair must prove the intended payer.
                if (payer.Length != 1) continue; // Missing/ambiguous routes are evaluated during preparation.
                customerRoutes.Add(new(point.MeteringPointId, dto.CustomerNumber!, CustomerNumberPolicy.PadPayerIdentifierForEnv(payer[0].Identifier, customer.AccountNumber, store.Environment, out _),
                    contract.Number, point.Start, point.EndDate) { SourceAccount = customer.AccountNumber });
            }
            if (customerRoutes.Count == 0) continue;
            customers.Add(new(dto, payers, customer.AccountNumber));
            routes.AddRange(customerRoutes);
        }
        var mapping = new SettlMappingService(load, warnings, reference.ToDateTime(TimeOnly.MinValue));
        var accounts = store.Scope?.SupportingAccounts ?? load.Customers.Select(c => c.AccountNumber).ToArray();
        var pointMapping = await mapping.ExtractAccountMeteringPointMappingAsync(accounts);
        var restored = AborabatRepair.RestoreCustomerPayerSources(
            await store.ReadSectionAsync<XellentContractPartRow>("xellent-contract-parts", ct),
            await store.ReadSectionAsync<XellentContractPartRow>("xellent-contract-part-logs", ct),
            await store.ReadSectionAsync<XellentChargeSubscriptionRow>("xellent-charge-subscriptions", ct), reference);
        var restorationErrors = new Dictionary<string, string>();
        // The domain mapper supplies identity/payment-term/template context. Raw part records
        // supply restored periods, including future parts deleted by the known migration batch.
        foreach (var (account, tuples) in pointMapping)
        {
            var restoredTuples = tuples.Where(p => store.Scope is not null && !store.Scope.MeteringPoints.Contains(p.MeteringPointId)).ToList();
            var selected = restored.Electricity.Where(p => p.Account == account && (p.End.Year <= 1900 || p.End >= reference.ToDateTime(TimeOnly.MinValue))
                && (store.Scope is null || store.Scope.MeteringPoints.Contains(p.MeteringPoint)));
            foreach (var part in selected)
            {
                if (restored.AmbiguousPoints.Contains(part.MeteringPoint) || restored.AmbiguousChargerAccounts.Contains(account))
                { restorationErrors[part.MeteringPoint] = "Ambiguous historical electricity or charger contract part."; continue; }
                var contexts = tuples.Where(t => t.MeteringPointId == part.MeteringPoint && t.ContractNum == part.ContractNumber).ToArray();
                if (contexts.Length == 0 || contexts.Select(t => (t.ContractStartDate, t.IsJoined, t.PaymentTerm)).Distinct().Count() != 1)
                { restorationErrors[part.MeteringPoint] = "Restored part has no unique domain contract/payment context."; continue; }
                var context = contexts[0];
                restoredTuples.Add((part.MeteringPoint, context.ContractStartDate, part.Start, part.End,
                    context.IsJoined, part.ContractNumber, part.ProductNumber, part.AmrCode!, context.PaymentTerm));
            }
            tuples.Clear(); tuples.AddRange(restoredTuples.Distinct());
        }
        EvCharger RestoreCharger(EvCharger charger) => charger with
        {
            Parts = charger.Parts.Where(p => p.Kind != EvChargerPartKind.Subscription)
                .Concat(restored.Chargers.Where(p => p.Account == charger.CustomerAccount && p.ContractNumber == charger.ContractNumber)
                    .Select(p => new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, p.ProductNumber,
                        DateOnly.FromDateTime(p.Start), p.End.Year <= 1900 || p.End.Year >= 9999 ? null : DateOnly.FromDateTime(p.End))))
                .Distinct().OrderBy(p => p.StartDate).ToArray()
        };
        var restoredChargersByMp = load.EvChargersByMeteringPoint.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EvCharger>)kv.Value.Select(RestoreCharger).ToArray());
        var restoredChargersByAccount = load.EvChargersByAccount.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EvCharger>)kv.Value.Select(RestoreCharger).ToArray());
        var createOptions = new CreateOptions
        {
            Accounts = accounts, ReferenceDate = reference.ToDateTime(TimeOnly.MinValue), DeliveryCategory = "El-ekstern",
            ProductNumContractMap = mappings?.ProductNum, GroupNumContractMap = mappings?.GroupNum,
            AccountNumContractMap = mappings?.AccountNum, TemplateProductContractMap = mappings?.TemplateProducts,
            ElectricityChargerContractMap = mappings?.ElectricityChargerContractMap,
            ContractTemplatesByContractNum = (await db.ExuContractTables.Where(c => c.Dataareaid == dataArea && accounts.Contains(c.Custaccount)
                && c.Contracttemplatenum != null && c.Contracttemplatenum != "").ToListAsync(ct)).ToDictionary(c => c.Contractnum, c => c.Contracttemplatenum.Trim())
        };
        // ResolveLocal performs no Settl calls. Preserve the production planner's products/charger rules.
        var expectedContracts = new List<RepairExpectedContract>();
        var mappingErrors = new Dictionary<string, string>(restorationErrors);
        var valueErrors = AttributeSourceErrors(sourceErrors, store.Scope?.MeteringPoints ?? routes.Select(r => r.MeteringPoint).Distinct().ToArray(), accounts, mappingErrors);
        var groupings = await mapping.ExtractAccountGroupingsAsync(accounts);
        var identities = load.Customers.ToDictionary(c => c.AccountNumber, c => !string.IsNullOrEmpty(c.Cvr) ? CustomerIdentity.Cvr : !string.IsNullOrEmpty(c.Cpr) ? CustomerIdentity.Cpr : CustomerIdentity.Blank);
        foreach (var (account, tuples) in pointMapping)
        {
            var accountPoints = tuples.Select(p => p.MeteringPointId).Where(mp => store.Scope is null || store.Scope.MeteringPoints.Contains(mp)).Distinct().ToArray();
            if (accountPoints.Length == 0) continue;
            try
            {
                // Keep all of an account's MPs together: the production charger rules
                // distinguish a charger-linked MP from its ordinary sibling MPs.
                if (accountPoints.Any(mappingErrors.ContainsKey))
                    throw new InvalidOperationException("An account contract part requires historical period review.");
                var accountMapping = pointMapping.Where(kv => kv.Key == account).ToDictionary(kv => kv.Key, kv => kv.Value);
                var resolution = ResolveAccountContractContext(createOptions, groupings, accountMapping, identities, restoredChargersByMp, restoredChargersByAccount);
                expectedContracts.AddRange(resolution.Links.Where(l => accountPoints.Contains(l.MeteringPointId)).Select(l => new RepairExpectedContract(l.MeteringPointId, l.ContractName,
                    DateOnly.FromDateTime(l.ContractPartStartdate), DateOnly.FromDateTime(l.ContractPartEnddate), l.ProductNum,
                    string.Join("|", tuples.Where(p => p.MeteringPointId == l.MeteringPointId && p.ProductNum == l.ProductNum).Select(p => p.ContractNum).Distinct().Order())
                    + "; historical periods from xellent-contract-parts/logs and xellent-charge-subscriptions (AborabatRepair restoration rules)")).Distinct());
            }
            catch (InvalidOperationException ex)
            {
                foreach (var mp in accountPoints) mappingErrors.TryAdd(mp, ex.Message);
            }
        }
        var sourcePoints = new Dictionary<string, HashSet<string>>();
        foreach (var chunk in routes.Select(r => r.ContractNumber).Distinct().Chunk(1000))
        {
            var headers = await db.ExuContractTables.Where(c => c.Dataareaid == dataArea && chunk.Contains(c.Contractnum))
                .Select(c => new { c.Contractnum, c.Custaccount }).Distinct().ToListAsync(ct);
            if (headers.GroupBy(h => h.Contractnum).Any(g => g.Select(h => h.Custaccount).Distinct().Count() != 1))
                throw new InvalidOperationException("Contract number has ambiguous source account ownership.");
            foreach (var header in headers)
            {
                if (!sourcePoints.TryGetValue(header.Custaccount, out var points)) sourcePoints[header.Custaccount] = points = [];
                points.UnionWith(routes.Where(r => r.ContractNumber == header.Contractnum).Select(r => r.MeteringPoint));
            }
        }
        evidenceTimer.Complete($"customers={customers.Count}, routes={routes.Count}");
        captureTimer.Complete();
        var assignments = restoredChargersByMp.SelectMany(pair => pair.Value.Select(charger => new XellentChargerAssignmentRow(pair.Key, charger, reference)))
            .Concat(restoredChargersByAccount.SelectMany(pair => pair.Value.Select(charger => new XellentChargerAssignmentRow(null, charger, reference))))
            .Where(a => store.Scope is null || a.MeteringPoint is null || store.Scope.MeteringPoints.Contains(a.MeteringPoint))
            .DistinctBy(a => (a.MeteringPoint, a.Charger.CustomerAccount, a.Charger.InstallationNumber)).ToList();
        var installations = await db.ExuInstallations.ToListAsync(ct);
        var delpoints = await db.ExuDelpoints.ToListAsync(ct);
        var streets = await db.ExuStreets.ToListAsync(ct);
        assignments = assignments.Select(a =>
        {
            var installation = installations.SingleOrDefault(i => i.Instnum == a.Charger.InstallationNumber);
            var candidates = delpoints.Where(p => p.Meteringpoint == a.MeteringPoint).Select(p =>
                new XellentMpAddressCandidate(p.Attachmentnum, p.Num,
                    streets.FirstOrDefault(s => s.Streetnum == p.Streetnum && s.Countynum == p.Countynum)?.Streetname ?? p.Addressstreet,
                    p.Housenumstart, p.Houseletterstart, p.Zipcodeid, p.Addressid, "Målepunktets adresse i Xellent")).ToList();
            return a with { AddressEvidence = new(installation?.Addressid, installation?.Instaddressformatted,
                installation?.Zipcodeid, "Laderens adresse i Xellent", candidates) };
        }).ToList();
        return new RepairXellentEvidence(store.StartedAt, reference, customers, routes)
            { SourceAccountPoints = sourcePoints.ToDictionary(kv => kv.Key, kv => kv.Value.Order().ToArray()), OriginalAccountsPreserved = true,
                ExpectedContracts = expectedContracts, ContractMappingErrors = mappingErrors, ChargerAssignments = assignments, SourceValueErrors = valueErrors };
    }

    internal static Dictionary<string, string> AttributeSourceErrors(IEnumerable<string> errors, IReadOnlyList<string> targets,
        IReadOnlyList<string> accounts, Dictionary<string, string> mappingErrors)
    {
        var values = new Dictionary<string, string>();
        foreach (var error in errors)
        {
            var owner = System.Text.RegularExpressions.Regex.Match(error, @"^(Customer|MP)\s+(\d+)(?=[:\s])");
            if (!owner.Success)
            {
                foreach (var mp in targets) mappingErrors.TryAdd(mp, "Unattributed source finding requires review.");
                continue;
            }
            var id = owner.Groups[2].Value;
            if (owner.Groups[1].Value == "Customer")
            { if (accounts.Contains(id)) values[id] = "Source customer/payer values require review."; }
            else if (targets.Contains(id)) mappingErrors.TryAdd(id, "Source MP address requires review.");
        }
        return values;
    }

    internal static ContractNameResolver.Result ResolveAccountContractContext(CreateOptions options,
        List<DataMigration.Migration.Runners.AccountGrouping> groups,
        Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>> mapping,
        IReadOnlyDictionary<string, CustomerIdentity> identities,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> chargersByMp,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> chargersByAccount)
    {
        var warnings = new WarningCollector();
        var result = new ContractNameResolver(null!, warnings, _ => { }).ResolveLocal(options, groups, mapping, identities, chargersByMp, chargersByAccount);
        if (warnings.GetErrors().Any()) throw new InvalidOperationException("Contract planner reported errors for this account.");
        return result;
    }
    internal static async Task CaptureSettlAsync(StateSourceSnapshotStore store,
        ISettlCustomerClient customers, ISettlPayerClient payers, ISettlMeteringPointClient points, CancellationToken ct, ISettlContractClient? contracts = null)
    {
        using var captureTimer = new CaptureStage("Settl customer/payer capture");
        using var pointTimer = new CaptureStage(store.Scope is null ? "Settl metering point inventory" : "Selected Settl metering point lookups");
        var pointLoader = new SettlMeteringPointCosLoader(points, Console.Error.WriteLine);
        var pointRows = (store.Scope is { } scope
            ? await pointLoader.LoadScopedAsync(scope.MeteringPoints, ct, includeNotInDelivery: true)
            : await pointLoader.LoadAsync(new CosPeriod(null, null), ct, includeNotInDelivery: true, commodity: CommodityFilter.Electricity))
            .Values.ToList();
        pointTimer.Complete($"scopedPoints={pointRows.Count}");
        using var historyTimer = new CaptureStage("Settl delivery histories");
        var historyCalls = 0;
        var ids = pointRows.Select(p => p.Id).ToHashSet();
        var histories = new List<GenerateStateOfflineCache.DeliveryHistoryRecord>();
        foreach (var p in pointRows)
        {
            var (status, error) = await points.GetDeliveryStatusesAsync(p.Id, ct);
            historyCalls++;
            if (historyCalls % 25 == 0) Console.Error.WriteLine($"  Settl delivery histories: {historyCalls}/{pointRows.Count}, elapsedSeconds={historyTimer.Elapsed.TotalSeconds:F2}.");
            if (error is not null || status is null) throw new InvalidOperationException($"Settl metering-point delivery history lookup failed (entityId={p.Id}, completed={histories.Count}/{pointRows.Count}); capture remains incomplete.");
            var (date, disposition) = MoveInDeliveryFilter.FirstDelivery(status.DeliveryStatuses);
            histories.Add(new(p.Identification, p.Id, p.Info.InDelivery, p.Info.CurrentOrNextDeliveryPeriod,
                p.Info.MostRecentCompletedDeliveryPeriod, status, null, date, disposition));
        }
        historyTimer.Complete($"calls={historyCalls}");
        var customerActivity = new InventoryActivity("customer");
        var payerActivity = new InventoryActivity("payer");
        using var customerActivityTimer = new CaptureStage("Settl customer association/version reads", details: () => customerActivity.Summary);
        var customerInventory = new List<RepairCustomerInventory>();
        var payerInventory = new List<RepairPayerInventory>();
        var customerLinks = new List<RepairCustomerLink>();
        var payerLinks = new List<RepairPayerLink>();
        // All entities must be inspected: an unexpected owner can hold a target point.
        // Persist only dependencies and entities with links to targets for scoped snapshots.
        await foreach (var page in InventoryPagesAsync("customer", customers.FindCustomersAsync, p => p.Id, Console.Error.WriteLine, ct))
        {
            await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (c, token) =>
            {
                try
                {
                var (links, linkError) = await customerActivity.CallAsync("association", c.Id, () => customers.GetMeteringPointsAsync(c.Id, ct));
                if (links?.Count >= 1000) throw new InvalidOperationException("Association endpoint reached its 1000-row limit; complete conflict evidence cannot be established.");
                if (linkError is not null || links is null) throw new InvalidOperationException($"Settl customer association lookup failed ({FailureCategory(linkError)}, entityId={c.Id}); capture remains incomplete.");
                var relevant = links.Where(l => ids.Contains(l.MeteringPointId)).ToArray();
                lock (customerLinks) customerLinks.AddRange(relevant.Select(l => new RepairCustomerLink(c.Id, l)));
                if (store.Scope is not null && relevant.Length == 0 && !store.Scope.SupportingAccounts.Any(a => c.CustomerNumber == a || c.CustomerNumber == CustomerNumberPolicy.PadForEnv(a, store.Environment, out _))) return;
                var (history, historyError) = await customerActivity.CallAsync("history", c.Id, () => customers.GetHistoryAsync(c.Id, ct));
                if (historyError is not null || history?.ChangeDates is null) throw new InvalidOperationException($"Settl customer history lookup failed ({FailureCategory(historyError)}, entityId={c.Id}); capture remains incomplete.");
                var (detail, detailError) = await customerActivity.CallAsync("detail", c.Id, () => customers.GetByIdAsync(c.Id, ct));
                if (detailError is not null || detail is null) throw new InvalidOperationException($"Settl customer detail lookup failed ({FailureCategory(detailError)}, entityId={c.Id}); capture remains incomplete.");
                var first = history.ChangeDates.Select(d => (DateOnly?)MoveInDeliveryFilter.ToDkDate(new DateTimeOffset(d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d))).Append(detail.Address?.Start).Min();
                lock (customerInventory) customerInventory.Add(new(c, first));
                }
                finally { customerActivity.Finished(); }
            });
        }
        customerActivityTimer.Complete();
        using var payerActivityTimer = new CaptureStage("Settl payer association/version reads", details: () => payerActivity.Summary);
        var relevantCustomers = customerInventory.Select(c => c.Customer.Id).ToHashSet();
        await foreach (var page in InventoryPagesAsync("payer", payers.FindPayersAsync, p => p.Id, Console.Error.WriteLine, ct))
        {
            await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (p, token) =>
            {
                try
                {
                var (links, linkError) = await payerActivity.CallAsync("association", p.Id, () => payers.GetMeteringPointsAsync(p.Id, ct));
                if (links?.Count >= 1000) throw new InvalidOperationException("Association endpoint reached its 1000-row limit; complete conflict evidence cannot be established.");
                if (linkError is not null || links is null) throw new InvalidOperationException($"Settl payer association lookup failed ({FailureCategory(linkError)}, entityId={p.Id}); capture remains incomplete.");
                var relevant = links.Where(l => ids.Contains(l.MeteringPointId)).ToArray();
                lock (payerLinks) payerLinks.AddRange(relevant.Select(l => new RepairPayerLink(p.Id, l)));
                if (store.Scope is not null && relevant.Length == 0 && !relevantCustomers.Contains(p.CustomerId)
                    && !store.Scope.SupportingAccounts.SelectMany(a => new[] { a, CustomerNumberPolicy.PadForEnv(a, store.Environment, out _) }).Any(a => p.PayerIdentification == a || p.PayerIdentification?.StartsWith(a + "-", StringComparison.Ordinal) == true)) return;
                var (history, historyError) = await payerActivity.CallAsync("history", p.Id, () => payers.GetHistoryAsync(p.Id, ct));
                if (historyError is not null || history?.ChangeDates is null) throw new InvalidOperationException($"Settl payer history lookup failed ({FailureCategory(historyError)}, entityId={p.Id}); capture remains incomplete.");
                var (detail, detailError) = await payerActivity.CallAsync("detail", p.Id, () => payers.GetByIdAsync(p.Id, cancellationToken: ct));
                if (detailError is not null || detail is null) throw new InvalidOperationException($"Settl payer detail lookup failed ({FailureCategory(detailError)}, entityId={p.Id}); capture remains incomplete.");
                var starts = new[] { detail.PayerInformation?.Start, detail.PayerBilling?.Start };
                var first = history.ChangeDates.Select(d => (DateOnly?)MoveInDeliveryFilter.ToDkDate(new DateTimeOffset(d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d))).Append(starts.Max()).Min();
                var ownership = new Dictionary<DateOnly, Guid>();
                var versions = new Dictionary<DateOnly, RepairPayerEffectiveVersion>();
                foreach (var date in histories.Where(h => h.FirstDate is not null).Select(h => h.FirstDate!.Value).Distinct().Where(d => d >= first))
                {
                    var (historical, historicalError) = await payerActivity.CallAsync("historicalOwner", p.Id, () => payers.GetByIdAsync(p.Id, date.ToDateTime(TimeOnly.MinValue), ct));
                    if (historicalError is not null || historical is null)
                        throw new InvalidOperationException($"Settl payer historical-owner lookup failed ({FailureCategory(historicalError)}, entityId={p.Id}, effectiveDate={date:yyyy-MM-dd}); capture remains incomplete.");
                    ownership[date] = historical.CustomerId;
                    versions[date] = new(historical.CustomerId, historical.PayerInformation?.Start, historical.PayerBilling?.Start);
                }
                lock (payerInventory) payerInventory.Add(new(p, first) { CustomerAtDate = ownership, CapturedVersions = versions });
                }
                finally { payerActivity.Finished(); }
            });
        }
        payerActivityTimer.Complete();
        var (catalog, catalogError) = contracts is null ? (null, "Contract client required") : await contracts.FindContractsAsync("Electricity", ct);
        if (catalogError is not null || catalog is null) throw new InvalidOperationException("Contract catalog lookup failed; capture remains incomplete.");
        await store.WriteAsync("repair-settl", new[] { new RepairSettlEvidence(DateTimeOffset.UtcNow, pointRows, histories,
            customerInventory, payerInventory, customerLinks, payerLinks) { ContractCatalog = catalog } }, ct);
        captureTimer.Complete($"customers={customerInventory.Count}, payers={payerInventory.Count}, customerLinks={customerLinks.Count}, payerLinks={payerLinks.Count}");
    }
}


internal sealed class CaptureStage(string name, Action<string>? write = null, Func<string>? details = null) : IDisposable
{
    private readonly Stopwatch _timer = Start(name, write);
    private bool _complete;
    public TimeSpan Elapsed => _timer.Elapsed;
    private static Stopwatch Start(string name, Action<string>? write)
    {
        (write ?? Console.Error.WriteLine)($"  [timer] {name}: started at {DateTimeOffset.UtcNow:O}.");
        return Stopwatch.StartNew();
    }
    public void Complete(string? summary = null)
    {
        if (_complete) return;
        _complete = true;
        (write ?? Console.Error.WriteLine)($"  [timer] {name}: completed at {DateTimeOffset.UtcNow:O} in {_timer.Elapsed.TotalSeconds:F2}s. {summary ?? details?.Invoke()}");
    }
    public void Dispose()
    {
        if (!_complete) (write ?? Console.Error.WriteLine)($"  [timer] {name}: stopped incomplete at {DateTimeOffset.UtcNow:O} after {_timer.Elapsed.TotalSeconds:F2}s. {details?.Invoke()}");
    }
}

internal sealed class InventoryActivity(string kind)
{
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, (int Calls, double Seconds)> _calls = new();
    private readonly object _progressLock = new();
    private int _finished;
    private TimeSpan _lastReport;
    public string Summary => $"entitiesAttempted={Volatile.Read(ref _finished)}, elapsedSeconds={_timer.Elapsed.TotalSeconds:F2}; "
        + string.Join(", ", _calls.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}Calls={kv.Value.Calls}, {kv.Key}ApiSeconds={kv.Value.Seconds:F2}"));
    public async Task<T> CallAsync<T>(string operation, Guid entityId, Func<Task<T>> call)
    {
        var timer = Stopwatch.StartNew();
        using var heartbeatStop = new CancellationTokenSource();
        try
        {
            var pending = call();
            while (!pending.IsCompleted)
            {
                if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30), heartbeatStop.Token)) == pending) break;
                Console.Error.WriteLine($"  [waiting] Settl {kind} {operation} request pending: entityId={entityId}, elapsedSeconds={timer.Elapsed.TotalSeconds:F2}.");
            }
            return await pending;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Settl {kind} {operation} request failed: entityId={entityId}, elapsedSeconds={timer.Elapsed.TotalSeconds:F2}. Capture remains incomplete.", ex);
        }
        finally
        {
            heartbeatStop.Cancel();
            _calls.AddOrUpdate(operation, (1, timer.Elapsed.TotalSeconds), (_, old) => (old.Calls + 1, old.Seconds + timer.Elapsed.TotalSeconds));
        }
    }
    public void Finished()
    {
        var finished = Interlocked.Increment(ref _finished);
        lock (_progressLock)
        {
            if (finished % 100 != 0 && _timer.Elapsed - _lastReport < TimeSpan.FromSeconds(10)) return;
            _lastReport = _timer.Elapsed;
            Console.Error.WriteLine($"  Capture {kind} association progress: {Summary}");
        }
    }
}
