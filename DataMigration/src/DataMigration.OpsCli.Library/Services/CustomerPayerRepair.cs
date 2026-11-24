using System.Globalization;
using DataMigration.Migration.Services;
using DataMigration.Migration.Configuration;
using System.Text.Json;
using ClosedXML.Excel;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal sealed record CustomerPayerRepairDecision(string MeteringPoint, string? CustomerNumber,
    string? PayerIdentification, DateOnly? Start, bool CreateCustomer, bool CreatePayer,
    bool CreateCustomerLink, bool CreatePayerLink, string Status)
{
    public List<RepairContractDecision> Contracts { get; init; } = [];
    public DateOnly? CustomerStart { get; init; }
    public DateOnly? PayerStart { get; init; }
    public DateOnly? End { get; init; }
    public bool DeliveryHistoryChecked { get; init; }
    public RepairDeliveryEvent? DeliveryEvent { get; init; }
    public RepairContractDecision[] OriginalContracts { get; init; } = [];
    public string[] Warnings { get; init; } = [];
}
internal sealed record RepairContractDecision(string ContractName, DateOnly Start, DateOnly End, bool Create, string Evidence);

internal static class CustomerPayerRepair
{
    internal static readonly string[] ContractHeaders = "Action,ContractName,CountryCode,AssociationId,MeteringPointIdentification,Start,End,CommodityType".Split(',');
    internal static readonly string[] CustomerHeaders = "Action,CustomerNumber,CountryCode,Date,CustomerType,OrganizationIdCvr,PersonalNumber,Name1,Name2,StreetCode,StreetName,BuildingNumber,BuildingFloor,RoomIdentification,CitySubDivisionName,PostalCode,City,MunicipalityCode,PostalCountry,AddressIdentifier,Phone,Cellphone,Fax,Email,ManualApprovalRequired,CustomerResponsible,Attention,FrameworkAgreement,PortfolioResponsible,ClientCoordinator,OneStopShop,PbaResponsible,Owner,Notes,CustomerGroupNumber,CustomerGroupName,PaymentTerms,PaymentTermsOffset".Split(',');
    internal static readonly string[] PayerHeaders = "Action,CustomerNumber,CountryCode,Start,PayerIdentification,Name1,Name2,PostOfficeBox,CareOf,Attention,StreetCode,StreetName,BuildingNumber,BuildingFloor,RoomIdentification,CitySubDivisionName,PostalCode,City,MunicipalityCode,PostalCountry,AddressIdentifier,InvoiceText1,InvoiceText2,Notes,Email,ElectronicNumber,RegistrationNumber,AccountNumber,IBAN,BIC,InvoiceChannel,PaymentChannel,AutomaticallyApproveInvoices,Operator,BuyerReferenceIdentifier,AgreementIdentifier,OrderIdentifier,BookkeepingReference,InvoiceLanguage,PayerType,VatIdentificationNumber,NationalIdentificationNumber,ReminderEmail,Cellphone,ReminderTrack".Split(',');
    internal static readonly string[] CustomerLinkHeaders = "Action,CustomerNumber,CountryCode,CommodityType,MeteringPointIdentification,InstallationNo,Start,End,TargetDate".Split(',');
    internal static readonly string[] PayerLinkHeaders = "Action,PayerIdentification,CountryCode,MeteringPointIdentification,InstallationNo,Start,End,TargetDate,CommodityType".Split(',');
    private static readonly HashSet<string> InvoiceChannels = "Paper,ElectronicInvoice,PDF,PBS_BS,PBS_Total,PBS_MSB,EBoks,EHF,OioUbl,SveFact,FinVoice,OioXml,Zugferd,XRechningPeppol,XRechnungEmail,FinVoiceIban".Split(',').ToHashSet();
    private static readonly HashSet<string> PaymentChannels = "FIK,OIOXML_FIK,OIOUBL_FIK,LS,BS,Bank,RefencePayment,AutoGiro,BankGiro,PlusGiro,KID,SEPA,OIOUBK_FIK,AutomaticCardTransfer,MobilePay".Split(',').ToHashSet();

