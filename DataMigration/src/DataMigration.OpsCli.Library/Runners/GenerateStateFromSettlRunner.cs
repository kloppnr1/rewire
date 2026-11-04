using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataMigration.Core;
using DataMigration.Core.Helpers;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Runners;

public class GenerateStateFromSettlRunOptions
{
    public string OutputPath { get; set; } = "migration-state.json";
    /// <summary>
    /// Legacy CLI option, accepted for compatibility but ignored by COS-driven generation.
    /// Actual ownership and history dates come from each Settl metering point.
    /// </summary>
    public DateTime? ReferenceDate { get; set; }
    public string? ConnectionString { get; set; }
    public bool SkipActive { get; set; }
    public string? Env { get; set; }
    /// <summary>
    /// Retired: a flat date cannot prove invoice ownership and is rejected.
    /// </summary>
    public bool FlatReferenceDate { get; set; }
    /// <summary>Skip the water/heat census over the excluded customers (one extra Settl read).</summary>
    public bool SkipWaterCensus { get; set; }

    /// <summary>
    /// Retired: a synthetic supplier-change date cannot prove invoice ownership and is rejected.
    /// </summary>
    public DateTime? IncludeNotInDeliveryCos { get; set; }

    /// <summary>
    /// Opt-in: drop the Slutafregning eligibility test, so a customer that is still being billed keeps
    /// its invoice history instead of being zeroed out or aborting the run. Only the per-customer
    /// configured per-customer history window filters. Ignored in <see cref="FlatReferenceDate"/> mode.
    /// </summary>
    public bool IncludeActive { get; set; }

    /// <summary>
    /// Opt-in: keep the SourceAccounts enrichment but skip the invoice history entirely — no
    /// CUSTINVOICEJOUR query, no Slutafregning eligibility test, every entry's invoice list left
    /// empty. For consumers that only need the account identity (Bifrost reads SettlAccountNumber,
    /// CustomerName, DeliveryCategories and SourceAccounts, and never touches Invoices). The file is
    /// then NOT usable for PDF upload.
    /// </summary>
    public bool NoInvoices { get; set; }

    /// <summary>
    /// Opt-in: omit metering points without a clear Settl customer and Xellent source accounts
    /// with unresolved ownership, including their invoices. Every omission is reported.
    /// </summary>
    public bool SkipUnresolvedSourceAccounts { get; set; }
    /// <summary>Accept a dated first delivery whose Settl start reason is Unknown.</summary>
    public bool IncludeUnknownDeliveryStarts { get; set; }

    /// <summary>
    /// How many years back of invoice history to keep, measured from each customer's own
    /// change-of-supplier date. Configured via <c>Invoicing:HistoryYears</c> in appsettings; defaults to
    /// <see cref="Invoice.ExportHistoryYears"/> when not set.
    /// </summary>
    public int HistoryYears { get; set; } = Invoice.ExportHistoryYears;
    public bool Offline { get; set; }
    /// <summary>Test override; the CLI uses the user's local application-data directory.</summary>
    public string? OfflineCacheDirectory { get; set; }
}

/// <summary>
/// Generates migration-state.json from Settl: one entry per <b>migrated</b> customer.
///
/// <para>By default, each entry is enriched with its Xellent SourceAccounts and invoice history,
/// ready for PDF upload.
/// The population and every date come from Settl: metering points that were ever in delivery are the
/// seed. Only points with a FOR Xellent origin can contribute invoice source accounts; their first
/// delivery start and dated payer identify the Settl owner, and the Xellent account comes from the
/// contract ending at that metering point's COS-1 date. A customer's earliest in-scope COS
/// decides how far back its invoice history reaches. Customers with no metering point in
/// scope were never migrated and are left out of the file entirely — PdfExportCli writes a per-account
/// manifest.json commit marker regardless of invoice count, so an empty entry would mark an account
/// complete with nothing in it.</para>
///
/// <para><see cref="GenerateStateFromSettlRunOptions.IncludeActive"/> keeps customers that are
/// still being billed. A synthetic COS or a flat reference date cannot prove invoice ownership
/// and is rejected for this command.</para>
/// </summary>
public class GenerateStateFromSettlRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = Services.JsonOutput.HumanReadable
    };

    /// <summary>Metering point ids per payersForMeteringPoints call — same batch size as
    /// <see cref="ExportSettlementConfigRunner"/>, which covers the ~12k population in ~31 calls.</summary>
    private const int PayerLookupBatchSize = 400;
    private const int HistoricalAccountLookupChunkSize = 2000;
    private const int HistoricalResolvePortionSize = 15000;
    private const int HistoricalResolveConcurrency = 8;
    private const string InvoiceCompanyId = ForInvoiceScope.CompanyId;

    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlMeteringPointClient? _meteringPointClient;
    private readonly ISettlPayerClient? _payerClient;
    private readonly ISettlCustomerLinkLookup? _customerLinkLookup;
    private StateSourceSnapshot? _sourceSnapshot;
    private IReadOnlyList<SourceAccountOwnershipIssue>? _ownershipIssues;
    private IReadOnlyList<SameCvrAcceptance>? _sameCvrAccepted;
    private IReadOnlyList<OwnershipTieBreak>? _ownershipTieBreaks;
    private IReadOnlyList<HistoricalSourceDecision>? _historicalSourceDecisions;
    private IReadOnlyList<ExcludedCompanyInvoice>? _excludedCompanyInvoices;
    private IReadOnlyList<DeliveryStartAudit>? _deliveryStartAudit;
    private IReadOnlyList<GenerateStateOfflineCache.OwnershipEvidence>? _ownershipEvidence;
    private IReadOnlyList<string> _excludedSourceAccounts = [];
    private IReadOnlySet<string> _settlCustomerNumbers = new HashSet<string>();
    // Bygger sit eget Xellent-lag udenom XellentDomainLoader, så listen skal med her.
    private readonly IReadOnlyList<string> _ignoredGroups;

    public GenerateStateFromSettlRunner(
        ISettlCustomerClient customerClient,
        IReadOnlyList<string>? ignoredGroups = null,
        ISettlMeteringPointClient? meteringPointClient = null,
        ISettlPayerClient? payerClient = null,
        ISettlCustomerLinkLookup? customerLinkLookup = null)
    {
        _customerClient = customerClient;
        _ignoredGroups = ignoredGroups ?? [];
        _meteringPointClient = meteringPointClient;
        _payerClient = payerClient;
        _customerLinkLookup = customerLinkLookup;
    }

    private static void Log(string message) => Console.Error.WriteLine(message);

    /// <summary>Pull source lookups without assigning an invoice owner or generating state.</summary>
    public async Task PullSourcesAsync(string environment, string connectionString,
        string? directory = null, CancellationToken ct = default, StatePullScope? scope = null, bool deferCompletion = false)
    {
        if (_meteringPointClient is null || _payerClient is null)
            throw new InvalidOperationException("Pull needs Settl metering point and payer clients.");
        var store = StateSourceSnapshotStore.Open(directory, environment, scope);
        await store.BeginUpdateAsync(ct);
        Log($"  Offline source data: {store.DirectoryPath}");

        var customers = new List<CustomerInfoDto>();
        const int customerPageSize = 1000;
        try
        {
            while (true)
            {
                var (page, _, error) = await _customerClient.FindCustomersAsync(customerPageSize,
                    customers.Count, ct);
                if (error is not null) throw new InvalidOperationException($"Settl customers: {error}");
                customers.AddRange(page);
                if (page.Count < customerPageSize) break;
            }
        }
        finally
        {
            await store.WriteAsync("settl-customers", scope is null ? customers : customers.Where(c => scope.SupportingAccounts.Any(a => c.CustomerNumber == a || c.CustomerNumber == CustomerNumberPolicy.PadForEnv(a, environment, out _))).ToList(), CancellationToken.None);
        }
        Log($"  Settl customers: {customers.Count}");

        var loader = new SettlMeteringPointCosLoader(_meteringPointClient, Log);
        var meteringPoints = (scope is null
            ? await loader.LoadAsync(new CosPeriod(null, null), ct, includeNotInDelivery: true, commodity: CommodityFilter.Electricity)
            : await loader.LoadScopedAsync(scope.MeteringPoints, ct, includeNotInDelivery: true))
            .Values.OrderBy(m => m.Identification, StringComparer.Ordinal).ToList();
        await store.WriteAsync("settl-metering-points", meteringPoints, ct);
        Log($"  Settl electricity metering points: {meteringPoints.Count}");

        var inspect = meteringPoints.Where(m => CosStatePlanner.WasEverInDelivery(m.Info)).ToList();
        var byId = inspect.ToDictionary(m => m.Id);
        var histories = new ConcurrentBag<GenerateStateOfflineCache.DeliveryHistoryRecord>();
        var filtered = await MoveInDeliveryFilter.FilterAsync(inspect,
            async (id, token) =>
            {
                GetDeliveryStatusesResponse? statuses;
                string? error;
                try { (statuses, error) = await _meteringPointClient.GetDeliveryStatusesAsync(id, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { statuses = null; error = ex.Message; }
                var (firstDate, disposition) = error is not null || statuses is null
                    ? ((DateOnly?)null, "DeliveryStatusLookupFailed")
                    : MoveInDeliveryFilter.FirstDelivery(statuses.DeliveryStatuses);
                var mp = byId[id];
                histories.Add(new GenerateStateOfflineCache.DeliveryHistoryRecord(
                    mp.Identification, id, mp.Info.InDelivery,
                    mp.Info.CurrentOrNextDeliveryPeriod,
                    mp.Info.MostRecentCompletedDeliveryPeriod,
                    statuses, error, firstDate, disposition));
                return (statuses, error);
            }, ct, includeUnknownFirstStarts: true);
        var orderedHistories = histories.OrderBy(h => h.MeteringPoint, StringComparer.Ordinal).ToArray();
        await store.WriteAsync("settl-delivery-histories", orderedHistories, ct);
        Log($"  Delivery histories: {orderedHistories.Length}");
        Log($"  Unknown first delivery starts captured for optional inclusion: " +
            orderedHistories.Count(h => h.Disposition == "UnknownDeliveryStartReason"));

        var payerLookups = new List<StatePayerLookup>();
        try
        {
            foreach (var dateGroup in filtered.Kept.GroupBy(m => m.Cos))
            foreach (var batch in dateGroup.Select(m => m.Id).Distinct().Chunk(PayerLookupBatchSize))
            {
                var (items, error) = await _payerClient.FindPayersForMeteringPointsAsync(
                    batch, dateGroup.Key.ToDateTime(TimeOnly.MinValue), ct);
                payerLookups.Add(new StatePayerLookup(dateGroup.Key, batch, items, error));
            }
        }
        finally
        {
            await store.WriteAsync("settl-payers", payerLookups, CancellationToken.None);
        }
        Log($"  Dated payer lookup batches: {payerLookups.Count}");
        if (scope is not null)
        {
            var ownerIds = payerLookups.Where(p => p.Items is not null).SelectMany(p => p.Items!)
                .Where(p => p.Payer is not null).Select(p => p.Payer!.CustomerId).ToHashSet();
            await store.WriteAsync("settl-customers", customers.Where(c => ownerIds.Contains(c.Id)
                || scope.SupportingAccounts.Any(a => c.CustomerNumber == a || c.CustomerNumber == CustomerNumberPolicy.PadForEnv(a, environment, out _))).ToList(), ct);
        }


        // Xellent is bounded by the metering points Settl returned, never by payer/customer success.
        var cosByMp = filtered.Kept.ToDictionary(m => m.Identification, m => m.Cos,
            StringComparer.Ordinal);
        var resolution = await MeteringPointAccountResolver.ResolveAsync(connectionString,
            cosByMp, MigrationConstants.DataAreaId, ct: ct,
            companyId: InvoiceCompanyId, includeBlankCompany: true);
        var pairs = resolution.Pairs.Select(p => new StateMpAccount(p.MpId, p.Account))
            .OrderBy(p => p.MeteringPoint, StringComparer.Ordinal).ToArray();
        await store.WriteAsync("xellent-mp-accounts", pairs, ct);
        await store.WriteAsync("xellent-for-mps", resolution.XellentMpIds
            .Order(StringComparer.Ordinal).ToArray(), ct);

        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(300); })
            .Options;
        await using var context = new XellentDbContext(dbOptions);
        var mehIds = await LoadMehMeteringPointIdsAsync(context, cosByMp.Keys, ct);
        await store.WriteAsync("xellent-meh-mps", mehIds.Order(StringComparer.Ordinal).ToArray(), ct);

        // Give every Xellent candidate its own key so a point with several historical accounts
        // cannot suppress representative evidence for any of them.
        var synthetic = pairs.Select((p, i) => (Key: $"source-{i}", p.MeteringPoint,
                p.Account, Cos: cosByMp[p.MeteringPoint]))
            .ToArray();
        var syntheticCustomerByMp = synthetic.ToDictionary(x => x.Key, _ => "000000",
            StringComparer.Ordinal);
        var syntheticCosByMp = synthetic.ToDictionary(x => x.Key, x => x.Cos,
            StringComparer.Ordinal);
        var syntheticPairs = synthetic.Select(x => (MpId: x.Key, x.Account)).ToArray();
        var evidenceOptions = new GenerateStateFromSettlRunOptions
            { Env = environment, SkipUnresolvedSourceAccounts = true };
        await ResolveInvoiceSourcesAsync(context, new MigrationStateDocument(),
            syntheticCustomerByMp, syntheticCosByMp, syntheticPairs, evidenceOptions, ct);
        var originalMpByKey = synthetic.ToDictionary(x => x.Key, x => x.MeteringPoint,
            StringComparer.Ordinal);
        var evidence = (_ownershipEvidence ?? [])
            .Select(e => new StateXellentEvidence(originalMpByKey[e.MeteringPoint],
                e.SourceAccount, e.Representative, e.Cos, e.RepresentativeMembers))
            .ToArray();
        await store.WriteAsync("xellent-representatives", evidence, ct);

        var invoiceAccounts = pairs.Select(p => p.Account)
            .Concat(evidence.SelectMany(e => e.RepresentativeMembers))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var invoices = await XellentInvoiceQueries.QueryAsync(context, invoiceAccounts,
            MigrationConstants.DataAreaId, ct);
        await store.WriteAsync("xellent-invoices", invoices, ct);
        var excluded = await ForInvoiceScope.FindExcludedAsync(context, invoiceAccounts,
            MigrationConstants.DataAreaId, ct);
        await store.WriteAsync("xellent-excluded-invoices", excluded, ct);
        var failedLookups = orderedHistories.Count(h => h.Error is not null)
            + payerLookups.Count(p => p.Error is not null || p.Items is null);
        if (failedLookups > 0)
            Log($"  WARNING: {failedLookups} Settl lookup(s) failed; offline state will report or skip affected points.");
        if (!deferCompletion) await store.CompleteAsync(ct, includesUnknownDeliveryStarts: true);
        Log($"  Complete offline source data: {store.DirectoryPath}");
    }

    internal static async Task PullXellentContractSourcesAsync(XellentDbContext context,
        StateSourceSnapshotStore store, string connectionString, string dataAreaId,
        DateOnly referenceDate, CancellationToken ct, StatePullScope? scope = null, bool rawRepairCapture = false)
    {
        // Capture live parts and logged changes. Keep the log change type so offline
        // repair can use deleted parts without treating end-date changes as extra parts.
        var live = new List<XellentContractPartRow>();
        foreach (var scopeBatch in scope?.MeteringPoints.Chunk(1000) ?? new[] { Array.Empty<string>() })
        {
            live.AddRange(await (
            from c in context.ExuContractTables.AsNoTracking()
            join p in context.ExuContractPartTables.AsNoTracking()
                on new { c.Contractnum, c.Dataareaid, c.Companyid }
                equals new { p.Contractnum, p.Dataareaid, p.Companyid }
            join a in context.ExuAgreementTables.AsNoTracking()
                on new { Agreementnum = p.Instagreenum, p.Dataareaid, p.Companyid }
                equals new { a.Agreementnum, a.Dataareaid, a.Companyid }
            join d in context.ExuDelpoints.AsNoTracking()
                on new { Attachmentnum = a.Agreementnum, a.Dataareaid, a.Companyid }
                equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
            where c.Dataareaid == dataAreaId
                && (scopeBatch.Length == 0 || scopeBatch.Contains(d.Meteringpoint))
                && p.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
            select new XellentContractPartRow(d.Meteringpoint, c.Custaccount, c.Contractnum,
                p.Instagreenum, p.Productnum, p.Startdate, p.Enddate, "Live", null, null)
                { CompanyId = p.Companyid, AmrCode = p.Amrcode }
        ).ToListAsync(ct));
        }
        if (scope is not null) live = live.Where(p => scope.MeteringPoints.Contains(p.MeteringPoint)).ToList();
        await store.WriteAsync("xellent-contract-parts", live, ct);
        Log($"  Xellent electricity contract parts: {live.Count}");

        var logged = new List<XellentContractPartRow>();
        foreach (var scopeBatch in scope?.MeteringPoints.Chunk(1000) ?? new[] { Array.Empty<string>() })
        {
            logged.AddRange(await (
            from c in context.ExuContractTables.AsNoTracking()
            join p in context.ExuContractPartLogs.AsNoTracking()
                on new { c.Contractnum, c.Dataareaid, c.Companyid }
                equals new { p.Contractnum, p.Dataareaid, p.Companyid }
            join a in context.ExuAgreementTables.AsNoTracking()
                on new { Agreementnum = p.Instagreenum, p.Dataareaid, p.Companyid }
                equals new { a.Agreementnum, a.Dataareaid, a.Companyid }
            join d in context.ExuDelpoints.AsNoTracking()
                on new { Attachmentnum = a.Agreementnum, a.Dataareaid, a.Companyid }
                equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
            where c.Dataareaid == dataAreaId
                && (scopeBatch.Length == 0 || scopeBatch.Contains(d.Meteringpoint))
                && p.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
            select new XellentContractPartRow(d.Meteringpoint, c.Custaccount, c.Contractnum,
                p.Instagreenum, p.Productnum, p.Contractpartstartdate, p.Contractpartenddate,
                "Log", p.Logdatetime, p.Errormsg == null ? "" : p.Errormsg.Trim())
                { CompanyId = p.Companyid, AmrCode = p.Amrcode,
                    LogEmployeeId = p.Logemplid, LogRecordId = p.Recid }
        ).ToListAsync(ct));
        }
        if (scope is not null) logged = logged.Where(p => scope.MeteringPoints.Contains(p.MeteringPoint)).ToList();
        await store.WriteAsync("xellent-contract-part-logs", logged, ct);
        Log($"  Xellent logged electricity contract parts: {logged.Count}");

        var chargeLive = new List<XellentChargeSubscriptionRow>();
        foreach (var scopeBatch in scope?.SupportingAccounts.Chunk(1000) ?? new[] { Array.Empty<string>() })
        {
            chargeLive.AddRange(await (
            from c in context.ExuContractTables.AsNoTracking()
            join p in context.ExuContractPartTables.AsNoTracking()
                // Charge_Abo contract headers and parts can have different CompanyId values.
                // The charger mapper also joins these by contract number and data area only.
                on new { c.Contractnum, c.Dataareaid }
                equals new { p.Contractnum, p.Dataareaid }
            where c.Dataareaid == dataAreaId
                && (scopeBatch.Length == 0 || scopeBatch.Contains(c.Custaccount))
                && p.Deliverycategory == "Charge_Abo"
            select new XellentChargeSubscriptionRow(c.Custaccount, c.Contractnum,
                p.Productnum, p.Startdate, p.Enddate, "Live", null, null)
                { AgreementNumber = p.Instagreenum, CompanyId = p.Companyid,
                    AmrCode = p.Amrcode }
        ).Distinct().ToListAsync(ct));
        }
        var chargeLogged = new List<XellentChargeSubscriptionRow>();
        foreach (var scopeBatch in scope?.SupportingAccounts.Chunk(1000) ?? new[] { Array.Empty<string>() })
        {
            chargeLogged.AddRange(await (
            from c in context.ExuContractTables.AsNoTracking()
            join p in context.ExuContractPartLogs.AsNoTracking()
                on new { c.Contractnum, c.Dataareaid }
                equals new { p.Contractnum, p.Dataareaid }
            where c.Dataareaid == dataAreaId
                && (scopeBatch.Length == 0 || scopeBatch.Contains(c.Custaccount))
                && p.Deliverycategory == "Charge_Abo"
            select new XellentChargeSubscriptionRow(c.Custaccount, c.Contractnum,
                p.Productnum, p.Contractpartstartdate, p.Contractpartenddate,
                "Log", p.Logdatetime, p.Errormsg == null ? "" : p.Errormsg.Trim())
                { AgreementNumber = p.Instagreenum, CompanyId = p.Companyid,
                    AmrCode = p.Amrcode, LogEmployeeId = p.Logemplid,
                    LogRecordId = p.Recid }
        ).Distinct().ToListAsync(ct));
        }
        if (scope is not null)
        {
            chargeLive = chargeLive.Where(p => scope.SupportingAccounts.Contains(p.Account)).ToList();
            chargeLogged = chargeLogged.Where(p => scope.SupportingAccounts.Contains(p.Account)).ToList();
        }
        if (scope is null && chargeLive.Count == 0 && await context.ExuContractPartTables.AsNoTracking()
                .AnyAsync(p => p.Dataareaid == dataAreaId
                    && p.Deliverycategory == "Charge_Abo", ct))
            throw new InvalidOperationException(
                "Xellent contains Charge_Abo parts, but the offline charger export matched no contracts.");
        await store.WriteAsync("xellent-charge-subscriptions", chargeLive.Concat(chargeLogged).ToArray(), ct);
        Log($"  Xellent Charge_Abo parts (live and logged): {chargeLive.Count + chargeLogged.Count}");

        // Repair snapshots retain source records. Charger assignment is interpreted offline.
        if (rawRepairCapture) return;

        // Capture the physical charger matches for every account with a Charge_Abo part.
        // Keep account-only chargers with a null MP. Consumers decide their own scope
        // from this general snapshot instead of the pull choosing a product or repair.
        var accounts = chargeLive.Concat(chargeLogged)
            .Select(p => p.Account).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (accounts.Length == 0)
        {
            await store.WriteAsync("xellent-charger-assignments",
                Array.Empty<XellentChargerAssignmentRow>(), ct);
            return;
        }
        var warnings = new WarningCollector();
        var options = new MapperOptions(referenceDate, Commodity.Electricity,
            RestorePartsChangedFrom: new DateTime(2026, 8, 31, 9, 13, 49),
            RestorePartsChangedBefore: new DateTime(2026, 8, 31, 9, 59, 33));
        var load = await new DataMigration.OpsCli.Services.XellentDomainLoader(connectionString)
            .LoadAsync(accounts, options, dataAreaId, warnings, ct);
        var mappingErrors = warnings.GetErrors().Where(error => !IsEmptyDarError(error)).ToList();
        foreach (var warning in warnings.GetErrors().Where(IsEmptyDarError))
            Log($"  WARNING: {warning} (exported with no DAR identifier)");
        if (mappingErrors.Count != 0)
            throw new InvalidOperationException("Xellent charger mapping reported errors: "
                + string.Join("; ", mappingErrors));
        var accountSet = accounts.ToHashSet(StringComparer.Ordinal);
        var assignments = load.EvChargersByMeteringPoint
            .SelectMany(pair => pair.Value.Select(charger =>
                new XellentChargerAssignmentRow(pair.Key, charger, referenceDate)))
            .Concat(load.EvChargersByAccount.SelectMany(pair => pair.Value.Select(charger =>
                new XellentChargerAssignmentRow(null, charger, referenceDate))))
            .Where(a => accountSet.Contains(a.Charger.CustomerAccount))
            .DistinctBy(a => (a.MeteringPoint, a.Charger.CustomerAccount,
                a.Charger.InstallationNumber)).ToArray();
        // Capture the raw Xellent values behind the mapper's address verdict. Keep
        // every delivery-point candidate so an ambiguous match stays visible offline.
        var installationNumbers = assignments.Select(a => a.Charger.InstallationNumber)
            .Distinct(StringComparer.Ordinal).ToArray();
        var installations = new List<(string Number, string? Dar, string? Address,
            string? PostalCode, long RecordId)>();
        foreach (var batch in installationNumbers.Chunk(1000))
        {
            var page = await context.ExuInstallations.AsNoTracking()
                .Where(i => i.Dataareaid == dataAreaId && batch.Contains(i.Instnum))
                .Select(i => new { i.Instnum, i.Addressid, i.Instaddressformatted,
                    i.Zipcodeid, i.Recid }).ToListAsync(ct);
            installations.AddRange(page.Select(i => (i.Instnum, (string?)i.Addressid,
                (string?)i.Instaddressformatted, (string?)i.Zipcodeid, i.Recid)));
        }
        var installationLookup = installations.ToLookup(i => i.Number, StringComparer.Ordinal);
        var candidateRows = new List<(string Mp, string Agreement, string Point,
            string? Street, string? FallbackStreet, int? Number, string? Letter,
            string? PostalCode, string? DarId, string Company)>();
        foreach (var batch in assignments.Where(a => a.MeteringPoint is not null)
            .Select(a => a.MeteringPoint!).Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var page = await (
                from d in context.ExuDelpoints.AsNoTracking()
                join s in context.ExuStreets.AsNoTracking()
                    on new { d.Streetnum, d.Countynum, d.Dataareaid }
                    equals new { s.Streetnum, s.Countynum, s.Dataareaid } into streets
                from s in streets.DefaultIfEmpty()
                join m in context.EgsMeterpointTypecodeperiods.AsNoTracking()
                    on new { Delpointnum = d.Num, d.Attachmentnum, d.Dataareaid,
                        d.Companyid, d.Deliverycategory }
                    equals new { m.Delpointnum, m.Attachmentnum, m.Dataareaid,
                        m.Companyid, m.Deliverycategory }
                where d.Dataareaid == dataAreaId
                    && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && batch.Contains(d.Meteringpoint)
                    && m.Enddate == MigrationConstants.EndDateNull
                    && (m.Meterpointtypecode == "E17/Forb" || m.Meterpointtypecode == "FORB")
                select new { Mp = d.Meteringpoint, Agreement = d.Attachmentnum,
                    Point = d.Num, Street = s != null ? s.Streetname : null,
                    FallbackStreet = d.Addressstreet, Number = d.Housenumstart,
                    Letter = d.Houseletterstart, PostalCode = d.Zipcodeid,
                    DarId = d.Addressid, Company = d.Companyid }
            ).ToListAsync(ct);
            candidateRows.AddRange(page.Select(d => (d.Mp, d.Agreement, d.Point,
                (string?)d.Street, (string?)d.FallbackStreet, d.Number,
                (string?)d.Letter, (string?)d.PostalCode,
                (string?)d.DarId, d.Company)));
        }
        var candidatesByMp = candidateRows.ToLookup(d => d.Mp, StringComparer.Ordinal);
        var electricAgreements = live.Concat(logged)
            .Where(p => p.ProductNumber is "V LadVariP" or "V LadVarUA")
            .Select(p => (p.MeteringPoint, p.Account, p.AgreementNumber, p.CompanyId))
            .ToHashSet();
        assignments = assignments.Select(a =>
        {
            var installation = installationLookup[a.Charger.InstallationNumber]
                .OrderBy(i => i.RecordId).FirstOrDefault();
            var candidates = a.MeteringPoint is null ? [] : candidatesByMp[a.MeteringPoint]
                .Where(d => electricAgreements.Contains((a.MeteringPoint,
                    a.Charger.CustomerAccount, d.Agreement, d.Company)))
                .Select(d => new XellentMpAddressCandidate(d.Agreement, d.Point,
                    string.IsNullOrWhiteSpace(d.Street) ? d.FallbackStreet : d.Street,
                    d.Number, d.Letter, d.PostalCode, d.DarId,
                    $"EXU_DELPOINT[ATTACHMENTNUM={d.Agreement}, NUM={d.Point}, COMPANYID={d.Company}]"))
                .Distinct().ToList();
            return a with { AddressEvidence = new XellentChargerAddressEvidence(
                installation.Dar, installation.Address, installation.PostalCode,
                installation.RecordId == 0 ? null
                    : $"EXU_INSTALLATION[INSTNUM={a.Charger.InstallationNumber}, RECID={installation.RecordId}]",
                candidates) };
        }).ToArray();
        await store.WriteAsync("xellent-charger-assignments", assignments, ct);
        Log($"  Xellent charger assignments: {assignments.Length} across {accounts.Length} Charge_Abo accounts");
    }

    // AddressMapper represents Guid.Empty as a missing DAR. It does not discard
    // the MP or its charger assignment; this source export does not create addresses.
    internal static bool IsEmptyDarError(string error) =>
        error.StartsWith("MP ", StringComparison.Ordinal)
        && (error.EndsWith(": invalid DAR identifier '00000000-0000-0000-0000-000000000000' — not a valid Guid [Xellent: Addressid / ExuAddressid]", StringComparison.Ordinal)
            || error.EndsWith(": invalid DAR identifier '00000000-0000-0000-0000-000000000000' - not a valid Guid [Xellent: Addressid / ExuAddressid]", StringComparison.Ordinal));

    internal static async Task PullSettlContractSourcesAsync(StateSourceSnapshotStore store,
        ISettlContractClient contractClient, CancellationToken ct, StatePullScope? scope = null, ISettlMeteringPointClient? pointClient = null)
    {
        if (scope is not null)
        {
            if (pointClient is null) throw new InvalidOperationException("Scoped Settl contract capture requires a metering-point client.");
            var scopedLinks = await ScopedSettlContractCapture.ReadAsync(scope, pointClient, contractClient, ct);
            await store.WriteAsync("settl-contract-links", scopedLinks, ct);
            return;
        }
        var (contracts, error) = await contractClient.FindContractsAsync("Electricity", ct);
        if (error is not null || contracts is null)
            throw new InvalidOperationException($"Settl electricity contracts: {error ?? "no response"}");
        var links = new List<SettlContractLinkRow>();
        foreach (var contract in contracts)
        {
            ct.ThrowIfCancellationRequested();
            var (points, linkError) = await contractClient.GetContractMeteringPointsAsync(contract.Id, ct);
            if (linkError is not null || points is null)
                throw new InvalidOperationException($"Settl contract links for {contract.Id}: {linkError ?? "no response"}");
            links.AddRange(points.Where(p => !string.IsNullOrWhiteSpace(p.MeteringPointIdentification))
                .Select(p => new SettlContractLinkRow(p.MeteringPointIdentification!, contract.Id,
                    contract.ContractName ?? "", p.Start, p.End, p.Id)));
        }
        await store.WriteAsync("settl-contract-links", links, ct);
        Log($"  Settl electricity contract links: {links.Count} across {contracts.Count} contracts");
    }

    internal async Task<int> ReplayFromInputsAsync(GenerateStateOfflineCache.RunInputs inputs,
        GenerateStateFromSettlRunOptions options, CancellationToken ct)
    {
        _ownershipIssues = inputs.InitialOwnershipIssues;
        _deliveryStartAudit = inputs.DeliveryAudit;
        if (!options.SkipUnresolvedSourceAccounts && inputs.InitialOwnershipIssues?.Any(i =>
                i.Reason is not ("MehOnlySettlCustomer" or "MehOnlyXellentMeteringPoint")) == true)
        {
            var reportPath = SourceAccountOwnership.IssueReportPath(options.OutputPath);
            await File.WriteAllTextAsync(reportPath,
                SourceAccountOwnership.BuildIssueCsv(inputs.InitialOwnershipIssues), ct);
            Log($"ERROR: offline data has unresolved ownership; use --skip-unresolved-source-accounts " +
                $"or review {reportPath}");
            return 1;
        }
        var state = JsonSerializer.Deserialize<MigrationStateDocument>(inputs.StateSeedJson, JsonOptions)
            ?? throw new InvalidOperationException("Offline capture has no state seed.");
        var ownership = ApplyOwnershipEvidence(state, inputs.Ownership, options.Env,
            inputs.ExcludedSourceAccounts, options.SkipUnresolvedSourceAccounts);
        LogSameCvrRuleSkipped(ownership.Issues);
        if (ownership.Issues.Count > 0 && !options.SkipUnresolvedSourceAccounts)
        {
            var reportPath = SourceAccountOwnership.IssueReportPath(options.OutputPath);
            await File.WriteAllTextAsync(reportPath,
                SourceAccountOwnership.BuildIssueCsv(
                    [.. (inputs.InitialOwnershipIssues ?? []), .. ownership.Issues]), ct);
            Log($"ERROR: offline ownership rules rejected {ownership.Issues.Count} account(s); see {reportPath}");
            return 1;
        }
        _ownershipIssues = [.. (_ownershipIssues ?? []).Concat(ownership.Issues).Distinct()];
        var sourceAccounts = state.Accounts.SelectMany(a => a.SourceAccounts)
            .ToHashSet(StringComparer.Ordinal);
        if (!sourceAccounts.IsSubsetOf(inputs.QueriedSourceAccounts))
        {
            Log("ERROR: revised ownership needs invoices not present in this offline capture. Run live once.");
            return 1;
        }
        var result = await AssignInvoicesAsync(null, state, options, sourceAccounts,
            inputs.CosByCustomer, ct, inputs);
        if (result != 0) return result;
        await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(state, JsonOptions), ct);
        if (_ownershipIssues is { } issues)
            await File.WriteAllTextAsync(SourceAccountOwnership.IssueReportPath(options.OutputPath),
                SourceAccountOwnership.BuildIssueCsv(issues), ct);
        if (_deliveryStartAudit is { } audit)
            await File.WriteAllTextAsync(MoveInDeliveryFilter.ReportPath(options.OutputPath),
                MoveInDeliveryFilter.BuildCsv(audit), ct);
        if (_excludedCompanyInvoices is { } excluded)
            await File.WriteAllTextAsync(ForInvoiceScope.ReportPath(options.OutputPath),
                ForInvoiceScope.BuildCsv(excluded), ct);
        return 0;
    }

    public async Task<int> RunAsync(GenerateStateFromSettlRunOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Offline)
            _sourceSnapshot = await StateSourceSnapshotStore.Open(options.OfflineCacheDirectory,
                    options.Env ?? throw new InvalidOperationException("--offline requires --env."))
                .ReadAsync(cancellationToken);
        if (options.Offline && options.IncludeUnknownDeliveryStarts
            && !_sourceSnapshot!.IncludesUnknownDeliveryStarts)
            throw new InvalidOperationException(
                "This offline snapshot lacks Xellent and payer candidates for unknown delivery starts; run pull-state-sources again.");
        if (options.FlatReferenceDate || options.IncludeNotInDeliveryCos.HasValue)
        {
            Log("ERROR: only a confirmed first ChangeOfSupplier start can establish invoice ownership.");
            return 1;
        }
        var stopwatch = Stopwatch.StartNew();
        _ownershipIssues = null;
        _sameCvrAccepted = null;
        _ownershipTieBreaks = null;
        _historicalSourceDecisions = null;
        _excludedCompanyInvoices = null;
        _deliveryStartAudit = null;
        _ownershipEvidence = null;
        _excludedSourceAccounts = [];

        // --- Step 1: enumerate all Settl customers ---
        Console.Error.Write("  Loading customers from Settl...");
        const int pageSize = 1000;
        var customers = new List<CustomerInfoDto>();
        while (_sourceSnapshot is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, _, pageErr) = await _customerClient.FindCustomersAsync(pageSize, customers.Count, cancellationToken);
            if (pageErr != null)
            {
                Console.Error.WriteLine($"\n  FAILED at offset {customers.Count}: {pageErr}");
                return 1;
            }
            customers.AddRange(page);
            Console.Error.Write($"\r  Loading customers from Settl... {customers.Count}");
            if (page.Count < pageSize) break;
        }
        if (_sourceSnapshot is not null) customers.AddRange(_sourceSnapshot.Customers);
        Console.Error.WriteLine($"\r  Loaded {customers.Count} customers from Settl                    ");

        var accounts = customers
            .Where(c => !string.IsNullOrWhiteSpace(c.CustomerNumber))
            .Select(c => new MigratedAccount
            {
                SettlAccountNumber = c.CustomerNumber!,
                CustomerName = c.FullName ?? string.Empty,
                DeliveryCategories = ["El-ekstern"],
            })
            .OrderBy(a => a.SettlAccountNumber)
            .ToList();

        var skipped = customers.Count - accounts.Count;
        if (skipped > 0)
            Console.Error.WriteLine($"  Skipped {skipped} customers without CustomerNumber");

        var state = new MigrationStateDocument { Accounts = accounts };
        // Taken now: enrichment drops customers from the state that still exist in Settl.
        _settlCustomerNumbers = accounts.Select(a => a.SettlAccountNumber).ToHashSet(StringComparer.Ordinal);
        var customerNumbersById = customers
            .Where(c => c.Id != Guid.Empty && !string.IsNullOrWhiteSpace(c.CustomerNumber))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().CustomerNumber!, EqualityComparer<Guid>.Default);

        // --- Step 2: enrich with SourceAccounts + Invoices from Xellent ---
        if (_sourceSnapshot is null && string.IsNullOrEmpty(options.ConnectionString))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: invoice enrichment requires a Xellent connection string.");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("--- Step 2: Xellent invoice enrichment, first Settl supplier change ---");
        int exitCode;
        exitCode = await EnrichCosDrivenAsync(state, customerNumbersById, options, cancellationToken);
        if (_ownershipIssues is { } issues)
        {
            var issueReportPath = SourceAccountOwnership.IssueReportPath(options.OutputPath);
            await File.WriteAllTextAsync(
                issueReportPath,
                SourceAccountOwnership.BuildIssueCsv(issues),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);
            Log($"  Ownership issue report: {issueReportPath} ({issues.Count} row(s))");
        }
        if (_sameCvrAccepted is { } sameCvrAccepted)
        {
            var sameCvrReportPath = SourceAccountOwnership.SameCvrReportPath(options.OutputPath);
            await File.WriteAllTextAsync(sameCvrReportPath,
                SourceAccountOwnership.BuildSameCvrCsv(sameCvrAccepted),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            Log($"  Same-CVR acceptance report: {sameCvrReportPath} ({sameCvrAccepted.Count} row(s))");
        }
        if (_ownershipTieBreaks is { } ownershipTieBreaks)
        {
            var tieBreakReportPath = SourceAccountOwnership.TieBreakReportPath(options.OutputPath);
            await File.WriteAllTextAsync(tieBreakReportPath,
                SourceAccountOwnership.BuildTieBreakCsv(ownershipTieBreaks),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            Log($"  Ownership tie-break report: {tieBreakReportPath} ({ownershipTieBreaks.Count} row(s))");
        }
        if (_historicalSourceDecisions is { } historicalDecisions)
        {
            var historicalReportPath = HistoricalSourceOwnership.ReportPath(options.OutputPath);
            await File.WriteAllTextAsync(historicalReportPath,
                HistoricalSourceOwnership.BuildCsv(historicalDecisions),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            Log($"  Historical source report: {historicalReportPath} ({historicalDecisions.Count} row(s))");
        }
        if (_excludedCompanyInvoices is { } excludedCompanyInvoices)
        {
            var excludedReportPath = ForInvoiceScope.ReportPath(options.OutputPath);
            await File.WriteAllTextAsync(excludedReportPath,
                ForInvoiceScope.BuildCsv(excludedCompanyInvoices),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            Log($"  Excluded invoice report: {excludedReportPath} ({excludedCompanyInvoices.Count} row(s))");
        }
        if (_deliveryStartAudit is { } deliveryStartAudit)
        {
            var reportPath = MoveInDeliveryFilter.ReportPath(options.OutputPath);
            await File.WriteAllTextAsync(reportPath, MoveInDeliveryFilter.BuildCsv(deliveryStartAudit),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            Log($"  Delivery start audit: {reportPath} ({deliveryStartAudit.Count} row(s))");
        }
        if (exitCode != 0) return exitCode;

        var updatedJson = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(options.OutputPath, updatedJson, cancellationToken);

        stopwatch.Stop();
        Console.ForegroundColor = ConsoleColor.Green;
        if (options.NoInvoices)
        {
            Console.WriteLine($"Done in {stopwatch.Elapsed}. {state.Accounts.Count} accounts with source accounts, " +
                $"no invoices → {options.OutputPath}");
        }
        else
        {
            var withInvoices = state.Accounts.Count(a => a.Invoices.Count > 0);
            Console.WriteLine($"Done in {stopwatch.Elapsed}. {withInvoices}/{state.Accounts.Count} accounts have invoices → {options.OutputPath}");
        }
        Console.ResetColor();
        var omittedMeteringPoints = _ownershipIssues?.Count(i => i.Reason == "NoSettlCustomerForMeteringPoint") ?? 0;
        if (omittedMeteringPoints > 0)
            Log($"  {omittedMeteringPoints} metering point(s) omitted for missing Settl customer links; " +
                "review the ownership-issues CSV before uploading PDFs.");
        var missingXellentMeteringPoints = _ownershipIssues?.Count(i => i.Reason == "NoXellentAccountAtCos") ?? 0;
        if (missingXellentMeteringPoints > 0)
            Log($"  {missingXellentMeteringPoints} metering point(s) omitted for missing Xellent accounts at COS-1; " +
                "review the ownership-issues CSV before uploading PDFs.");
        var unresolvedSourceAccounts = _ownershipIssues?.Count(i =>
            i.Reason == "NoUniqueCosMatchedSettlCustomerForSourceAccount") ?? 0;
        if (options.SkipUnresolvedSourceAccounts && unresolvedSourceAccounts > 0)
            Log($"  {unresolvedSourceAccounts} unresolved Xellent source account(s) and their invoices omitted; " +
                "review the ownership-issues CSV before uploading PDFs.");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------
    // COS-driven enrichment (default)
    // ---------------------------------------------------------------------------------------------

    private async Task<int> EnrichCosDrivenAsync(
        MigrationStateDocument state,
        IReadOnlyDictionary<Guid, string> customerNumbersById,
        GenerateStateFromSettlRunOptions options,
        CancellationToken ct)
    {
        if (_sourceSnapshot is null && (_meteringPointClient is null || _payerClient is null || _customerLinkLookup is null))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: COS-driven enrichment needs a metering point client, a payer client and a customer link lookup.");
            Console.ResetColor();
            return 1;
        }

        // --- 2a: which metering points are in scope, and when did each change supplier ---
        Log(_sourceSnapshot is null
            ? "  Loading electricity metering points from Settl..."
            : "  Reading saved Settl electricity metering points...");
        var allMps = _sourceSnapshot is { } snapshot
            ? snapshot.MeteringPoints.ToDictionary(m => m.Identification, StringComparer.Ordinal)
            : await new SettlMeteringPointCosLoader(_meteringPointClient!, Log)
                .LoadAsync(new CosPeriod(null, null), ct,
                    includeNotInDelivery: true, commodity: CommodityFilter.Electricity);
        // Use the list's delivery evidence only as a cheap population filter. The actual
        // migration date is always taken from the first historical delivery status below.
        List<SettlMpWithCos> inScope = [.. allMps.Values.Where(m => CosStatePlanner.WasEverInDelivery(m.Info))];
        Log($"  Metering points to inspect for first delivery: {inScope.Count}");
        if (inScope.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: no metering point in Settl is in scope — nothing to enrich.");
            Console.ResetColor();
            return 1;
        }

        // The list API is only a population filter. The status history supplies the first start
        // and its reason; a later start cannot replace the migration event.
        var savedDeliveryById = _sourceSnapshot?.DeliveryHistories.ToDictionary(h => h.Id);
        if (savedDeliveryById is not null && inScope.Any(m => !savedDeliveryById.ContainsKey(m.Id)))
            throw new InvalidOperationException("Offline source data is missing a Settl delivery history.");
        var deliveryFilter = await MoveInDeliveryFilter.FilterAsync(
            inScope,
            async (id, token) =>
            {
                GetDeliveryStatusesResponse? statuses;
                string? error;
                try
                {
                    if (savedDeliveryById is not null)
                    {
                        if (!savedDeliveryById.TryGetValue(id, out var row))
                            throw new InvalidOperationException($"Missing saved delivery history for {id}.");
                        (statuses, error) = (row.Statuses, row.Error);
                    }
                    else (statuses, error) = await _meteringPointClient!.GetDeliveryStatusesAsync(id, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { statuses = null; error = ex.Message; }
                return (statuses, error);
            }, ct,
            (done, total) => Log($"    delivery start lookup: {done}/{total}"),
            includeUnknownFirstStarts: options.IncludeUnknownDeliveryStarts);
        inScope = [.. deliveryFilter.Kept];
        _deliveryStartAudit = deliveryFilter.Audit;
        Log($"  Accepted {inScope.Count} first delivery starts" +
            (options.IncludeUnknownDeliveryStarts ? $" (including {deliveryFilter.IncludedUnknown} with unknown reason)" : "") + "; " +
            $"excluded {deliveryFilter.Audit.Count - deliveryFilter.IncludedUnknown} other or unverified first starts");
        if (inScope.Count == 0)
        {
            Log("ERROR: no metering point has an allowed first delivery start.");
            return 1;
        }

        LogHistogram("Metering points per change-of-supplier date", inScope.Select(m => m.Cos));

        // Only MPs with an el-ekstern FOR (or legacy blank-company) delpoint in Xellent can
        // contribute FOR invoice source accounts. Settl-native and MEH-only MPs have no FOR
        // Xellent invoice owner to assign, so exclude them before requiring a payer link or
        // calculating each customer's COS window. The resolver also supplies the COS-1 contracts
        // used below; do this once for the whole inspected population.
        Log($"  Checking FOR Xellent origin for {inScope.Count} Settl metering point(s)...");
        var allCosByMp = inScope.ToDictionary(m => m.Identification, m => m.Cos,
            StringComparer.Ordinal);
        var mpResolution = _sourceSnapshot is { } savedSources
            ? new MeteringPointAccountResolver.MpAccountResolution(
                savedSources.XellentMpAccounts.Select(p => (p.MeteringPoint, p.Account)).ToArray(),
                savedSources.ForXellentMeteringPoints.ToHashSet(StringComparer.Ordinal),
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                new HashSet<string>(), new Dictionary<string, MeteringPointAccountResolver.OwnerContract>())
            : await MeteringPointAccountResolver.ResolveAsync(
                options.ConnectionString!, allCosByMp, MigrationConstants.DataAreaId,
                ct: ct, companyId: InvoiceCompanyId, includeBlankCompany: true);
        var (forMps, scopeAudit) = FilterForXellentOrigin(
            inScope, mpResolution.XellentMpIds, deliveryFilter.Audit);
        _deliveryStartAudit = scopeAudit;
        var withoutForOriginCount = inScope.Count - forMps.Count;
        inScope = forMps;
        Log($"  FOR Xellent origin: {inScope.Count} metering point(s); " +
            $"excluded {withoutForOriginCount} without a FOR Xellent delpoint " +
            "(listed in delivery-start-audit CSV)");
        if (inScope.Count == 0)
        {
            Log("ERROR: no first ChangeOfSupplier metering point has a FOR Xellent origin.");
            return 1;
        }

        // --- 2b: who held each metering point at COS (metering point → payer link → customer number) ---
        var settlLinkIssues = new List<SourceAccountOwnershipIssue>();
        var customerByMpGuid = await ResolveCustomerByMeteringPointAsync(
            inScope, customerNumbersById, ct, settlLinkIssues);
        var resolvedByLink = await ResolveMissingCustomersByLinkAsync(inScope, customerNumbersById,
            customerByMpGuid, settlLinkIssues, _sourceSnapshot is not null, ct);
        var resolvedToday = await ResolveMissingCustomersTodayAsync(inScope, customerNumbersById,
            customerByMpGuid, settlLinkIssues, _sourceSnapshot is not null, ct);
        var unlinkedMps = inScope.Where(m => !customerByMpGuid.ContainsKey(m.Id)).ToList();
        var sourceAccountsByMp = mpResolution.Pairs
            .GroupBy(p => p.MpId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => string.Join('|', g.Select(p => p.Account).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)), StringComparer.Ordinal);
        var cosByMpId = inScope.ToDictionary(m => m.Identification, m => m.Cos,
            StringComparer.Ordinal);
        settlLinkIssues = [.. settlLinkIssues.Select(i => i with
        {
            SourceAccount = sourceAccountsByMp.GetValueOrDefault(i.MeteringPoint) ?? i.SourceAccount,
            CosDate = cosByMpId.GetValueOrDefault(i.MeteringPoint)
        })];
        var unlinkedIssues = unlinkedMps
            .Select(m => new SourceAccountOwnershipIssue(
                "NoSettlCustomerForMeteringPoint",
                sourceAccountsByMp.GetValueOrDefault(m.Identification) ?? "",
                m.Identification, "", m.Cos))
            .ToList();
        _ownershipIssues = unlinkedIssues;
        if (unlinkedMps.Count > 0 || settlLinkIssues.Count > 0)
        {
            _ownershipIssues = [.. settlLinkIssues, .. unlinkedIssues];
            var blockedMeteringPoints = unlinkedMps.Select(m => m.Identification)
                .Concat(settlLinkIssues.Select(i => i.MeteringPoint))
                .ToHashSet(StringComparer.Ordinal);
            if (!options.SkipUnresolvedSourceAccounts)
            {
                Log($"ERROR: {blockedMeteringPoints.Count} metering point(s) have no clear six-digit Settl customer number " +
                    $"({unlinkedMps.Count} without a resolved customer, {settlLinkIssues.Count} link issue row(s)); " +
                    "refusing to choose an invoice owner.");
                return 1;
            }
            var omission = OmitUnresolvedMeteringPoints(inScope,
                blockedMeteringPoints, mpResolution.Pairs);
            inScope = omission.Kept;
            _excludedSourceAccounts = omission.ExcludedSources;
            _ownershipIssues = [.. _ownershipIssues, .. omission.SharedSourceIssues];
            Log($"  --skip-unresolved-source-accounts: omitted {omission.OmittedCount} " +
                $"metering point(s) and {_excludedSourceAccounts.Count} Xellent source account(s) " +
                "whose Settl owner is unclear; see ownership-issues CSV");
            if (inScope.Count == 0)
            {
                Log("ERROR: no metering point with a verified Settl customer remains.");
                return 1;
            }
        }
        var cosByCustomer = CosStatePlanner.MinCosByCustomer(
            inScope
                .Where(m => customerByMpGuid.ContainsKey(m.Id))
                .Select(m => (customerByMpGuid[m.Id], m.Cos)));
        Log($"  Settl named {cosByCustomer.Count} distinct customer(s) behind those metering points");
        LogHistogram("Customers per change-of-supplier date", cosByCustomer.Values);

        // --- 2c: drop everyone Settl does not hold a migrated metering point for ---
        var excluded = state.Accounts.Where(a => !cosByCustomer.ContainsKey(a.SettlAccountNumber)).ToList();
        state.Accounts = [.. state.Accounts.Where(a => cosByCustomer.ContainsKey(a.SettlAccountNumber))];
        Log($"  Excluded {excluded.Count} Settl customer(s) with no migrated electricity metering point — " +
            $"{state.Accounts.Count} migrated customer(s) remain");

        // Settl production holds a handful of customer numbers twice, on two different customer
        // GUIDs. The state file is keyed on the customer number and PdfExportCli uploads to
        // hol/{customerNumber}/, so two entries would be two runs at the same blob folder — the
        // second one skipped by the manifest marker, which makes which entry won a matter of order.
        // One entry per number, the first in the sorted order, and the collision is named.
        var duplicates = state.Accounts
            .GroupBy(a => a.SettlAccountNumber, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count > 0)
        {
            state.Accounts = [.. state.Accounts
                .GroupBy(a => a.SettlAccountNumber, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(a => a.SettlAccountNumber, StringComparer.Ordinal)];
            Log($"  WARNING: {duplicates.Count} customer number(s) appear more than once in Settl — " +
                $"one entry kept for each: {string.Join(", ", duplicates.Take(20))}" +
                (duplicates.Count > 20 ? $" (+{duplicates.Count - 20} more)" : ""));
        }

        if (!options.SkipWaterCensus && _sourceSnapshot is null)
            await ReportWaterOnlyCustomersAsync(excluded, customerNumbersById, ct);

        if (state.Accounts.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: no migrated customer left after exclusion — refusing to write an empty state file.");
            Console.ResetColor();
            return 1;
        }

        // --- 2d: Xellent ---
        XellentDbContext? context = null;
        if (_sourceSnapshot is null)
        {
            var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
                .UseSqlServer(options.ConnectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(300); })
                .Options;
            context = new XellentDbContext(dbOptions);
            Log("  Testing database connection...");
            await context.Database.CanConnectAsync(ct);
        }
        await using var contextLease = context;

        // Each Settl MP has a customer through its payer. Resolve that same MP's Xellent contract
        // account at COS-1, then group the resulting source accounts under the Settl customer.
        // Unlike the old CVR/merge-group reconstruction, this never guesses ownership from a
        // representative or silently keeps the first of several claims.
        var linkedMps = inScope.Where(m => customerByMpGuid.ContainsKey(m.Id)).ToList();
        var settlCustomerByMp = linkedMps.ToDictionary(
            m => m.Identification,
            m => customerByMpGuid[m.Id],
            StringComparer.Ordinal);
        var cosByMp = linkedMps.ToDictionary(m => m.Identification, m => m.Cos, StringComparer.Ordinal);
        var noHistoryMpIds = linkedMps
            .Where(m => options.IncludeNotInDeliveryCos.HasValue && m.Info.InDelivery != true)
            .Select(m => m.Identification)
            .ToHashSet(StringComparer.Ordinal);

        Log($"  Using {(_sourceSnapshot is null ? "COS-1 Xellent" : "saved COS-1 Xellent")} " +
            $"account resolution for {linkedMps.Count} FOR metering point(s)...");
        var mehMpIds = _sourceSnapshot is { } savedMeh
            ? savedMeh.MehXellentMeteringPoints.ToHashSet(StringComparer.Ordinal)
            : await LoadMehMeteringPointIdsAsync(context!, settlCustomerByMp.Keys, ct);
        var companyScope = SettlCustomerCompanyScope.Select(
            settlCustomerByMp, mpResolution.XellentMpIds, mehMpIds);
        state.Accounts = [.. state.Accounts.Where(a =>
            !companyScope.ExcludedCustomers.Contains(a.SettlAccountNumber))];
        foreach (var mp in settlCustomerByMp.Where(kv => companyScope.ExcludedCustomers.Contains(kv.Value))
            .Select(kv => kv.Key).Concat(companyScope.OmittedMeteringPoints).Distinct(StringComparer.Ordinal).ToArray())
        {
            settlCustomerByMp.Remove(mp);
            noHistoryMpIds.Remove(mp);
        }
        if (companyScope.ExcludedCustomers.Count > 0 || companyScope.OmittedMeteringPoints.Count > 0)
            Log($"  Excluded {companyScope.ExcludedCustomers.Count} MEH-only Settl customer(s) and " +
                $"{companyScope.OmittedMeteringPoints.Count} MEH-only metering point(s); " +
                "see ownership-issues CSV");
        if (state.Accounts.Count == 0)
        {
            _ownershipIssues = [.. unlinkedIssues, .. companyScope.Issues];
            Log("ERROR: no FOR-linked Settl customer remains after company scoping.");
            return 1;
        }
        var ownership = _sourceSnapshot is { } savedOwnership
            ? ResolveInvoiceSourcesFromSnapshot(state, settlCustomerByMp, cosByMp,
                mpResolution.Pairs, savedOwnership, options)
            : await ResolveInvoiceSourcesAsync(
                context!, state, settlCustomerByMp, cosByMp, mpResolution.Pairs, options, ct);
        _ownershipIssues = [.. (_ownershipIssues ?? []), .. companyScope.Issues, .. ownership.Issues];
        if (ownership.SameCvrAccepted is { } acceptedBySameCvr)
        {
            _sameCvrAccepted = acceptedBySameCvr;
            Log($"  Same-CVR rule accepted {acceptedBySameCvr.Count} metering point(s) whose Settl customer " +
                "does not match the Xellent account or its representative");
        }
        if (ownership.TieBreaks is { } tieBreaks)
        {
            _ownershipTieBreaks = tieBreaks;
            Log($"  Ownership tie-break gave {tieBreaks.Count} account(s) claimed by several same-CVR " +
                "Settl customers to one of them");
        }
        if (ownership.Issues.Count > 0 && !options.SkipUnresolvedSourceAccounts)
        {
            Log($"ERROR: {ownership.Issues.Count} Xellent account(s) do not have a unique COS-1 owner " +
                "matching the dated Settl customer; review the ownership-issues CSV.");
            return 1;
        }
        if (ownership.Issues.Count > 0)
            Log($"  --skip-unresolved-source-accounts: omitted {ownership.Issues.Count} " +
                "unresolved ownership claim(s); see ownership-issues CSV");

        // A customer found without a payer at COS that failed the COS-1 check must not stay in the
        // state or pull another of its metering points' invoice floor back to this COS.
        var rejectedFallback = ownership.Issues.Select(i => i.MeteringPoint)
            .Where(m => resolvedByLink.Contains(m) || resolvedToday.Contains(m))
            .ToHashSet(StringComparer.Ordinal);
        if (rejectedFallback.Count > 0)
        {
            cosByCustomer = MinCosWithoutRejectedTodayPoints(inScope, customerByMpGuid, rejectedFallback);
            state.Accounts = [.. state.Accounts.Where(a => cosByCustomer.ContainsKey(a.SettlAccountNumber))];
            Log($"  Rejected {rejectedFallback.Count} metering point(s) resolved without a payer at COS; " +
                "see ownership-issues CSV");
        }

        var missingXellentIssues = ownership.Issues
            .Where(i => i.Reason == "NoXellentAccountAtCos").ToList();
        var skippedUnresolvedIssues = options.SkipUnresolvedSourceAccounts
            ? ownership.Issues.Where(i => i.Reason == "NoUniqueCosMatchedSettlCustomerForSourceAccount").ToList()
            : [];
        var blockingIssues = ownership.Issues
            .Where(i => i.Reason != "NoXellentAccountAtCos"
                && (!options.SkipUnresolvedSourceAccounts
                    || i.Reason != "NoUniqueCosMatchedSettlCustomerForSourceAccount")).ToList();
        foreach (var mpId in noHistoryMpIds.Order(StringComparer.Ordinal))
            blockingIssues.Add(new SourceAccountOwnershipIssue(
                "NoHistoricalCosForMeteringPoint",
                string.Join('|', mpResolution.Pairs.Where(p => p.MpId == mpId).Select(p => p.Account).Distinct()),
                mpId,
                settlCustomerByMp[mpId]));
        _ownershipIssues = [.. (_ownershipIssues ?? []), .. blockingIssues
            .Where(i => !ownership.Issues.Contains(i))];
        Log($"  Directly mapped: {ownership.MappedMeteringPoints} metering point(s), " +
            $"{ownership.MappedSourceAccounts} Xellent source account(s); " +
            $"{ownership.CosResolvedSourceAccounts} shared source account(s) resolved by COS");
        if (missingXellentIssues.Count > 0 && options.NoInvoices)
            Log($"  WARNING: {missingXellentIssues.Count} metering point(s) have no Xellent account at COS-1 " +
                "and will be omitted; their IDs will be listed in the ownership-issues CSV");
        if (skippedUnresolvedIssues.Count > 0 && options.NoInvoices)
            Log($"  WARNING: {skippedUnresolvedIssues.Count} shared Xellent source account(s) have no unique " +
                "COS-matched Settl customer and will be omitted with their invoices; " +
                "their IDs will be listed in the ownership-issues CSV");
        if (blockingIssues.Count > 0 && options.NoInvoices
            && !options.SkipUnresolvedSourceAccounts)
        {
            Log($"ERROR: {blockingIssues.Count} blocking ownership issue(s); refusing to write an invoice state file. " +
                "Review the ownership-issues CSV.");
            return 1;
        }

        var allSourceAccounts = state.Accounts.SelectMany(a => a.SourceAccounts)
            .ToHashSet(StringComparer.Ordinal);
        Log($"  Source accounts total: {allSourceAccounts.Count}");

        // --- 2g: approved source-account owner and customer-specific invoice history floor ---
        return await AssignInvoicesAsync(context, state, options, allSourceAccounts, cosByCustomer, ct);
    }

    internal static (List<SettlMpWithCos> Included, IReadOnlyList<DeliveryStartAudit> Audit)
        FilterForXellentOrigin(IReadOnlyList<SettlMpWithCos> points,
            IReadOnlySet<string> forXellentMpIds,
            IReadOnlyList<DeliveryStartAudit> deliveryAudit)
    {
        var included = points.Where(m => forXellentMpIds.Contains(m.Identification)).ToList();
        var excluded = points.Where(m => !forXellentMpIds.Contains(m.Identification));
        var audit = deliveryAudit.Concat(excluded.Select(m => new DeliveryStartAudit(
                m.Identification, m.Cos, "NoForXellentOrigin")))
            .OrderBy(a => a.MeteringPoint, StringComparer.Ordinal).ToArray();
        return (included, audit);
    }

    internal sealed record MeteringPointOmission(List<SettlMpWithCos> Kept,
        IReadOnlyList<string> ExcludedSources,
        IReadOnlyList<SourceAccountOwnershipIssue> SharedSourceIssues,
        int OmittedCount);

    internal static MeteringPointOmission OmitUnresolvedMeteringPoints(
        IReadOnlyList<SettlMpWithCos> points,
        IReadOnlySet<string> unresolvedMeteringPoints,
        IReadOnlyCollection<(string MpId, string Account)> xellentPairs)
    {
        var excludedSources = xellentPairs
            .Where(p => unresolvedMeteringPoints.Contains(p.MpId))
            .Select(p => p.Account.Trim())
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var affectedMeteringPoints = xellentPairs
            .Where(p => excludedSources.Contains(p.Account.Trim()))
            .Select(p => p.MpId)
            .ToHashSet(StringComparer.Ordinal);
        affectedMeteringPoints.UnionWith(unresolvedMeteringPoints);
        var kept = points.Where(p => !affectedMeteringPoints.Contains(p.Identification)).ToList();
        var sharedSourceIssues = points
            .Where(p => affectedMeteringPoints.Contains(p.Identification)
                && !unresolvedMeteringPoints.Contains(p.Identification))
            .Select(p => new SourceAccountOwnershipIssue(
                "SharesXellentAccountWithUnresolvedMeteringPoint",
                string.Join('|', xellentPairs
                    .Where(x => x.MpId == p.Identification && excludedSources.Contains(x.Account.Trim()))
                    .Select(x => x.Account.Trim()).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)),
                p.Identification, "", p.Cos))
            .ToArray();
        return new(kept, [.. excludedSources.Order(StringComparer.Ordinal)],
            sharedSourceIssues, points.Count - kept.Count);
    }

    private async Task<SourceAccountOwnershipResult> ResolveInvoiceSourcesAsync(
        XellentDbContext context, MigrationStateDocument state,
        IReadOnlyDictionary<string, string> customerByMp,
        IReadOnlyDictionary<string, DateOnly> cosByMp,
        IReadOnlyCollection<(string MpId, string Account)> pairs,
        GenerateStateFromSettlRunOptions options, CancellationToken ct)
    {
        var issues = new List<SourceAccountOwnershipIssue>();
        var unique = pairs.GroupBy(p => p.MpId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Account.Trim())
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var seeds = new List<(string Mp, string Source, string CustomerNumber, DateOnly Cos)>();
        foreach (var (mp, customerNumber) in customerByMp)
        {
            if (!unique.TryGetValue(mp, out var sources) || sources.Length != 1)
            {
                issues.Add(new SourceAccountOwnershipIssue(
                    sources is null ? "NoXellentAccountAtCos" : "MultipleXellentAccountsAtCos",
                    string.Join('|', sources ?? []), mp, customerNumber));
                continue;
            }
            seeds.Add((mp, sources[0], customerNumber, cosByMp[mp]));
        }
        if (issues.Count > 0 && !options.SkipUnresolvedSourceAccounts)
            return new(0, 0, 0, issues);
        if (issues.Count > 0)
        {
            _excludedSourceAccounts = [.. _excludedSourceAccounts
                .Concat(issues.SelectMany(i => i.SourceAccount.Split('|',
                    StringSplitOptions.RemoveEmptyEntries)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        // Include the full CVR cohort before calling the same representative resolver as migration.
        // Its fingerprint and DAR rules decide the actual group; CVR alone never merges accounts.
        var cvrByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in seeds.Select(s => s.Source).Distinct(StringComparer.Ordinal).Chunk(1500))
        {
            var rows = await context.CustTables.Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 }).ToListAsync(ct);
            foreach (var row in rows) cvrByAccount[row.Accountnum.Trim()] = row.ExuCvrDisponent1?.Trim() ?? "";
        }
        var accountsByCvr = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var chunk in cvrByAccount.Values.Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal).Chunk(1500))
        {
            var rows = await context.CustTables.Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && c.ExuCvrDisponent1 != null && chunk.Contains(c.ExuCvrDisponent1))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 }).ToListAsync(ct);
            foreach (var row in rows)
            {
                var cvr = row.ExuCvrDisponent1!.Trim();
                if (!accountsByCvr.TryGetValue(cvr, out var members))
                    accountsByCvr[cvr] = members = [];
                members.Add(row.Accountnum.Trim());
            }
        }
        var forAccounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in accountsByCvr.Values.SelectMany(v => v)
            .Distinct(StringComparer.Ordinal).Chunk(1500))
        {
            var rows = await context.ExuContractTables.Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && (c.Companyid == InvoiceCompanyId || c.Companyid == "")
                    && chunk.Contains(c.Custaccount))
                .Select(c => c.Custaccount).Distinct().ToListAsync(ct);
            foreach (var account in rows) forAccounts.Add(account.Trim());
        }
        foreach (var cvr in accountsByCvr.Keys.ToArray())
            accountsByCvr[cvr] = accountsByCvr[cvr].Where(forAccounts.Contains).ToList();

        var evidence = new List<GenerateStateOfflineCache.OwnershipEvidence>();
        foreach (var dateGroup in seeds.GroupBy(s => s.Cos))
        {
            var sources = dateGroup.Select(s => s.Source).Distinct(StringComparer.Ordinal).ToArray();
            var portions = CosStatePlanner.BuildCvrPortions(
                sources, cvrByAccount, accountsByCvr, HistoricalResolvePortionSize);
            var representativeBySource = new Dictionary<string, string>(StringComparer.Ordinal);
            var membersByRepresentative = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var portion in portions)
            {
                var resolution = await AccountRepresentativeResolver.ResolveAsync(
                    context, portion.Accounts, MigrationConstants.DataAreaId,
                    referenceDate: dateGroup.Key.AddDays(-1), cancellationToken: ct,
                    ignoredGroupNumbers: IgnoredGroupsLoader.ToSet(_ignoredGroups));
                foreach (var (source, representative) in resolution.RepresentativeByAccount)
                    representativeBySource[source] = representative;
                foreach (var group in resolution.RepresentativeByAccount.GroupBy(p => p.Value, StringComparer.Ordinal))
                    membersByRepresentative[group.Key] = group.Select(p => p.Key).ToArray();
            }
            foreach (var seed in dateGroup)
            {
                var representative = representativeBySource.GetValueOrDefault(seed.Source, seed.Source);
                evidence.Add(new GenerateStateOfflineCache.OwnershipEvidence(
                    seed.Mp, seed.CustomerNumber, seed.Source, representative, seed.Cos,
                    membersByRepresentative.GetValueOrDefault(representative) ?? [seed.Source]));
            }
        }
        _ownershipEvidence = evidence;

        // The same-CVR rule also needs the CVR of the account behind each Settl customer number,
        // and the ownership tie-break the CVR of every group member a customer claims.
        foreach (var chunk in evidence
            .SelectMany(e => CustomerNumberPolicy.AccountCandidates(e.SettlCustomerNumber, options.Env)
                .Concat(e.RepresentativeMembers))
            .Where(a => !cvrByAccount.ContainsKey(a))
            .Distinct(StringComparer.Ordinal).Chunk(1500))
        {
            var rows = await context.CustTables.Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 }).ToListAsync(ct);
            foreach (var row in rows) cvrByAccount[row.Accountnum.Trim()] = row.ExuCvrDisponent1?.Trim() ?? "";
        }
        var applied = ApplyOwnershipEvidence(state, evidence, options.Env,
            _excludedSourceAccounts, options.SkipUnresolvedSourceAccounts, cvrByAccount, _settlCustomerNumbers);
        return applied with { Issues = [.. issues, .. applied.Issues] };
    }

    private SourceAccountOwnershipResult ResolveInvoiceSourcesFromSnapshot(
        MigrationStateDocument state,
        IReadOnlyDictionary<string, string> customerByMp,
        IReadOnlyDictionary<string, DateOnly> cosByMp,
        IReadOnlyCollection<(string MpId, string Account)> pairs,
        StateSourceSnapshot snapshot,
        GenerateStateFromSettlRunOptions options)
    {
        var issues = new List<SourceAccountOwnershipIssue>();
        var byMp = pairs.GroupBy(p => p.MpId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Account.Trim())
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var evidenceByKey = snapshot.XellentEvidence.ToDictionary(
            e => (e.MeteringPoint, e.SourceAccount, e.Cos));
        var evidence = new List<GenerateStateOfflineCache.OwnershipEvidence>();
        foreach (var (mp, customer) in customerByMp)
        {
            if (!byMp.TryGetValue(mp, out var sources) || sources.Length != 1)
            {
                issues.Add(new SourceAccountOwnershipIssue(
                    sources is null ? "NoXellentAccountAtCos" : "MultipleXellentAccountsAtCos",
                    string.Join('|', sources ?? []), mp, customer));
                continue;
            }
            if (!evidenceByKey.TryGetValue((mp, sources[0], cosByMp[mp]), out var saved))
                throw new InvalidOperationException($"Missing saved Xellent representative for {mp}/{sources[0]}.");
            evidence.Add(new GenerateStateOfflineCache.OwnershipEvidence(mp, customer,
                sources[0], saved.Representative, saved.Cos, saved.RepresentativeMembers));
        }
        if (issues.Count > 0 && !options.SkipUnresolvedSourceAccounts)
            return new(0, 0, 0, issues);
        if (issues.Count > 0)
            _excludedSourceAccounts = [.. _excludedSourceAccounts
                .Concat(issues.SelectMany(i => i.SourceAccount.Split('|', StringSplitOptions.RemoveEmptyEntries)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        _ownershipEvidence = evidence;
        var applied = ApplyOwnershipEvidence(state, evidence, options.Env,
            _excludedSourceAccounts, options.SkipUnresolvedSourceAccounts);
        LogSameCvrRuleSkipped(applied.Issues);
        return applied with { Issues = [.. issues, .. applied.Issues] };
    }

    private static void LogSameCvrRuleSkipped(IReadOnlyList<SourceAccountOwnershipIssue> issues)
    {
        var mismatches = issues.Count(i => i.Reason == "XellentAccountDoesNotMatchSettlCustomer");
        if (mismatches > 0)
            Log($"  Offline: skipped the same-CVR rule for {mismatches} rejected metering point(s); " +
                "saved source data does not hold CVRs");
        var conflicts = issues.Count(i => i.Reason == "XellentSourceClaimedByMultipleCustomers");
        if (conflicts > 0)
            Log($"  Offline: skipped the same-CVR ownership tie-break for {conflicts} account(s) " +
                "claimed by several customers; saved source data does not hold CVRs");
    }

    /// <param name="cvrByAccount">Xellent account → CVR. Null means the CVRs are not available and
    /// the same-CVR rule does not run.</param>
    /// <param name="settlCustomers">Every customer number in Settl, including those the state
    /// file leaves out. Defaults to the customers in <paramref name="state"/>. Only used to find which
    /// customers validly claim their own account; only those keep other customers off it.</param>
    internal static SourceAccountOwnershipResult ApplyOwnershipEvidence(
        MigrationStateDocument state,
        IReadOnlyList<GenerateStateOfflineCache.OwnershipEvidence> evidence,
        string? env,
        IReadOnlyCollection<string>? excludedSources = null,
        bool skipUnresolvedSources = false,
        IReadOnlyDictionary<string, string>? cvrByAccount = null,
        IReadOnlySet<string>? settlCustomers = null)
    {
        var issues = new List<SourceAccountOwnershipIssue>();
        var ownerBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimsBySource = new Dictionary<string, List<SourceClaim>>(StringComparer.Ordinal);
        var conflictBySource = new Dictionary<string, SourceAccountOwnershipIssue>(StringComparer.Ordinal);
        var knownCustomers = state.Accounts.Select(a => a.SettlAccountNumber)
            .ToHashSet(StringComparer.Ordinal);
        var representativeMatches = 0;
        var sameCvrRows = new List<GenerateStateOfflineCache.OwnershipEvidence>();

        // Matches a row and lists the accounts it vouches for. Null match: the account number is invalid.
        // An account in ownClaimants is a Settl customer under its own number, and that customer claims it.
        (InvoiceSourceMatch.Kind? Match, string[] Approved) Evaluate(
            GenerateStateOfflineCache.OwnershipEvidence row, IReadOnlySet<string> ownClaimants)
        {
            InvoiceSourceMatch.Kind match;
            try { match = InvoiceSourceMatch.Match(row.SettlCustomerNumber, row.SourceAccount, row.Representative, env); }
            catch (InvalidOperationException) { return (null, []); }
            if (match == InvoiceSourceMatch.Kind.None && cvrByAccount is not null
                && InvoiceSourceMatch.SameCvr(row.SettlCustomerNumber, row.SourceAccount, cvrByAccount,
                    ownClaimants, env))
                match = InvoiceSourceMatch.Kind.SameCvr;
            // Today's resolver can group a source account with another customer's representative
            // even though the account became its own Settl customer at migration. That group is
            // no match for the account, the same as under the same-CVR rule.
            if (match == InvoiceSourceMatch.Kind.Representative && InvoiceSourceMatch.IsOtherSettlCustomer(
                    row.SourceAccount, row.SettlCustomerNumber, ownClaimants, env))
                match = InvoiceSourceMatch.Kind.None;
            if (match == InvoiceSourceMatch.Kind.None || !knownCustomers.Contains(row.SettlCustomerNumber))
                return (match, []);
            // Today's representative group is what disagrees with Settl here, so a same-CVR
            // acceptance vouches for the source account alone, not for its group.
            // A group vouches for all its members except those that are a Settl customer under
            // their own number: they were not merged into anyone at migration.
            string[] approved = match is InvoiceSourceMatch.Kind.Direct or InvoiceSourceMatch.Kind.SameCvr
                ? [row.SourceAccount]
                : row.RepresentativeMembers.Where(m => !InvoiceSourceMatch.IsOtherSettlCustomer(
                    m, row.SettlCustomerNumber, ownClaimants, env)).ToArray();
            return (match, approved);
        }

        // An account that is a Settl customer itself only keeps other customers off when that
        // customer holds a valid claim on it in this run. Otherwise nobody would own the account,
        // so the other claims stand. Valid claims are found with every Settl customer guarded:
        // guarding fewer accounts only adds claims, so these stay valid in the real pass.
        var ownClaimants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in evidence)
        {
            foreach (var source in Evaluate(row, settlCustomers ?? knownCustomers).Approved)
            {
                if (excludedSources?.Contains(source) == true) continue;
                try
                {
                    if (CustomerNumberPolicy.PadForEnv(source, env, out _) == row.SettlCustomerNumber)
                        ownClaimants.Add(row.SettlCustomerNumber);
                }
                catch (InvalidOperationException) { }
            }
        }
        var customers = ownClaimants;

        // A rejected row drops only its own claim. Its source account leaves the state when no
        // other row holds a valid claim on it, because only valid claims ever reach ownerBySource.
        foreach (var row in evidence)
        {
            var (match, approved) = Evaluate(row, customers);
            if (match is null)
            {
                issues.Add(new SourceAccountOwnershipIssue("InvalidXellentAccountNumber",
                    $"{row.SourceAccount}|representative={row.Representative}", row.MeteringPoint, row.SettlCustomerNumber));
                continue;
            }
            if (match == InvoiceSourceMatch.Kind.None || !knownCustomers.Contains(row.SettlCustomerNumber))
            {
                issues.Add(new SourceAccountOwnershipIssue(
                    match == InvoiceSourceMatch.Kind.None ? "XellentAccountDoesNotMatchSettlCustomer" : "SettlCustomerMissingFromState",
                    $"{row.SourceAccount}|representative={row.Representative}", row.MeteringPoint, row.SettlCustomerNumber));
                continue;
            }
            if (match == InvoiceSourceMatch.Kind.Representative) representativeMatches++;
            if (match == InvoiceSourceMatch.Kind.SameCvr) sameCvrRows.Add(row);
            var claim = new SourceClaim(row.SettlCustomerNumber, row.Cos,
                RepresentativeIsClaimant(row.Representative, row.SettlCustomerNumber, env));
            foreach (var source in approved)
            {
                if (excludedSources?.Contains(source) == true) continue;
                if (!claimsBySource.TryGetValue(source, out var claims))
                    claimsBySource[source] = claims = [];
                if (claims.Count > 0 && !conflictBySource.ContainsKey(source)
                    && claims.All(c => c.Customer != row.SettlCustomerNumber))
                {
                    var conflict = new SourceAccountOwnershipIssue("XellentSourceClaimedByMultipleCustomers",
                        source, row.MeteringPoint, $"{claims[0].Customer}|{row.SettlCustomerNumber}");
                    issues.Add(conflict);
                    conflictBySource[source] = conflict;
                }
                claims.Add(claim);
            }
        }
        // Several customers claiming one account conflict, unless the account is no Settl customer
        // claiming itself and every claimant carries its real CVR. Then one claimant wins: the one the
        // account's representative at that claimant's COS-1 points to, then the earliest COS, then
        // the lowest customer number.
        var tieBreaks = new List<OwnershipTieBreak>();
        foreach (var (source, claims) in claimsBySource)
        {
            var ranked = claims.GroupBy(c => c.Customer, StringComparer.Ordinal)
                .Select(g => new SourceClaim(g.Key, g.Min(c => c.Cos), g.Any(c => c.RepresentativeIsClaimant)))
                .OrderByDescending(c => c.RepresentativeIsClaimant)
                .ThenBy(c => c.Cos)
                .ThenBy(c => c.Customer.Length).ThenBy(c => c.Customer, StringComparer.Ordinal)
                .ToArray();
            if (ranked.Length == 1)
            {
                ownerBySource[source] = ranked[0].Customer;
                continue;
            }
            var claimants = ranked.Select(c => c.Customer).ToArray();
            if (cvrByAccount is null
                || !InvoiceSourceMatch.AllClaimantsShareCvr(source, claimants, cvrByAccount, customers, env))
                continue;
            var (winner, runnerUp) = (ranked[0], ranked[1]);
            ownerBySource[source] = winner.Customer;
            issues.Remove(conflictBySource[source]);
            tieBreaks.Add(new OwnershipTieBreak(source, winner.Customer,
                string.Join('|', claimants.Order(StringComparer.Ordinal)),
                winner.RepresentativeIsClaimant != runnerUp.RepresentativeIsClaimant ? "RepresentativeAtCos"
                    : winner.Cos != runnerUp.Cos ? "EarliestCos" : "LowestCustomerNumber",
                winner.Cos));
        }
        if (issues.Count == 0 || skipUnresolvedSources)
        {
            var byCustomer = ownerBySource.GroupBy(p => p.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Key)
                    .Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            foreach (var account in state.Accounts)
                account.SourceAccounts = byCustomer.GetValueOrDefault(account.SettlAccountNumber) ?? [];
        }
        // A later conflict can still take the source away; only a surviving owner counts as accepted.
        IReadOnlyList<SameCvrAcceptance>? sameCvrAccepted = cvrByAccount is null ? null :
            [.. sameCvrRows
                .Where(r => ownerBySource.GetValueOrDefault(r.SourceAccount) == r.SettlCustomerNumber)
                .Select(r => new SameCvrAcceptance(r.SourceAccount, r.Representative, r.MeteringPoint,
                    r.SettlCustomerNumber, r.Cos))];
        return new(evidence.Count, ownerBySource.Count, representativeMatches, issues, sameCvrAccepted,
            cvrByAccount is null ? null : tieBreaks);
    }

    private sealed record SourceClaim(string Customer, DateOnly Cos, bool RepresentativeIsClaimant);

    /// <summary>True when the row's representative at its COS-1 is the claiming customer itself.</summary>
    private static bool RepresentativeIsClaimant(string representative, string customer, string? env)
    {
        try { return CustomerNumberPolicy.PadForEnv(representative, env, out _) == customer; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<IReadOnlySet<string>> LoadMehMeteringPointIdsAsync(
        XellentDbContext context, IEnumerable<string> meteringPointIds, CancellationToken ct)
    {
        var mehIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in meteringPointIds.Chunk(1500))
        {
            var rows = await context.ExuDelpoints
                .Where(d => d.Dataareaid == MigrationConstants.DataAreaId
                    && d.Companyid == "meh"
                    && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && chunk.Contains(d.Meteringpoint))
                .Select(d => d.Meteringpoint).Distinct().ToListAsync(ct);
            foreach (var id in rows) mehIds.Add(id);
        }
        return mehIds;
    }

    private async Task<(IReadOnlyList<HistoricalSourceClaim> Claims,
        IReadOnlyDictionary<string, string> CvrByXellentAccount)> ResolveHistoricalSourceClaimsAsync(
        XellentDbContext context,
        MigrationStateDocument state,
        IReadOnlyDictionary<string, DateOnly> cosByCustomer,
        CancellationToken ct)
    {
        var seeds = state.Accounts.SelectMany(a => a.SourceAccounts.Select(source =>
            (Source: source, Customer: a.SettlAccountNumber, Cos: cosByCustomer[a.SettlAccountNumber])))
            .ToArray();
        if (seeds.Length == 0)
            return ([], new Dictionary<string, string>(StringComparer.Ordinal));

        Log($"  Resolving historical merge members for {seeds.Length} directly owned source account(s)...");
        var cvrByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in seeds.Select(s => s.Source).Distinct(StringComparer.Ordinal)
            .Chunk(HistoricalAccountLookupChunkSize))
        {
            var rows = await context.CustTables
                .Where(c => c.Dataareaid == MigrationConstants.DataAreaId && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 }).ToListAsync(ct);
            foreach (var row in rows)
                cvrByAccount[row.Accountnum.Trim()] = row.ExuCvrDisponent1 ?? "";
        }

        var accountsByCvr = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Snapshot the seed CVRs: the lookup below adds historical members to cvrByAccount.
        var cvrsToLookup = cvrByAccount.Values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var chunk in cvrsToLookup.Chunk(HistoricalAccountLookupChunkSize))
        {
            var rows = await context.CustTables
                .Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && c.ExuCvrDisponent1 != null && chunk.Contains(c.ExuCvrDisponent1))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 }).ToListAsync(ct);
            foreach (var row in rows)
            {
                var cvr = row.ExuCvrDisponent1!.Trim();
                cvrByAccount[row.Accountnum.Trim()] = cvr;
                if (!accountsByCvr.TryGetValue(cvr, out var accounts))
                    accountsByCvr[cvr] = accounts = [];
                accounts.Add(row.Accountnum.Trim());
            }
        }

        // CustTable/AccountRepresentativeResolver have no company dimension. Limit each CVR
        // portion to accounts with a FOR contract so MEH-only accounts cannot bridge groups or
        // become invoice sources for this FOR export.
        var forAccounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in accountsByCvr.Values.SelectMany(v => v)
            .Distinct(StringComparer.Ordinal).Chunk(HistoricalAccountLookupChunkSize))
        {
            var rows = await context.ExuContractTables
                .Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && (c.Companyid == InvoiceCompanyId || c.Companyid == "")
                    && chunk.Contains(c.Custaccount))
                .Select(c => c.Custaccount).Distinct().ToListAsync(ct);
            foreach (var source in rows) forAccounts.Add(source.Trim());
        }
        foreach (var cvr in accountsByCvr.Keys.ToArray())
            accountsByCvr[cvr] = accountsByCvr[cvr].Where(forAccounts.Contains).ToList();

        var claims = new ConcurrentBag<HistoricalSourceClaim>();
        var ignoredGroups = IgnoredGroupsLoader.ToSet(_ignoredGroups);
        using var gate = new SemaphoreSlim(HistoricalResolveConcurrency);
        var slices = seeds.GroupBy(s => s.Cos).OrderBy(g => g.Key).ToArray();
        var completed = 0;
        await Task.WhenAll(slices.Select(async slice =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var seedAccounts = slice.Select(s => s.Source).Distinct(StringComparer.Ordinal).ToArray();
                var portions = CosStatePlanner.BuildCvrPortions(
                    seedAccounts, cvrByAccount, accountsByCvr, HistoricalResolvePortionSize);
                var seedCustomers = slice.GroupBy(s => s.Source, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(s => s.Customer).Distinct(StringComparer.Ordinal).ToArray(),
                        StringComparer.Ordinal);
                var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
                    .UseSqlServer(context.Database.GetConnectionString(),
                        o => { o.UseCompatibilityLevel(110); o.CommandTimeout(300); }).Options;
                await using var sliceContext = new XellentDbContext(dbOptions);
                foreach (var portion in portions)
                {
                    ct.ThrowIfCancellationRequested();
                    var resolution = await AccountRepresentativeResolver.ResolveAsync(
                        sliceContext, portion.Accounts, MigrationConstants.DataAreaId,
                        referenceDate: slice.Key.AddDays(-1),
                        cancellationToken: ct, ignoredGroupNumbers: ignoredGroups);
                    var membersByRoot = resolution.RepresentativeByAccount
                        .GroupBy(kv => kv.Value, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray(), StringComparer.Ordinal);
                    foreach (var seed in portion.SeedAccounts)
                    {
                        var root = resolution.RepresentativeByAccount.GetValueOrDefault(seed, seed);
                        var members = membersByRoot.GetValueOrDefault(root) ?? [seed];
                        foreach (var customer in seedCustomers[seed])
                            foreach (var member in members)
                                claims.Add(new HistoricalSourceClaim(member, customer, slice.Key));
                    }
                }
                Log($"    historical grouping: {Interlocked.Increment(ref completed)}/{slices.Length} COS date(s)");
            }
            finally { gate.Release(); }
        }));
        return (claims.ToArray(), cvrByAccount);
    }

    private static async Task<IReadOnlyList<HistoricalSourceDecision>> AddHistoricalContractDatesAsync(
        XellentDbContext context, IReadOnlyList<HistoricalSourceDecision> decisions, CancellationToken ct)
    {
        var datesByAccount = new Dictionary<string, (DateOnly Earliest, DateOnly Latest)>(StringComparer.Ordinal);
        foreach (var chunk in decisions.Select(d => d.SourceAccount).Chunk(HistoricalAccountLookupChunkSize))
        {
            var rows = await context.ExuContractTables
                .Where(c => c.Dataareaid == MigrationConstants.DataAreaId
                    && (c.Companyid == InvoiceCompanyId || c.Companyid == "")
                    && chunk.Contains(c.Custaccount))
                .Select(c => new { c.Custaccount, c.Contractenddate }).ToListAsync(ct);
            foreach (var group in rows.GroupBy(r => r.Custaccount.Trim(), StringComparer.Ordinal))
            {
                var dates = group.Select(r => DateOnly.FromDateTime(r.Contractenddate)).ToArray();
                datesByAccount[group.Key] = (dates.Min(), dates.Max());
            }
        }
        return decisions.Select(d => datesByAccount.TryGetValue(d.SourceAccount, out var dates)
            ? d with { EarliestXellentContractEnd = dates.Earliest, LatestXellentContractEnd = dates.Latest }
            : d).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>> ResolveHistoricalMeteringPointCustomersAsync(
        XellentDbContext context,
        IReadOnlyCollection<string> sources,
        IReadOnlyDictionary<string, string> settlCustomerByMp,
        CancellationToken ct)
    {
        var customersBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var chunk in sources.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await (
                from c in context.ExuContractTables
                join cp in context.ExuContractPartTables
                    on new { c.Contractnum, c.Dataareaid, c.Companyid }
                    equals new { cp.Contractnum, cp.Dataareaid, cp.Companyid }
                join ag in context.ExuAgreementTables
                    on new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid }
                    equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                join d in context.ExuDelpoints
                    on new { Attachmentnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                    equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
                where c.Dataareaid == MigrationConstants.DataAreaId
                    && (c.Companyid == InvoiceCompanyId || c.Companyid == "")
                    && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && chunk.Contains(c.Custaccount)
                select new { c.Custaccount, d.Meteringpoint }
            ).Distinct().ToListAsync(ct);
            foreach (var row in rows)
            {
                if (!settlCustomerByMp.TryGetValue(row.Meteringpoint, out var customer)) continue;
                var source = row.Custaccount.Trim();
                if (!customersBySource.TryGetValue(source, out var customers))
                    customersBySource[source] = customers = new HashSet<string>(StringComparer.Ordinal);
                customers.Add(customer);
            }
        }
        return customersBySource.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Queries CUSTINVOICEJOUR only for approved source accounts, applies the Slutafregning
    /// eligibility test, and hands each customer invoices inside its own configured window.
    /// </summary>
    private async Task<int> AssignInvoicesAsync(
        XellentDbContext? context,
        MigrationStateDocument state,
        GenerateStateFromSettlRunOptions options,
        HashSet<string> allSourceAccounts,
        IReadOnlyDictionary<string, DateOnly> cosByCustomer,
        CancellationToken ct,
        GenerateStateOfflineCache.RunInputs? offlineInputs = null)
    {
        if (options.NoInvoices)
        {
            _excludedCompanyInvoices = [];
            // The change-of-supplier date is not invoice data — it comes from Settl, and this loop is
            // the only place that writes it, so it still runs.
            PopulateNoInvoiceDates(state, cosByCustomer);

            Console.WriteLine("--no-invoices: skipping CUSTINVOICEJOUR entirely. " +
                "SourceAccounts and change-of-supplier dates are written; invoice lists stay empty.");
            return 0;
        }

        Log($"  {(_sourceSnapshot is null ? "Querying CUSTINVOICEJOUR" : "Reading saved Xellent invoices")} " +
            $"for {allSourceAccounts.Count} account(s)...");
        var allInvoices = _sourceSnapshot is { } savedInvoices
            ? savedInvoices.Invoices.Where(i => allSourceAccounts.Contains(i.AccountNumber)).ToList()
            : offlineInputs is null
            ? await XellentInvoiceQueries.QueryAsync(
                context!, [.. allSourceAccounts], MigrationConstants.DataAreaId, ct)
            : offlineInputs.Invoices.Where(i => allSourceAccounts.Contains(i.AccountNumber)).ToList();
        Log($"  {allInvoices.Count} invoice(s) found");
        _excludedCompanyInvoices = _sourceSnapshot is { } savedExcluded
            ? savedExcluded.ExcludedInvoices.Where(i => allSourceAccounts.Contains(i.SourceAccount)).ToArray()
            : offlineInputs is null
            ? await ForInvoiceScope.FindExcludedAsync(
                context!, allSourceAccounts, MigrationConstants.DataAreaId, ct)
            : offlineInputs.ExcludedInvoices.Where(i => allSourceAccounts.Contains(i.SourceAccount)).ToArray();
        var excludedKeys = _excludedCompanyInvoices
            .Select(i => (i.SourceAccount, i.InvoiceId)).ToHashSet();
        allInvoices.RemoveAll(i => excludedKeys.Contains((i.AccountNumber, i.InvoiceId)));
        Log($"  FOR invoice scope: {allInvoices.Count} kept, {_excludedCompanyInvoices.Count} " +
            "non-FOR or unlinked invoice row(s) omitted (see excluded-invoices CSV)");

        // Eligibility is date-agnostic — the most recent invoice either is a Slutafregning or is not.
        var eligibleAccounts = CosStatePlanner.EligibleInvoiceAccounts(allInvoices, options.IncludeActive);
        if (options.IncludeActive)
        {
            // Named, not silent: these are the accounts (and the customers behind them) that the
            // default run would have handed zero invoices, or aborted on.
            var strictlyEligible = InvoiceAccountFilter.GetEligibleAccounts(allInvoices);
            var activeAccounts = eligibleAccounts.Where(a => !strictlyEligible.Contains(a))
                .ToHashSet(StringComparer.Ordinal);
            var activeCustomers = state.Accounts.Count(a => a.SourceAccounts.Any(activeAccounts.Contains));
            Console.WriteLine($"Invoice accounts: {eligibleAccounts.Count} (--include-active) — " +
                $"{strictlyEligible.Count} closed with a Slutafregning, {activeAccounts.Count} still active");
            Console.WriteLine($"  {activeCustomers} customer(s) keep invoices that the default run would have excluded as active");
        }
        else
        {
            var excludedCount = allSourceAccounts.Count(a => !eligibleAccounts.Contains(a));
            Console.WriteLine($"Eligible (Slutafregning): {eligibleAccounts.Count}, excluded (active): {excludedCount}");

            if (excludedCount > 0 && !options.SkipActive)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: {excludedCount} accounts are still active. Use --skip-active to exclude them.");
                Console.ResetColor();
                return 1;
            }
        }

        var accountsByCustomer = state.Accounts.ToDictionary(a => a.SettlAccountNumber, StringComparer.Ordinal);
        var ownerBySource = state.Accounts.SelectMany(a => a.SourceAccounts.Select(s =>
                (Source: s, Customer: a.SettlAccountNumber)))
            .ToDictionary(x => x.Source, x => x.Customer, StringComparer.Ordinal);
        var invoicesByCustomer = accountsByCustomer.Keys.ToDictionary(
            c => c, _ => new List<InvoiceManifestItem>(), StringComparer.Ordinal);
        var invoiceIssues = new List<SourceAccountOwnershipIssue>();
        var kept = 0;
        var outsideWindow = 0;
        foreach (var invoice in allInvoices.Where(i => eligibleAccounts.Contains(i.AccountNumber)))
        {
            if (!ownerBySource.TryGetValue(invoice.AccountNumber, out var owner)
                || !invoicesByCustomer.ContainsKey(owner))
            {
                invoiceIssues.Add(new SourceAccountOwnershipIssue(
                    "InvoiceAccountHasNoApprovedOwner",
                    $"{invoice.AccountNumber}/{invoice.InvoiceId}", "", ""));
                continue;
            }
            // Each owner's floor is Jan 1 of its own COS year minus HistoryYears.
            var cos = cosByCustomer[owner];
            if (!CosStatePlanner.IsWithinHistory(invoice.InvoiceDate, cos, options.HistoryYears))
            {
                outsideWindow++;
                continue;
            }
            invoicesByCustomer[owner].Add(ToManifestItem(invoice));
            kept++;
        }

        _ownershipIssues = [.. (_ownershipIssues ?? []), .. invoiceIssues];
        if (invoiceIssues.Count > 0)
        {
            Log($"ERROR: {invoiceIssues.Count} invoice(s) have no unique contract → MP → Settl customer path; " +
                "review the ownership-issues CSV.");
            if (!options.SkipUnresolvedSourceAccounts) return 1;
            Log("  --skip-unresolved-source-accounts: omitting those invoices");
        }
        foreach (var account in state.Accounts)
        {
            account.ChangeOfSupplierDate = cosByCustomer[account.SettlAccountNumber];
            account.Invoices = [.. invoicesByCustomer[account.SettlAccountNumber].OrderBy(i => i.InvoiceDate)];
            account.SourceAccounts = [.. account.SourceAccounts
                .Concat(account.Invoices.Select(i => i.SourceAccount))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        Log($"  Invoices kept: {kept} ({options.HistoryYears}-year window per customer, calendar-aligned to Jan 1); " +
            $"outside their customer's window: {outsideWindow}; unresolved: {invoiceIssues.Count}");
        return 0;
    }

    internal static void PopulateNoInvoiceDates(
        MigrationStateDocument state, IReadOnlyDictionary<string, DateOnly> cosByCustomer)
    {
        foreach (var account in state.Accounts)
            account.ChangeOfSupplierDate = cosByCustomer[account.SettlAccountNumber];
    }

    private static InvoiceManifestItem ToManifestItem(Invoice invoice) => new()
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
    };

    /// <summary>
    /// Metering point GUID → six-digit Settl customer number at its first supplier change. The
    /// dated payer link supplies CustomerId, which is resolved against Settl's customer list.
    /// PayerIdentifier is a billing identifier and can have a suffix or be five digits.
    /// </summary>
    internal async Task<Dictionary<Guid, string>> ResolveCustomerByMeteringPointAsync(
        IReadOnlyList<SettlMpWithCos> mps,
        IReadOnlyDictionary<Guid, string> customerNumbersById,
        CancellationToken ct,
        List<SourceAccountOwnershipIssue>? ownershipIssues = null,
        bool useCosDate = true)
    {
        var wanted = mps.Select(m => m.Id).ToHashSet();
        var result = new Dictionary<Guid, string>();
        var fromPrimary = new HashSet<Guid>();
        var primaryCustomersByMp = new Dictionary<Guid, HashSet<string>>();
        var secondaryCustomersByMp = new Dictionary<Guid, HashSet<string>>();
        var guids = wanted.ToList();
        var failedBatches = 0;
        var unnamed = 0;
        var processed = 0;
        var identificationById = mps.ToDictionary(m => m.Id, m => m.Identification);

        // The API evaluates payer/customer links at SearchDate. A current lookup can name a
        // different customer from the one that held this MP when it migrated.
        foreach (var dateGroup in mps.GroupBy(m => useCosDate ? (DateOnly?)m.Cos : null))
        {
            var date = dateGroup.Key?.ToDateTime(TimeOnly.MinValue);
            var datedGuids = dateGroup.Select(m => m.Id).Distinct().ToList();
            for (var offset = 0; offset < datedGuids.Count; offset += PayerLookupBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = datedGuids.GetRange(offset,
                    Math.Min(PayerLookupBatchSize, datedGuids.Count - offset));
                List<PayerForMeteringPointsDto>? payers;
                string? error;
                if (_sourceSnapshot is { } saved)
                {
                    var matched = saved.PayerLookups.Where(p => p.Date == dateGroup.Key
                        && p.MeteringPointIds.Any(batch.Contains)).ToArray();
                    if (batch.Any(id => !matched.Any(p => p.MeteringPointIds.Contains(id))))
                        throw new InvalidOperationException($"Missing saved Settl payer lookup at {dateGroup.Key}.");
                    payers = matched.SelectMany(p => p.Items ?? []).ToList();
                    error = matched.Select(p => p.Error).FirstOrDefault(e => e is not null);
                }
                else (payers, error) = await _payerClient!.FindPayersForMeteringPointsAsync(batch, date, ct);
                processed += batch.Count;
                if (error != null || payers is null) { failedBatches++; continue; }

                foreach (var entry in payers)
                {
                    if (entry.Payer is not { } payer) continue;
                    var isPrimary = entry.Payer.IsPrimaryCustomerPayer;
                    var customerNumber = customerNumbersById.GetValueOrDefault(payer.CustomerId)?.Trim();
                    if (customerNumber is null || customerNumber.Length != 6
                        || !customerNumber.All(char.IsAsciiDigit))
                    {
                        unnamed++;
                        foreach (var mp in entry.MeteringPoints ?? [])
                            if (batch.Contains(mp.MeteringPointId))
                                ownershipIssues?.Add(new SourceAccountOwnershipIssue(
                                    "MissingOrInvalidSettlCustomerNumber",
                                    payer.PayerInformation?.PayerIdentifier ?? "",
                                    identificationById[mp.MeteringPointId],
                                    payer.CustomerId.ToString()));
                        continue;
                    }

                    foreach (var mp in entry.MeteringPoints ?? [])
                    {
                        if (!batch.Contains(mp.MeteringPointId)) continue;
                        var candidates = isPrimary ? primaryCustomersByMp : secondaryCustomersByMp;
                        if (!candidates.TryGetValue(mp.MeteringPointId, out var customers))
                            candidates[mp.MeteringPointId] = customers = new HashSet<string>(StringComparer.Ordinal);
                        customers.Add(customerNumber);
                        if (result.ContainsKey(mp.MeteringPointId) && (fromPrimary.Contains(mp.MeteringPointId) || !isPrimary)) continue;
                        result[mp.MeteringPointId] = customerNumber;
                        if (isPrimary) fromPrimary.Add(mp.MeteringPointId);
                    }
                }

                if ((processed / PayerLookupBatchSize) % 5 == 0 || processed >= guids.Count)
                    Log($"    customer lookup: {processed}/{guids.Count}" +
                        (date is { } d ? $" at {d:yyyy-MM-dd}" : " current"));
            }
        }

        Log($"  Settl named the customer for {result.Count} of {guids.Count} metering point(s)");
        if (failedBatches > 0)
            Log($"  WARNING: {failedBatches} payer batch(es) failed — affected metering points cannot be assigned an invoice owner");
        if (unnamed > 0)
            Log($"  WARNING: {unnamed} payer link(s) have no six-digit Settl customer number");
        if (ownershipIssues is not null)
        {
            foreach (var mp in mps)
            {
                var candidates = primaryCustomersByMp.TryGetValue(mp.Id, out var primary) && primary.Count > 0
                    ? primary
                    : secondaryCustomersByMp.GetValueOrDefault(mp.Id);
                if (candidates is not { Count: > 1 }) continue;
                ownershipIssues.Add(new SourceAccountOwnershipIssue(
                    "MultipleSettlCustomersForMeteringPoint", "", mp.Identification,
                    string.Join('|', candidates.Order(StringComparer.Ordinal))));
            }
        }
        return result;
    }

    /// <summary>
    /// A payer link can start after COS while the customer link already ran. Metering points with
    /// no payer at COS are looked up through the Customer↔MP link at their own COS date; the
    /// caller's COS-1 Xellent check still decides whether that customer is accepted. A point with
    /// an unusable payer link at COS keeps that issue, and saved snapshots hold no customer link
    /// lookup. Returns the identifications resolved here.
    /// </summary>
    internal async Task<IReadOnlySet<string>> ResolveMissingCustomersByLinkAsync(
        IReadOnlyList<SettlMpWithCos> mps,
        IReadOnlyDictionary<Guid, string> customerNumbersById,
        Dictionary<Guid, string> customerByMpGuid,
        List<SourceAccountOwnershipIssue> linkIssues,
        bool offline,
        CancellationToken ct)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var hasLinkIssue = linkIssues.Select(i => i.MeteringPoint).ToHashSet(StringComparer.Ordinal);
        var missing = mps.Where(m => !customerByMpGuid.ContainsKey(m.Id)
            && !hasLinkIssue.Contains(m.Identification)).ToList();
        if (missing.Count == 0) return resolved;
        if (offline)
        {
            Log($"  Offline: skipped the customer link lookup for {missing.Count} metering point(s) " +
                "without a payer at COS; saved snapshots do not hold it");
            return resolved;
        }

        var customersByMp = new Dictionary<Guid, HashSet<string>>();
        var unusable = new HashSet<Guid>();
        var failedBatches = 0;
        foreach (var dateGroup in missing.GroupBy(m => m.Cos))
        {
            foreach (var batch in dateGroup.Chunk(PayerLookupBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var byId = batch.ToDictionary(m => m.Id);
                var (entries, error) = await _customerLinkLookup!.FindCustomersForMeteringPointsAsync(
                    [.. byId.Keys], dateGroup.Key.ToDateTime(TimeOnly.MinValue), ct);
                if (error != null || entries is null) { failedBatches++; continue; }

                foreach (var entry in entries)
                {
                    if (entry.Customer is not { } customer) continue;
                    var number = customerNumbersById.GetValueOrDefault(customer.Id)?.Trim();
                    var valid = number is { Length: 6 } && number.All(char.IsAsciiDigit);
                    foreach (var link in entry.MeteringPoints ?? [])
                    {
                        if (!byId.TryGetValue(link.MeteringPointId, out var mp)) continue;
                        if (!valid)
                        {
                            unusable.Add(mp.Id);
                            linkIssues.Add(new SourceAccountOwnershipIssue(
                                "MissingOrInvalidSettlCustomerNumber", "", mp.Identification, customer.Id.ToString()));
                            continue;
                        }
                        if (!customersByMp.TryGetValue(mp.Id, out var customers))
                            customersByMp[mp.Id] = customers = new HashSet<string>(StringComparer.Ordinal);
                        customers.Add(number!);
                    }
                }
            }
        }

        foreach (var mp in missing)
        {
            if (unusable.Contains(mp.Id) || !customersByMp.TryGetValue(mp.Id, out var customers)) continue;
            if (customers.Count > 1)
            {
                linkIssues.Add(new SourceAccountOwnershipIssue(
                    "MultipleSettlCustomersForMeteringPoint", "", mp.Identification,
                    string.Join('|', customers.Order(StringComparer.Ordinal))));
                continue;
            }
            customerByMpGuid[mp.Id] = customers.Single();
            resolved.Add(mp.Identification);
        }
        if (failedBatches > 0)
            Log($"  WARNING: {failedBatches} customer link batch(es) failed — affected metering points stay unresolved here");
        Log($"  Customer link at COS resolved {resolved.Count} of {missing.Count} metering point(s) without a payer at COS");
        return resolved;
    }

    /// <summary>
    /// A payer link can start after COS. Metering points with no link at COS get one current
    /// lookup; the caller's COS-1 Xellent check still decides whether that customer is accepted.
    /// A point with an unusable link at COS keeps that issue, and saved snapshots hold no current
    /// lookup. Returns the identifications resolved here.
    /// </summary>
    internal async Task<IReadOnlySet<string>> ResolveMissingCustomersTodayAsync(
        IReadOnlyList<SettlMpWithCos> mps,
        IReadOnlyDictionary<Guid, string> customerNumbersById,
        Dictionary<Guid, string> customerByMpGuid,
        List<SourceAccountOwnershipIssue> linkIssues,
        bool offline,
        CancellationToken ct)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var hasLinkIssue = linkIssues.Select(i => i.MeteringPoint).ToHashSet(StringComparer.Ordinal);
        var missing = mps.Where(m => !customerByMpGuid.ContainsKey(m.Id)
            && !hasLinkIssue.Contains(m.Identification)).ToList();
        if (missing.Count == 0) return resolved;
        if (offline)
        {
            Log($"  Offline: skipped the today-lookup for {missing.Count} metering point(s) " +
                "without a payer at COS; saved snapshots do not hold it");
            return resolved;
        }

        var todayIssues = new List<SourceAccountOwnershipIssue>();
        var today = await ResolveCustomerByMeteringPointAsync(missing, customerNumbersById, ct,
            todayIssues, useCosDate: false);
        linkIssues.AddRange(todayIssues);
        var unclear = todayIssues.Select(i => i.MeteringPoint).ToHashSet(StringComparer.Ordinal);
        foreach (var mp in missing)
        {
            if (unclear.Contains(mp.Identification) || !today.TryGetValue(mp.Id, out var customer))
                continue;
            customerByMpGuid[mp.Id] = customer;
            resolved.Add(mp.Identification);
        }
        Log($"  Today-lookup resolved {resolved.Count} of {missing.Count} metering point(s) " +
            $"without a payer at COS; {missing.Count - resolved.Count} remain NoSettlCustomerForMeteringPoint");
        return resolved;
    }

    internal static Dictionary<string, DateOnly> MinCosWithoutRejectedTodayPoints(
        IReadOnlyList<SettlMpWithCos> mps,
        IReadOnlyDictionary<Guid, string> customerByMpGuid,
        IReadOnlySet<string> rejected) =>
        CosStatePlanner.MinCosByCustomer(mps
            .Where(m => customerByMpGuid.ContainsKey(m.Id) && !rejected.Contains(m.Identification))
            .Select(m => (customerByMpGuid[m.Id], m.Cos)));

    /// <summary>
    /// Names the excluded customers that hold water/heat metering points. Water is out of scope for
    /// this command — the commodity API carries no delivery periods, so there is no COS to derive and
    /// their history window cannot be placed. They are counted and named rather than silently given
    /// the wrong date.
    /// </summary>
    private async Task ReportWaterOnlyCustomersAsync(
        IReadOnlyList<MigratedAccount> excluded,
        IReadOnlyDictionary<Guid, string> customerNumbersById,
        CancellationToken ct)
    {
        if (excluded.Count == 0 || _meteringPointClient is null || _payerClient is null) return;
        try
        {
            var loader = new SettlMeteringPointCosLoader(_meteringPointClient, _ => { });
            var waterMps = await loader.LoadAsync(new CosPeriod(null, null), ct, commodity: CommodityFilter.Water);
            if (waterMps.Count == 0) return;

            var byGuid = await ResolveCustomerByMeteringPointAsync(
                [.. waterMps.Values], customerNumbersById, ct, useCosDate: false);
            var waterCustomers = byGuid.Values.ToHashSet(StringComparer.Ordinal);
            var excludedNumbers = excluded.Select(a => a.SettlAccountNumber).ToHashSet(StringComparer.Ordinal);
            var overlap = excludedNumbers.Where(waterCustomers.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Log($"  Of the {excluded.Count} excluded customer(s), {overlap.Count} hold water/heat metering points " +
                "— out of scope for this command, no invoice history written for them");
            if (overlap.Count > 0)
                Log($"    first excluded water/heat customers: {string.Join(", ", overlap.Take(10))}" +
                    (overlap.Count > 10 ? $" (+{overlap.Count - 10} more)" : ""));
        }
        catch (Exception ex)
        {
            Log($"  WARNING: water/heat census failed ({ex.Message}) — excluded customers are not broken down by commodity");
        }
    }

    private static void LogHistogram(string title, IEnumerable<DateOnly> dates)
    {
        var histogram = CosStatePlanner.Histogram(dates);
        var total = histogram.Sum(h => h.Count);
        Log($"  {title} — {histogram.Count} distinct date(s), {total} item(s):");
        foreach (var (cos, count) in histogram.Take(10))
            Log($"    {cos:yyyy-MM-dd}  {count,7}  ({count * 100.0 / Math.Max(total, 1):F1}%)");
        if (histogram.Count > 10)
        {
            var tail = histogram.Skip(10).ToList();
            Log($"    ... {tail.Count} further date(s) holding {tail.Sum(t => t.Count)} item(s), " +
                $"{tail.Min(t => t.Cos):yyyy-MM-dd} to {tail.Max(t => t.Cos):yyyy-MM-dd}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Flat reference-date enrichment (A/B only — pre-COS behaviour, kept unchanged)
    // ---------------------------------------------------------------------------------------------

    private async Task<int> EnrichWithInvoicesAsync(
        MigrationStateDocument state,
        GenerateStateFromSettlRunOptions options,
        DateTime referenceDate,
        CancellationToken ct)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(options.ConnectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(300); })
            .Options;
        await using var context = new XellentDbContext(dbOptions);

        Console.Write("Testing database connection...");
        await context.Database.CanConnectAsync(ct);
        Console.WriteLine(" OK");

        // Load all CustTable accounts.
        Console.Write("Loading all Xellent accounts from CustTable...");
        var allXellentAccounts = await context.CustTables
            .Where(c => c.Dataareaid == MigrationConstants.DataAreaId)
            .Select(c => c.Accountnum.Trim())
            .Distinct()
            .ToArrayAsync(ct);
        Console.WriteLine($" {allXellentAccounts.Length}");

        // Resolve representatives over the full account set.
        Console.Write("Resolving representative groupings...");
        var resolution = await AccountRepresentativeResolver.ResolveAsync(
            context,
            allXellentAccounts,
            MigrationConstants.DataAreaId,
            referenceDate: DateOnly.FromDateTime(referenceDate),
            cancellationToken: ct,
            ignoredGroupNumbers: IgnoredGroupsLoader.ToSet(_ignoredGroups));
        Console.WriteLine($" done ({resolution.MergedCount} merged)");

        // Build reverse map: padded-representative → [all members].
        var membersByRep = resolution.RepresentativeByAccount
            .GroupBy(kvp => kvp.Value)
            .ToDictionary(g => g.Key, g => g.Select(kvp => kvp.Key).OrderBy(a => a).ToList());

        var paddedRepToMembers = membersByRep.ToDictionary(
            kvp => CustomerNumberPolicy.PadForEnv(kvp.Key, options.Env, out _),
            kvp => kvp.Value);

        // Assign SourceAccounts.
        var allSourceAccounts = new HashSet<string>(StringComparer.Ordinal);
        var missingCount = 0;
        foreach (var account in state.Accounts)
        {
            if (paddedRepToMembers.TryGetValue(account.SettlAccountNumber, out var members))
            {
                account.SourceAccounts = members;
            }
            else
            {
                var unpadded = UnpadAccountNumber(account.SettlAccountNumber, options.Env);
                account.SourceAccounts = [unpadded];
                missingCount++;
            }
            foreach (var src in account.SourceAccounts)
                allSourceAccounts.Add(src);
        }

        if (missingCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  WARNING: {missingCount} Settl accounts not found in Xellent — using SettlAccountNumber as fallback source");
            Console.ResetColor();
        }
        Console.WriteLine($"  Source accounts total: {allSourceAccounts.Count}");

        if (options.NoInvoices)
        {
            Console.WriteLine("--no-invoices: skipping CUSTINVOICEJOUR entirely. " +
                "SourceAccounts are written; invoice lists stay empty.");
            return 0;
        }

        // Query invoices.
        Console.Write($"Querying CUSTINVOICEJOUR for {allSourceAccounts.Count} accounts...");
        var allInvoices = await XellentInvoiceQueries.QueryAsync(
            context, allSourceAccounts.ToArray(), MigrationConstants.DataAreaId, ct);
        Console.WriteLine($" {allInvoices.Count}");

        // Filter: only closed accounts (most recent invoice = Slutafregning).
        var eligibleAccounts = InvoiceAccountFilter.GetEligibleAccounts(allInvoices);
        var historyCutoff = InvoiceAccountFilter.GetHistoryCutoffDate(referenceDate, options.HistoryYears);
        var excludedCount = allSourceAccounts.Count(a => !eligibleAccounts.Contains(a));

        Console.WriteLine($"Eligible (Slutafregning): {eligibleAccounts.Count}, excluded (active): {excludedCount}");

        if (excludedCount > 0 && !options.SkipActive)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: {excludedCount} accounts are still active. Use --skip-active to exclude them.");
            Console.ResetColor();
            return 1;
        }

        var filteredInvoices = allInvoices
            .Where(i => eligibleAccounts.Contains(i.AccountNumber) && i.InvoiceDate >= historyCutoff)
            .ToList();
        Console.WriteLine($"Invoices after filter (invoices from {historyCutoff:yyyy-MM-dd} onward, {options.HistoryYears}y calendar-aligned): {filteredInvoices.Count}");

        // Assign invoices to Settl accounts.
        var sourceToSettl = state.Accounts
            .SelectMany(a => a.SourceAccounts.Select(src => (src, a.SettlAccountNumber)))
            .ToDictionary(x => x.src, x => x.SettlAccountNumber, StringComparer.Ordinal);

        var invoicesBySettlAccount = state.Accounts
            .ToDictionary(a => a.SettlAccountNumber, _ => new List<InvoiceManifestItem>(), StringComparer.Ordinal);

        var processed = 0;
        var total = filteredInvoices.Count;
        foreach (var invoice in filteredInvoices)
        {
            processed++;
            if (processed % 500 == 0 || processed == total)
                Console.Write($"\r  Grouping invoices: {processed:N0}/{total:N0}    ");

            if (!sourceToSettl.TryGetValue(invoice.AccountNumber, out var settlAccount)) continue;
            if (!invoicesBySettlAccount.TryGetValue(settlAccount, out var list)) continue;

            list.Add(ToManifestItem(invoice));
        }
        if (total > 0) Console.WriteLine();

        foreach (var account in state.Accounts)
            account.Invoices = invoicesBySettlAccount.GetValueOrDefault(account.SettlAccountNumber) ?? [];

        return 0;
    }

    /// <summary>
    /// Strips leading '9' padding added by <see cref="CustomerNumberPolicy.Pad"/> (production = 6 digits).
    /// Used as fallback when no grouping is found in the resolver.
    /// </summary>
    private static string UnpadAccountNumber(string settlAccountNumber, string? env)
    {
        if (CustomerNumberPolicy.LengthForEnv(env) is not { } targetLen) return settlAccountNumber;
        if (settlAccountNumber.Length != targetLen) return settlAccountNumber;
        var trimmed = settlAccountNumber.TrimStart('9');
        return trimmed.Length > 0 ? trimmed : settlAccountNumber;
    }
}