    internal static async Task<int> PrepareAsync(string input, string environment, string scopeFile,
        string templates, string output, CancellationToken ct, bool draftImports = false, string? migrationModel = null, MappingOptions? mappings = null, bool checkDeliveryHistory = false)
    {
        var entries = StatePullScope.Parse(await File.ReadAllLinesAsync(scopeFile, ct));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Repair output directory must be empty; existing outputs are never overwritten.");
        Directory.CreateDirectory(output);
        var store = StateSourceSnapshotStore.Open(input, environment);
        List<CustomerPayerRepairDecision> decisions;
        RepairXellentEvidence source;
        RepairSettlEvidence settl;
        ContractSourceSnapshot? reportSources = null;
        try
        {
            await store.LoadManifestAsync(ct);
            if ((!store.HasSection("repair-xellent") && !store.HasSection("repair-raw-settings")) || !store.HasSection("repair-settl"))
                throw new InvalidOperationException("Snapshot lacks customer/payer repair evidence. Capture both sources with --include-sensitive-data using the updated pull-state-sources command.");
            if (store.HasSection("repair-raw-settings"))
            {
                await using var offlineDb = await RepairRawXellent.OpenOfflineAsync(store, ct);
                await RepairRawXellent.RestoreWorkingPartsAsync(store, offlineDb, ct);
                var settings = (await store.ReadSectionAsync<RepairRawSettings>("repair-raw-settings", ct)).Single();
                source = await CustomerPayerRepairCapture.PrepareXellentAsync(store, offlineDb, settings, ct);
            }
            else source = (await store.ReadSectionAsync<RepairXellentEvidence>("repair-xellent", ct)).Single();
            settl = (await store.ReadSectionAsync<RepairSettlEvidence>("repair-settl", ct)).Single();
            var targets = ResolveTargets(entries, store.Scope, source);
            if (migrationModel is not null)
            {
                var loadedModel = await MigrationRunModel.LoadAsync(migrationModel, ct);
                source = MigrationRunModel.ApplyForImport(source, loadedModel, targets, mappings);
                source = await MigrationRunModel.ApplyDetailsAsync(source, loadedModel, targets, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "preparation-xellent.json"), JsonSerializer.Serialize(source), ct);
            decisions = Plan(source, settl, targets, await store.ReadSectionAsync<SettlContractLinkRow>("settl-contract-links", ct));
            if (checkDeliveryHistory) decisions = decisions.Select(d => RepairDeliveryHistory.Apply(d, settl)).ToList();
            if (store.HasSection("xellent-contract-parts")) reportSources = await store.ReadContractSourcesAsync(ct);
            if (reportSources is not null && source.ChargerAssignments is not null)
                reportSources = reportSources with { ChargerAssignments = source.ChargerAssignments, HasChargerAssignments = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            { Ready = false, Error = ex.Message, Targets = entries.Select(e => new { e.Line, e.Value }) }, new JsonSerializerOptions { WriteIndented = true }), ct);
            await RepairVerification.WriteFailureAsync(output, entries, ex.Message, ct);
            return 1;
        }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        { Ready = false, Planned = decisions.All(d => d.Status == "Ready"), SourceCapturedAt = source.CapturedAt, SettlCapturedAt = settl.CapturedAt, DeliveryHistoryChecked = checkDeliveryHistory,
            source.MigrationModelVersion, source.MigrationModelSha256, Decisions = decisions }, new JsonSerializerOptions { WriteIndented = true }), ct);
        await RepairVerification.WriteAsync(output, decisions, source, settl, false, ct, reportSources);
        var workbooks = new List<(XLWorkbook Book, string Name)>();
        try
        {
            var customers = OpenTemplate(templates, "Customers", "Customer", 4, CustomerHeaders);
            workbooks.Add((customers, "1-Customers.xlsx"));
            var payers = OpenTemplate(templates, "Payers", "Payer", 6, PayerHeaders);
            workbooks.Add((payers, "2-Payers.xlsx"));
            var customerLinks = OpenTemplate(templates, "CustomerMeteringPoints", "CustomerMeteringPoint", 4, CustomerLinkHeaders);
            workbooks.Add((customerLinks, "3-CustomerMeteringPoints.xlsx"));
            var payerLinks = OpenTemplate(templates, "PayerMeteringPoints", "PayerMeteringPoint", 3, PayerLinkHeaders);
            workbooks.Add((payerLinks, "4-PayerMeteringPoints.xlsx"));
            var contractLinks = OpenTemplate(templates, "ContractMeteringPoints", "ContractMeteringPoint", 2, ContractHeaders);
            workbooks.Add((contractLinks, "5-ContractMeteringPoints.xlsx"));
            foreach (var group in decisions.Where(d => d.Status == "Ready" && d.CreateCustomer).GroupBy(d => d.CustomerNumber))
            {
                var c = source.Customers.Single(c => c.Customer.CustomerNumber == group.Key).Customer;
                var row = Postal(c.Address!.PostalAddress!);
                Put(row, "CustomerNumber", c.CustomerNumber, "CountryCode", c.CountryCode, "Date", group.Min(d => d.CustomerStart ?? d.Start),
                    "CustomerType", c.CustomerType, "OrganizationIdCvr", c.VatIdentificationNumber,
                    "PersonalNumber", c.NationalIdentificationNumber, "Phone", c.Address.Phone, "Cellphone", c.Address.Cellphone,
                    "Fax", c.Address.Fax, "Email", c.Address.Email);
                Append(customers, CustomerHeaders, row);
            }
            foreach (var group in decisions.Where(d => d.Status == "Ready" && d.CreatePayer).GroupBy(d => d.PayerIdentification))
            {
                var d = group.First();
                var c = source.Customers.Single(c => c.Customer.CustomerNumber == d.CustomerNumber);
                var p = c.Payers.Single(p => p.PayerInformation!.PayerIdentifier == group.Key);
                var b = p.PayerBilling!;
                var row = Postal(p.PayerInformation!.PostalAddress!);
                Put(row, "CustomerNumber", d.CustomerNumber, "CountryCode", p.CountryCode, "Start", group.Min(d => d.PayerStart ?? d.Start),
                    "PayerIdentification", group.Key, "Email", b.Email, "ElectronicNumber", b.ElectronicNumber,
                    "InvoiceChannel", b.InvoiceChannel, "PaymentChannel", b.PaymentChannel,
                    "BuyerReferenceIdentifier", b.BuyerReferenceIdentifier, "AgreementIdentifier", b.AgreementIdentifier,
                    "OrderIdentifier", b.OrderIdentifier, "PayerType", b.PayerType,
                    "VatIdentificationNumber", b.VatIdentificationNumber, "NationalIdentificationNumber", b.NationalIdentificationNumber);
                Append(payers, PayerHeaders, row);
            }
            foreach (var d in decisions.Where(d => d.Status == "Ready"))
            {
                var row = new Dictionary<string, object?> { ["Action"] = "Create", ["CountryCode"] = "DK",
                    ["CommodityType"] = "Electricity", ["MeteringPointIdentification"] = d.MeteringPoint,
                    ["CustomerNumber"] = d.CustomerNumber, ["PayerIdentification"] = d.PayerIdentification, ["Start"] = d.Start, ["End"] = d.End };
                if (d.CreateCustomerLink) Append(customerLinks, CustomerLinkHeaders, row);
                if (d.CreatePayerLink) Append(payerLinks, PayerLinkHeaders, row);
                foreach (var contract in d.Contracts.Where(c => c.Create))
                    Append(contractLinks, ContractHeaders, new() { ["Action"] = "Create", ["ContractName"] = contract.ContractName, ["CountryCode"] = "DK", ["MeteringPointIdentification"] = d.MeteringPoint, ["Start"] = contract.Start, ["End"] = contract.End == DateOnly.MaxValue ? null : contract.End, ["CommodityType"] = "Electricity" });
            }
            // Review proposed values from the same populated templates, even when release is held.
            await RepairVerification.WriteAsync(output, decisions, source, settl, false, ct, reportSources, workbooks);
            if (decisions.Any(d => d.Status != "Ready"))
            {
                if (draftImports)
                {
                    var draftDirectory = Path.Combine(output, "draft-imports");
                    Directory.CreateDirectory(draftDirectory);
                    foreach (var (book, name) in workbooks)
                    {
                        ct.ThrowIfCancellationRequested();
                        book.SaveAs(Path.Combine(draftDirectory, "DRAFT-NOT-FOR-IMPORT-" + name));
                    }
                }
                return 1;
            }
            // Validate and save all five privately before publishing the complete set.
            foreach (var (book, name) in workbooks)
            {
                ct.ThrowIfCancellationRequested();
                using (var file = File.Create(Path.Combine(output, name + ".pending"))) book.SaveAs(file);
                using var verifyStream = File.OpenRead(Path.Combine(output, name + ".pending"));
                using var verify = new XLWorkbook(verifyStream);
                if (verify.Worksheet(1).Cell(2, 1).GetString() != "Action") throw new InvalidOperationException("Workbook verification failed.");
            }
            foreach (var (_, name) in workbooks) File.Move(Path.Combine(output, name + ".pending"), Path.Combine(output, name));
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { Ready = true, SourceCapturedAt = source.CapturedAt, SettlCapturedAt = settl.CapturedAt, DeliveryHistoryChecked = checkDeliveryHistory,
                source.MigrationModelVersion, source.MigrationModelSha256, Decisions = decisions }, new JsonSerializerOptions { WriteIndented = true }), ct);
            await RepairVerification.WriteAsync(output, decisions, source, settl, true, ct, reportSources);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var (_, name) in workbooks)
            {
                File.Delete(Path.Combine(output, name));
                File.Delete(Path.Combine(output, name + ".pending"));
            }
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                { Ready = false, Error = ex.Message, Decisions = decisions }, new JsonSerializerOptions { WriteIndented = true }), ct);
            try { await RepairVerification.WriteAsync(output, decisions, source, settl, false, ct, reportSources); }
            catch (Exception reportError) when (reportError is not OperationCanceledException)
            { await RepairVerification.WriteFailureAsync(output, entries, ex.Message + "; " + reportError.Message, ct); }
            return 1;
        }
        finally { foreach (var (book, _) in workbooks) book.Dispose(); }
    }

    internal static string[] ResolveTargets(StateScopeEntry[] entries, StatePullScope? scope, RepairXellentEvidence source)
    {
        var result = new HashSet<string>();
        foreach (var e in entries)
        {
            if (e.IsMeteringPoint) result.Add(e.Value);
            else
            {
                var points = scope?.CustomerMeteringPoints.GetValueOrDefault(e.Value)
                    ?? source.SourceAccountPoints.GetValueOrDefault(e.Value) ?? [];
                if (points.Length == 0) throw new InvalidOperationException($"Cannot resolve customer scope at line {e.Line}; supply explicit metering points for merged accounts.");
                result.UnionWith(points);
            }
        }
        if (scope is not null && result.Any(p => !scope.MeteringPoints.Contains(p))) throw new InvalidOperationException("Repair targets exceed captured scope.");
        return result.Order().ToArray();
    }

    internal static List<CustomerPayerRepairDecision> Plan(RepairXellentEvidence source, RepairSettlEvidence settl, IEnumerable<string> targets, IReadOnlyList<SettlContractLinkRow>? contractLinks = null)
    {
        var result = new List<CustomerPayerRepairDecision>();
        foreach (var mp in targets.Distinct().Order())
        {
            if (source.MigrationModelVersion is not null)
            {
                result.Add(PlanMigrationPoint(source, settl, mp, contractLinks));
                continue;
            }
            string? customer = null, payer = null;
            DateOnly? start = null;
            try
            {
                if (!source.OriginalAccountsPreserved) throw new InvalidOperationException("Original-account evidence missing; recapture without representative merging.");
                var point = settl.MeteringPoints.Single(p => p.Identification == mp);
                var history = settl.Histories.Single(h => h.MeteringPoint == mp);
                if (history.Error is not null || history.FirstDate is null || history.Statuses is null)
                    throw new InvalidOperationException("Delivery history missing or failed.");
                var (first, disposition) = MoveInDeliveryFilter.FirstDelivery(history.Statuses.DeliveryStatuses);
                if (first is null || first != history.FirstDate || disposition != "ChangeOfSupplier") throw new InvalidOperationException("First delivery is not a proven supplier change.");
                if (history.ListedInDelivery != true || history.CurrentOrNextDeliveryPeriod is null || history.CurrentOrNextDeliveryPeriod.End.Year < 9999
                    || MoveInDeliveryFilter.ToDkDate(history.CurrentOrNextDeliveryPeriod.Start) != first)
                    throw new InvalidOperationException("Finite or inactive delivery requires period review.");
                start = first;
                var before = first.Value.AddDays(-1);
                if (source.ReferenceDate > before) throw new InvalidOperationException("Source capture reference date is after the required ownership date.");
                var routes = source.Routes.Where(r => r.MeteringPoint == mp && r.Start <= before
                    && (r.End <= new DateOnly(1900, 1, 1) || r.End >= before)).Select(r => (r.CustomerNumber, r.PayerIdentification)).Distinct().ToArray();
                if (routes.Length != 1) throw new InvalidOperationException("Source customer/payer routing is missing or ambiguous at supplier change.");
                (customer, payer) = routes[0];
                var sourceAccounts = source.Routes.Where(r => r.MeteringPoint == mp && r.Start <= before
                    && (r.End <= new DateOnly(1900, 1, 1) || r.End >= before)).Select(r => r.SourceAccount).Distinct().ToArray();
                if (sourceAccounts.Length != 1 || string.IsNullOrWhiteSpace(sourceAccounts[0]))
                    throw new InvalidOperationException("Original source account is missing or ambiguous.");
                var originalAccount = sourceAccounts[0]!;
                var expectedNumber = PadOriginalAccount(originalAccount);
                if (customer != expectedNumber || payer != expectedNumber)
                    throw new InvalidOperationException("Source customer/payer differs from original account; no representative or payer substitution is allowed.");
                if (source.Routes.Any(r => r.MeteringPoint == mp && r.Start > before && (r.CustomerNumber != customer || r.PayerIdentification != payer)))
                    throw new InvalidOperationException("A later source owner requires period review.");
                var c = source.Customers.Single(c => c.Customer.CustomerNumber == customer);
                var p = c.Payers.Single(p => p.PayerInformation?.PayerIdentifier == payer);
                if (p.CustomerId != c.Customer.Id) throw new InvalidOperationException("Source payer ownership is inconsistent.");
                if (c.SourceAccount != originalAccount) throw new InvalidOperationException("Customer source account disagrees with route evidence.");
                var customerAlias = c.SourceAccount;
                var payerAlias = payer!.StartsWith(customer!, StringComparison.Ordinal) ? customerAlias + payer[customer!.Length..] : payer;
                var existingCustomers = settl.Customers.Where(c => (c.Customer.CustomerNumber == customer || c.Customer.CustomerNumber == customerAlias) && c.Customer.CountryCode == "DK").ToArray();
                var existingPayers = settl.Payers.Where(p => (p.Payer.PayerIdentification == payer || p.Payer.PayerIdentification == payerAlias) && p.Payer.CountryCode == "DK").ToArray();
                if (existingCustomers.Length > 1 || existingPayers.Length > 1) throw new InvalidOperationException("Duplicate existing entity identifiers.");
                var existingCustomer = existingCustomers.SingleOrDefault();
                var existingPayer = existingPayers.SingleOrDefault();
                if (existingCustomer is not null && existingCustomer.Customer.CustomerNumber != customer || existingPayer is not null && existingPayer.Payer.PayerIdentification != payer)
                    throw new InvalidOperationException("Legacy unpadded identifier exists; resolve identifier policy before import.");
                if (existingCustomer is not null && (existingCustomer.EarliestVersion is null || existingCustomer.EarliestVersion > start)) throw new InvalidOperationException("Existing customer does not provably cover the required period.");
                if (existingPayer is not null && (existingPayer.EarliestVersion is null || existingPayer.EarliestVersion > start || existingCustomer is null || existingPayer.Payer.CustomerId != existingCustomer.Customer.Id
                    || !existingPayer.CustomerAtDate.TryGetValue(start!.Value, out var historicalOwner) || historicalOwner != existingCustomer.Customer.Id)) throw new InvalidOperationException("Existing payer ownership or period conflicts.");
                if (existingPayer?.CapturedVersions is { } captured && (!captured.TryGetValue(start!.Value, out var version)
                    || version.InformationStart is null || version.BillingStart is null || version.InformationStart > start || version.BillingStart > start))
                    throw new InvalidOperationException("Existing payer ownership or period conflicts.");
                if ((existingCustomer is null || existingPayer is null) && source.SourceValueErrors.TryGetValue(originalAccount, out var sourceValueError))
                    throw new InvalidOperationException(sourceValueError);
                if (existingCustomer is null) ValidateCustomer(c.Customer);
                if (existingPayer is null) ValidatePayer(p);
                var createCustomerLink = NeedsLink(settl.CustomerLinks.Where(l => l.Link.MeteringPointId == point.Id)
                    .Select(l => (l.CustomerId, l.Link.Start, l.Link.End)), existingCustomer?.Customer.Id, first.Value);
                var createPayerLink = NeedsLink(settl.PayerLinks.Where(l => l.Link.MeteringPointId == point.Id)
                    .Select(l => (l.PayerId, l.Link.Start, l.Link.End)), existingPayer?.Payer.Id, first.Value);
                var contracts = PlanContracts(source, settl, contractLinks, mp, first.Value);
                result.Add(new(mp, customer, payer, start, existingCustomer is null, existingPayer is null, createCustomerLink, createPayerLink, "Ready") { Contracts = contracts });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { result.Add(new(mp, customer, payer, start, false, false, false, false, ex.Message)); }
        }
        var collisions = result.Where(d => d.PayerIdentification is not null).GroupBy(d => d.PayerIdentification)
            .Where(g => g.Select(d => d.CustomerNumber).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        return result.Select(d => collisions.Contains(d.PayerIdentification) ? d with { Status = "Payer identifier claimed by multiple customers." } : d).ToList();
    }

    private static CustomerPayerRepairDecision PlanMigrationPoint(RepairXellentEvidence source, RepairSettlEvidence settl,
        string mp, IReadOnlyList<SettlContractLinkRow>? contractLinks)
    {
        if (source.IncludeAllMigrationRecords) return PlanAllMigrationRecords(source, settl, mp, contractLinks);
        var relation = source.MigrationRelationships.GetValueOrDefault(mp);
        var empty = new CustomerPayerRepairDecision(mp, relation?.CustomerNumber, relation?.PayerNumber, relation?.LinkStart,
            false, false, false, false, "Migration customer/payer relationship dates missing.")
            { CustomerStart = relation?.CustomerStart, PayerStart = relation?.PayerStart };
        try
        {
            if (source.ContractMappingErrors.TryGetValue(mp, out var finding)) throw new InvalidOperationException(finding);
            if (relation is null) return empty;
            if (relation.CustomerNumber != PadOriginalAccount(relation.Account) || relation.PayerNumber != relation.CustomerNumber)
                throw new InvalidOperationException("Source customer/payer differs from original account; no representative or payer substitution is allowed.");
            var point = settl.MeteringPoints.Single(p => p.Identification == mp);
            var proposedContracts = PlanContracts(source, settl, contractLinks, mp, relation.LinkStart);
            var customers = settl.Customers.Where(c => c.Customer.CountryCode == "DK"
                && (c.Customer.CustomerNumber == relation.CustomerNumber || c.Customer.CustomerNumber == relation.Account)).ToArray();
            var payers = settl.Payers.Where(p => p.Payer.CountryCode == "DK"
                && (p.Payer.PayerIdentification == relation.PayerNumber || p.Payer.PayerIdentification == relation.Account)).ToArray();
            if (customers.Length > 1 || payers.Length > 1) throw new InvalidOperationException("Duplicate existing entity identifiers.");
            var customer = customers.SingleOrDefault(); var payer = payers.SingleOrDefault();
            if (customer is not null && customer.Customer.CustomerNumber != relation.CustomerNumber
                || payer is not null && payer.Payer.PayerIdentification != relation.PayerNumber)
                throw new InvalidOperationException("Legacy unpadded identifier exists; resolve identifier policy before import.");
            if (customer is not null && (customer.EarliestVersion is null || customer.EarliestVersion > relation.CustomerStart))
                throw new InvalidOperationException("Existing customer does not provably cover the required period.");
            if (payer is not null && (payer.EarliestVersion is null || payer.EarliestVersion > relation.PayerStart
                || customer is null || payer.Payer.CustomerId != customer.Customer.Id))
                throw new InvalidOperationException("Existing payer ownership or period conflicts.");
            var original = source.Customers.Single(c => c.Customer.CustomerNumber == relation.CustomerNumber);
            if (customer is null) ValidateCustomer(original.Customer);
            if (payer is null) ValidatePayer(original.Payers.Single(p => p.PayerInformation?.PayerIdentifier == relation.PayerNumber));
            var customerLink = NeedsLink(settl.CustomerLinks.Where(l => l.Link.MeteringPointId == point.Id)
                .Select(l => (l.CustomerId, l.Link.Start, l.Link.End)), customer?.Customer.Id, relation.LinkStart);
            var payerLink = NeedsLink(settl.PayerLinks.Where(l => l.Link.MeteringPointId == point.Id)
                .Select(l => (l.PayerId, l.Link.Start, l.Link.End)), payer?.Payer.Id, relation.LinkStart);
            return empty with { CreateCustomer = customer is null, CreatePayer = payer is null, CreateCustomerLink = customerLink,
                CreatePayerLink = payerLink, Contracts = proposedContracts, Status = "Ready" };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { return empty with { Status = ex.Message }; }
    }

    private static CustomerPayerRepairDecision PlanAllMigrationRecords(RepairXellentEvidence source, RepairSettlEvidence settl,
        string mp, IReadOnlyList<SettlContractLinkRow>? existing)
    {
        var warnings = source.MigrationWarnings.GetValueOrDefault(mp, []).ToList();
        var relation = source.MigrationRelationships.GetValueOrDefault(mp);
        var decision = new CustomerPayerRepairDecision(mp, relation?.CustomerNumber, relation?.PayerNumber, relation?.LinkStart,
            false, false, false, false, "Migration customer/payer relationship dates missing.")
            { CustomerStart = relation?.CustomerStart, PayerStart = relation?.PayerStart };
        if (relation is null) return decision;
        if (source.ContractMappingErrors.TryGetValue(mp, out var error)) return decision with { Status = error };
        var owner = source.Customers.SingleOrDefault(c => c.Customer.CustomerNumber == relation.CustomerNumber);
        if (owner is null) return decision with { Status = "Migration customer/payer record missing or ambiguous." };
        // Snapshot comparisons describe the existing state; they never remove model rows.
        try { _ = PlanContracts(source, settl, existing, mp, relation.LinkStart); }
        catch (InvalidOperationException ex) { warnings.Add(ex.Message); }
        var customer = settl.Customers.FirstOrDefault(c => c.Customer.CustomerNumber == relation.CustomerNumber);
        var payer = settl.Payers.FirstOrDefault(p => p.Payer.PayerIdentification == relation.PayerNumber);
        if (customer is not null) warnings.Add("Customer already exists in Settl.");
        if (payer is not null) warnings.Add("Payer already exists in Settl.");
        var point = settl.MeteringPoints.FirstOrDefault(p => p.Identification == mp);
        if (point is null) warnings.Add("Metering point missing in Settl snapshot.");
        else
        {
            if (settl.CustomerLinks.Any(l => l.Link.MeteringPointId == point.Id)) warnings.Add("Customer links already exist in Settl.");
            if (settl.PayerLinks.Any(l => l.Link.MeteringPointId == point.Id)) warnings.Add("Payer links already exist in Settl.");
        }
        try { ValidateCustomer(owner.Customer); ValidatePayer(owner.Payers.Single()); }
        catch (InvalidOperationException ex) { warnings.Add(ex.Message); }
        return decision with { CreateCustomer = true, CreatePayer = true, CreateCustomerLink = true, CreatePayerLink = true,
            Contracts = (source.ExpectedContracts ?? []).Where(c => c.MeteringPoint == mp)
                .Select(c => new RepairContractDecision(c.ContractName, c.Start, c.End, true, c.SourceContracts)).ToList(),
            Status = "Ready", Warnings = warnings.Distinct().ToArray() };
    }

    internal static string PadOriginalAccount(string account)
    {
        if (account.Length is < 1 or > 6 || account.Any(c => c is < '0' or > '9'))
            throw new InvalidOperationException("Invalid original account identifier.");
        return account.Length == 5 ? "9" + account : account;
    }

    internal static List<RepairContractDecision> PlanContracts(RepairXellentEvidence source, RepairSettlEvidence settl,
        IReadOnlyList<SettlContractLinkRow>? existing, string mp, DateOnly deliveryStart)
    {
        if (source.ExpectedContracts is null || settl.ContractCatalog is null || existing is null)
            throw new InvalidOperationException("Contract evidence missing; recapture source mappings and Settl catalog/associations.");
        if (source.ContractMappingErrors.TryGetValue(mp, out var mappingError)) throw new InvalidOperationException(mappingError);
        var wanted = source.ExpectedContracts.Where(c => c.MeteringPoint == mp)
            .Select(c => c with { Start = source.MigrationModelVersion is null && c.Start < deliveryStart ? deliveryStart : c.Start,
                End = c.End.Year <= 1900 ? DateOnly.MaxValue : c.End })
            .Where(c => c.End >= c.Start).Distinct().OrderBy(c => c.Start).ThenBy(c => c.ContractName, StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0 || source.MigrationModelVersion is null && !wanted.Any(c => c.Start == deliveryStart))
            throw new InvalidOperationException("No proven contract covers the supplier-change start.");
        var relevantFrom = wanted.Min(c => c.Start);
        var links = existing.Where(l => l.MeteringPoint == mp && ContractEnd(l.End) >= relevantFrom).ToArray();
        var result = new List<RepairContractDecision>();
        foreach (var desired in wanted)
        {
            var catalog = settl.ContractCatalog.Where(c => c.ContractName == desired.ContractName && c.CommodityType == "Electricity").ToArray();
            if (catalog.Length != 1 || catalog[0].Id == Guid.Empty)
                throw new InvalidOperationException("Expected contract is absent or ambiguous in Settl catalog: " + desired.ContractName);
            var relevant = links.Where(l => l.ContractId == catalog[0].Id
                && MoveInDeliveryFilter.ToDkDate(l.Start) <= desired.End && ContractEnd(l.End) >= desired.Start).ToArray();
            var covered = Covers(relevant.Select(l => (MoveInDeliveryFilter.ToDkDate(l.Start), ContractEnd(l.End))), desired.Start, desired.End);
            if (relevant.Length > 0 && !covered)
                throw new InvalidOperationException("Existing contract periods overlap partially or conflict; period review required.");
            result.Add(new(desired.ContractName, desired.Start, desired.End, !covered,
                $"repair-xellent.ExpectedContracts: product={desired.Product}; sourceContracts={desired.SourceContracts}; sourcePeriod={desired.Start:yyyy-MM-dd}..{desired.End:yyyy-MM-dd}; settl-contract-links association={string.Join("|", relevant.Select(l => l.AssociationId))}"));
        }
        if (links.Any(l => !wanted.Any(w => w.ContractName == l.ContractName
            && (w.Start <= MoveInDeliveryFilter.ToDkDate(l.Start) || MoveInDeliveryFilter.ToDkDate(l.Start) <= relevantFrom) && w.End >= ContractEnd(l.End))))
            throw new InvalidOperationException("An existing contract outside expected periods requires review.");
        if (result.GroupBy(c => (c.ContractName, c.Start, c.End)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate source contract periods require review.");
        return result;
    }

    private static DateOnly ContractEnd(DateTimeOffset value) => value.Year >= 9999 ? DateOnly.MaxValue
        : MoveInDeliveryFilter.ToDkDate(value).AddDays(-1);

    private static bool Covers(IEnumerable<(DateOnly Start, DateOnly End)> periods, DateOnly start, DateOnly end)
    {
        var cursor = start;
        foreach (var period in periods.OrderBy(p => p.Start).ThenBy(p => p.End))
        {
            if (period.End < cursor) continue;
            if (period.Start > cursor) return false;
            if (period.End >= end) return true;
            cursor = period.End.AddDays(1);
        }
        return false;
    }

    internal static bool NeedsLink(IEnumerable<(Guid Owner, DateOnly Start, DateOnly? End)> links, Guid? expected, DateOnly start)
    {
        // Customer/payer association ends are exclusive Danish date boundaries.
        var relevant = links.Where(l => l.End is null || l.End > start).ToArray();
        if (relevant.Length == 0) return true;
        if (relevant.All(l => l.Owner == expected) && Covers(relevant.Select(l => (l.Start,
            l.End is null || l.End == DateOnly.MaxValue ? DateOnly.MaxValue : l.End.Value.AddDays(-1))), start, DateOnly.MaxValue)) return false;
        if (relevant.Length == 1 && relevant[0].Owner == expected && relevant[0].Start <= start
            && (relevant[0].End is null || relevant[0].End == DateOnly.MaxValue)) return false;
        throw new InvalidOperationException("Conflicting, overlapping or later associations require period review.");
    }

    private static void ValidateCustomer(CreateCustomerDto c)
    {
        if (c.CountryCode != "DK" || c.CustomerType is not ("Person" or "Company") || string.IsNullOrWhiteSpace(c.CustomerNumber)
            || string.IsNullOrWhiteSpace(c.Address?.PostalAddress?.Name1?.FullName) || c.Address.PostalAddress.PhysicalAddress is null
            || string.IsNullOrWhiteSpace(c.Address.PostalAddress.PhysicalAddress.StreetName) || string.IsNullOrWhiteSpace(c.Address.PostalAddress.PhysicalAddress.PostalCode))
            throw new InvalidOperationException("Required customer details are missing.");
        if (c.CustomerType == "Person" && !string.IsNullOrEmpty(c.VatIdentificationNumber)
            || c.CustomerType == "Company" && !string.IsNullOrEmpty(c.NationalIdentificationNumber)) throw new InvalidOperationException("Customer identity type conflicts.");
    }
    private static void ValidatePayer(CreatePayerDto p)
    {
        if (string.IsNullOrWhiteSpace(p.PayerInformation?.PayerIdentifier) || p.CountryCode != "DK" || string.IsNullOrWhiteSpace(p.PayerInformation?.PostalAddress?.Name1?.FullName)
            || p.PayerInformation.PostalAddress.PhysicalAddress is null || string.IsNullOrWhiteSpace(p.PayerInformation.PostalAddress.PhysicalAddress.StreetName)
            || string.IsNullOrWhiteSpace(p.PayerInformation.PostalAddress.PhysicalAddress.PostalCode) || p.PayerBilling is null
            || p.PayerBilling.PayerType is not ("Person" or "Company")) throw new InvalidOperationException("Required payer details are missing.");
        if (!InvoiceChannels.Contains(p.PayerBilling.InvoiceChannel) || !PaymentChannels.Contains(p.PayerBilling.PaymentChannel))
            throw new InvalidOperationException("Source billing channel is not supported by the supplied Excel template.");
        if (p.PayerBilling.InvoiceChannel == "PDF" && string.IsNullOrWhiteSpace(p.PayerBilling.Email)) throw new InvalidOperationException("PDF invoicing requires an email.");
    }

    private static XLWorkbook OpenTemplate(string directory, string sheetName, string type, int version, string[] headers)
    {
        var candidates = Directory.GetFiles(directory, sheetName + "_V" + version + "*.xlsx");
        if (candidates.Length != 1) throw new InvalidOperationException($"Expected exactly one {sheetName} V{version} template.");
        var book = new XLWorkbook(candidates[0]);
        try
        {
            if (book.Worksheets.Count != 1) throw new InvalidOperationException("Template must contain only its expected worksheet.");
            var sheet = book.Worksheet(sheetName);
            if (sheetName != "ContractMeteringPoints" && sheetName.EndsWith("MeteringPoints") && (sheet.Cell(1, 5).GetString() != "Identification" || sheet.Cell(1, 6).GetString() != "MeteringPointIdentification"))
                throw new InvalidOperationException("Template identification mode must be MeteringPointIdentification.");
            if (sheet.Cell(1, 1).GetString() != "Type" || sheet.Cell(1, 2).GetString() != type || sheet.Cell(1, 3).GetString() != "Version"
                || sheet.Cell(1, 4).GetValue<int>() != version || sheet.LastColumnUsed()!.ColumnNumber() != headers.Length
                || !headers.SequenceEqual(Enumerable.Range(1, headers.Length).Select(i => sheet.Cell(2, i).GetString()))
                || (sheet.LastRowUsed()?.RowNumber() ?? 0) > 2)
                throw new InvalidOperationException($"Template {sheetName} does not match the approved blank schema.");
            return book;
        }
        catch { book.Dispose(); throw; }
    }
    private static Dictionary<string, object?> Postal(PostalAddressDto address)
    {
        var p = address.PhysicalAddress!;
        return new() { ["Action"] = "Create", ["Name1"] = address.Name1?.FullName, ["Name2"] = address.Name2?.FullName,
            ["StreetCode"] = p.StreetCode, ["StreetName"] = p.StreetName, ["BuildingNumber"] = p.BuildingNumber,
            ["BuildingFloor"] = p.BuildingFloor, ["RoomIdentification"] = p.RoomIdentification,
            ["CitySubDivisionName"] = p.CitySubDivisionName, ["PostalCode"] = p.PostalCode, ["City"] = p.City,
            ["MunicipalityCode"] = p.MunicipalityCode, ["PostalCountry"] = p.CountryCode, ["AddressIdentifier"] = p.AddressIdentifier?.Identification };
    }
    private static void Put(Dictionary<string, object?> row, params object?[] pairs)
    { for (var i = 0; i < pairs.Length; i += 2) row[(string)pairs[i]!] = pairs[i + 1]; }
    private static void Append(XLWorkbook book, string[] headers, Dictionary<string, object?> values)
    {
        var sheet = book.Worksheet(1);
        var index = Math.Max(3, (sheet.LastRowUsed()?.RowNumber() ?? 2) + 1);
        for (var i = 0; i < headers.Length; i++)
        {
            var value = values.GetValueOrDefault(headers[i]);
            if (value is null) continue;
            var cell = sheet.Cell(index, i + 1);
            if (value is DateOnly date) { cell.Value = date.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; }
            else { cell.Style.NumberFormat.Format = "@"; cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
        }
    }
}
