using DataMigration.Migration.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DataMigration.OpsCli.Runners;

public class GenerateSeedRunOptions
{
    public string[] AccountNumbers { get; set; } = [];
    public string OutputPath { get; set; } = string.Empty;
    public DateTime? ReferenceDate { get; set; }

    /// <summary>
    /// Optional payer IDs to search for (when PayerIdentification differs from account number).
    /// Format: "accountNumber:payerId" pairs. If set, uses payerId for payer search instead of account number.
    /// </summary>
    public Dictionary<string, string>? PayerIdOverrides { get; set; }

    /// <summary>
    /// Optional contract names to fetch MP mappings for. Saved as contracts.json in output directory.
    /// </summary>
    public string[]? ContractNames { get; set; }

    /// <summary>Target Settl environment ("production" padder til 6 cifre; alle andre er no-op). See <see cref="CustomerNumberPolicy"/>.</summary>
    public string? Env { get; set; }
}

/// <summary>
/// All Settl API data for a single account: customer, payers, metering points, contracts.
/// Search endpoints find IDs, then GetById fetches full objects for comparison.
/// </summary>
public class SettlAccountSeedData
{
    /// <summary>Search result (lightweight, for ID lookup).</summary>
    public CustomerInfoDto? Customer { get; set; }
    /// <summary>Full customer from GET /api/v2/customers/{id}.</summary>
    public CustomerResponse? CustomerFull { get; set; }

    public List<PayerInfoDto> Payers { get; set; } = [];
    /// <summary>Full payers from GET /api/v1/Payers/{id}.</summary>
    public List<PayerResponse> PayersFull { get; set; } = [];

    public List<MeteringPointInfoDto> MeteringPoints { get; set; } = [];
    /// <summary>Full metering points (raw JSON) from GET /api/v3/meteringPoints/{id}.</summary>
    public List<string> MeteringPointsRaw { get; set; } = [];

    public List<CustomerContractDto> Contracts { get; set; } = [];
}

/// <summary>
/// Contract-MP mappings from Settl production for a profile.
/// Stored per-profile (not per-account) since contracts are global.
/// </summary>
public class SettlContractSeedData
{
    public Dictionary<string, List<string>> ContractMeteringPoints { get; set; } = [];
}

/// <summary>
/// Seed data snapshot captured from Xellent production DB for a single account.
/// Contains all tables needed to simulate a full profile run:
/// AccountQueryService, DataExtractionService, and SettlMappingService.
/// </summary>
public class XellentSeedData
{
    // Primary: ExtractCustomersAsync
    public List<CustTable> CustTables { get; set; } = [];
    public List<ExuContractTable> ExuContractTables { get; set; } = [];
    public List<ExuContractPartTable> ExuContractPartTables { get; set; } = [];
    public List<DirPartyAddressRelationship> DirPartyAddressRelationships { get; set; } = [];
    public List<DirPartyAddressRelationshi1066> DirPartyAddressRelationshi1066s { get; set; } = [];
    public List<Address> Addresses { get; set; } = [];

    // Fallback 1: GetFallbackLegalAddressesAsync (when primary address has no DAR ID)
    public List<ExuAgreementTable> ExuAgreementTables { get; set; } = [];
    public List<ExuDelpoint> ExuDelpoints { get; set; } = [];
    public List<ExuDelpointContactInfoTable> ExuDelpointContactInfoTables { get; set; } = [];
    public List<AddressZipCode> AddressZipCodes { get; set; } = [];

    // Fallback 2: GetFallbackEmailsAsync (when CustTable.Email is empty)
    public List<DirPartyECommunicationRela2608> DirPartyECommunicationRela2608s { get; set; } = [];
    public List<DirECommunicationAddress> DirECommunicationAddresses { get; set; } = [];

    // AccountQueryService: query + child metering point exclusion
    public List<EgsMeterpointTypecodeperiod> EgsMeterpointTypecodeperiods { get; set; } = [];
    public List<ExuBillingMethodPeriod> ExuBillingMethodPeriods { get; set; } = [];
    public List<ExuDelpoint2delpointrela25505> ExuDelpoint2delpointrela25505s { get; set; } = [];

    // Metering point extraction: addresses, meters
    public List<ExuStreet> ExuStreets { get; set; } = [];
    public List<ExuMeterTableTrade> ExuMeterTableTrades { get; set; } = [];
    public List<ExuGroupTable> ExuGroupTables { get; set; } = [];

    // Installation-based extraction (Vand, Varme, etc.)
    public List<ExuInstallation> ExuInstallations { get; set; } = [];
    public List<ExuMeterJournalTrans> ExuMeterJournalTrans { get; set; } = [];
}

public class GenerateSeedRunner
{
    private readonly string _connectionString;
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlPayerClient _payerClient;
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly ISettlContractClient _contractClient;

    public GenerateSeedRunner(
        string connectionString,
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient meteringPointClient,
        ISettlContractClient contractClient)
    {
        _connectionString = connectionString;
        _customerClient = customerClient;
        _payerClient = payerClient;
        _meteringPointClient = meteringPointClient;
        _contractClient = contractClient;
    }

    public async Task<int> RunAsync(GenerateSeedRunOptions options, CancellationToken cancellationToken = default)
    {
        foreach (var account in options.AccountNumbers)
        {
            var result = await RunSingleAsync(account, options.OutputPath, options.PayerIdOverrides, options.Env, cancellationToken);
            if (result != 0) return result;
        }

        // Fetch contract-MP mappings if contract names specified
        if (options.ContractNames is { Length: > 0 })
        {
            await FetchContractMeteringPointsAsync(options.ContractNames, options.OutputPath, cancellationToken);
        }

        return 0;
    }

    private async Task FetchContractMeteringPointsAsync(string[] contractNames, string outputPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Fetching contract-MP mappings ===");
        Console.Write("Finding contracts... ");
        var (allContracts, findError) = await _contractClient.FindContractsAsync(cancellationToken: cancellationToken);
        if (findError != null || allContracts == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {findError}");
            Console.ResetColor();
            return;
        }

        var contractsByName = allContracts
            .Where(c => c.ContractName != null && contractNames.Contains(c.ContractName))
            .ToDictionary(c => c.ContractName!);
        Console.WriteLine($"{contractsByName.Count} of {contractNames.Length} found");

        var seedData = new SettlContractSeedData();

        foreach (var name in contractNames)
        {
            if (!contractsByName.TryGetValue(name, out var contract))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  {name}: not found");
                Console.ResetColor();
                continue;
            }

            var (mps, mpError) = await _contractClient.GetContractMeteringPointsAsync(contract.Id, cancellationToken);
            var mpIds = mps?.Select(m => m.MeteringPointIdentification!)
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id)
                .ToList() ?? [];
            seedData.ContractMeteringPoints[name] = mpIds;
            Console.WriteLine($"  {name}: {mpIds.Count} MPs");
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var contractsPath = Path.Combine(outputPath, "contracts.json");
        await File.WriteAllTextAsync(contractsPath, JsonSerializer.Serialize(seedData, jsonOptions), cancellationToken);
        Console.WriteLine($"Written: {contractsPath}");
    }

    private async Task<int> RunSingleAsync(string account, string outputPath,
        Dictionary<string, string>? payerIdOverrides, string? env, CancellationToken cancellationToken)
    {
        Console.WriteLine($"=== Generate seed data: {account} ===");
        Console.WriteLine();

        var optionsBuilder = new DbContextOptionsBuilder<XellentDbContext>();
        optionsBuilder.UseSqlServer(_connectionString, sqlOptions => { sqlOptions.UseCompatibilityLevel(110); sqlOptions.CommandTimeout(120); });
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        await using var context = new XellentDbContext(optionsBuilder.Options);

        try
        {
            await context.Database.CanConnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to connect to Xellent: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        Console.Write("Querying Xellent... ");
        var seedData = await QueryXellentAsync(context, account, cancellationToken);
        Console.WriteLine("done");
        Console.WriteLine($"  CustTables:                     {seedData.CustTables.Count}");
        Console.WriteLine($"  ExuContractTables:              {seedData.ExuContractTables.Count}");
        Console.WriteLine($"  ExuContractPartTables:          {seedData.ExuContractPartTables.Count}");
        Console.WriteLine($"  DirPartyAddressRelationships:   {seedData.DirPartyAddressRelationships.Count}");
        Console.WriteLine($"  DirPartyAddressRelationshi1066: {seedData.DirPartyAddressRelationshi1066s.Count}");
        Console.WriteLine($"  Addresses:                      {seedData.Addresses.Count}");
        Console.WriteLine($"  ExuAgreementTables:             {seedData.ExuAgreementTables.Count}");
        Console.WriteLine($"  ExuDelpoints:                   {seedData.ExuDelpoints.Count}");
        Console.WriteLine($"  ExuDelpointContactInfoTables:   {seedData.ExuDelpointContactInfoTables.Count}");
        Console.WriteLine($"  AddressZipCodes:                {seedData.AddressZipCodes.Count}");
        Console.WriteLine($"  DirPartyECommunicationRela2608: {seedData.DirPartyECommunicationRela2608s.Count}");
        Console.WriteLine($"  DirECommunicationAddresses:     {seedData.DirECommunicationAddresses.Count}");
        Console.WriteLine($"  EgsMeterpointTypecodeperiods:   {seedData.EgsMeterpointTypecodeperiods.Count}");
        Console.WriteLine($"  ExuBillingMethodPeriods:        {seedData.ExuBillingMethodPeriods.Count}");
        Console.WriteLine($"  ExuDelpoint2delpointrelations:  {seedData.ExuDelpoint2delpointrela25505s.Count}");
        Console.WriteLine($"  ExuStreets:                     {seedData.ExuStreets.Count}");
        Console.WriteLine($"  ExuMeterTableTrades:            {seedData.ExuMeterTableTrades.Count}");
        Console.WriteLine($"  ExuGroupTables:                 {seedData.ExuGroupTables.Count}");
        Console.WriteLine($"  ExuInstallations:               {seedData.ExuInstallations.Count}");
        Console.WriteLine($"  ExuMeterJournalTrans:           {seedData.ExuMeterJournalTrans.Count}");
        Console.WriteLine();

        if (seedData.CustTables.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Skipping: account {account} not found in Xellent");
            Console.ResetColor();
            return 0;
        }

        // --- Write Xellent seed data (always) ---
        var outputDir = Path.Combine(outputPath, account);
        Directory.CreateDirectory(outputDir);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        var xellentPath = Path.Combine(outputDir, "xellent.json");
        await File.WriteAllTextAsync(xellentPath, JsonSerializer.Serialize(seedData, jsonOptions), cancellationToken);
        Console.WriteLine($"Written: {xellentPath}");

        // --- Settl API queries (optional — account may not be in Settl yet) ---
        var settlData = new SettlAccountSeedData();

        Console.Write("Querying Settl customer... ");
        var settlCustomerNumber = CustomerNumberPolicy.PadForEnv(account, env, out var wasPadded);
        if (wasPadded)
            Console.Error.WriteLine($"  WARNING: CustomerNumber {account} padded to {settlCustomerNumber} for env");
        var (customer, customerError) = await _customerClient.SearchByCustomerNumberAsync(settlCustomerNumber, cancellationToken: cancellationToken);
        if (customerError != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: {customerError}");
            Console.ResetColor();
        }
        else if (customer == null)
        {
            Console.WriteLine("not found in Settl");
        }
        else
        {
            settlData.Customer = customer;
            Console.Write($"{customer.CustomerNumber} ({customer.CustomerType})");

            // Fetch full customer
            var (customerFull, _) = await _customerClient.GetByIdAsync(customer.Id, cancellationToken);
            if (customerFull != null) settlData.CustomerFull = customerFull;
            Console.WriteLine(customerFull != null ? " + full" : "");
        }

        // Metering points — always search, independent of customer existence
        // (grouped accounts may not have their own Settl customer but their MPs exist)
        var uniqueMeteringPoints = seedData.ExuDelpoints
            .Select(d => d.Meteringpoint)
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct()
            .ToList();
        if (uniqueMeteringPoints.Count > 0)
        {
            Console.Write($"Querying Settl metering points ({uniqueMeteringPoints.Count})... ");
            foreach (var mpId in uniqueMeteringPoints)
            {
                var (mp, mpError) = await _meteringPointClient.SearchByIdentificationAsync(mpId, cancellationToken);
                if (mp != null)
                {
                    settlData.MeteringPoints.Add(mp);
                    var (rawJson, _) = await _meteringPointClient.GetByIdRawAsync(mp.Id, cancellationToken);
                    if (rawJson != null) settlData.MeteringPointsRaw.Add(rawJson);
                }
            }
            Console.WriteLine($"{settlData.MeteringPoints.Count} found, {settlData.MeteringPointsRaw.Count} full");
        }

        // Payer — always search, independent of customer existence
        string payerIdentification;
        if (payerIdOverrides != null && payerIdOverrides.TryGetValue(account, out var overrideId))
        {
            payerIdentification = overrideId;
        }
        else
        {
            var mainContract = seedData.ExuContractTables.FirstOrDefault();
            payerIdentification = !string.IsNullOrEmpty(mainContract?.Custpurchaseorder)
                ? mainContract.Custpurchaseorder
                : account;
        }
        var paddedPayerIdentification = CustomerNumberPolicy.PadForEnv(payerIdentification, env, out var wasPayerPadded);
        if (wasPayerPadded)
            Console.Error.WriteLine($"  WARNING: PayerIdentification {payerIdentification} padded to {paddedPayerIdentification} for env");
        Console.Write($"Querying Settl payer ({paddedPayerIdentification})... ");
        var (payer, payerError) = await _payerClient.SearchByPayerIdentificationAsync(paddedPayerIdentification, cancellationToken: cancellationToken);
        if (payerError != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: {payerError}");
            Console.ResetColor();
        }
        else if (payer != null)
        {
            settlData.Payers.Add(payer);
            var (payerFull, _) = await _payerClient.GetByIdAsync(payer.Id, null, cancellationToken);
            if (payerFull != null) settlData.PayersFull.Add(payerFull);
            Console.WriteLine($"found (InvoiceChannel={payer.InvoiceChannel})" + (payerFull != null ? " + full" : ""));
        }
        else
        {
            Console.WriteLine("not found");
        }

        Console.WriteLine();

        var settlPath = Path.Combine(outputDir, "settl.json");
        await File.WriteAllTextAsync(settlPath, JsonSerializer.Serialize(settlData, jsonOptions), cancellationToken);
        Console.WriteLine($"Written: {settlPath}");

        return 0;
    }

    private static async Task<XellentSeedData> QueryXellentAsync(
        XellentDbContext context, string account, CancellationToken cancellationToken)
    {
        // Step 1: CustTables
        var custTables = await context.CustTables
            .Where(c => c.Dataareaid == "hol" && c.Accountnum == account)
            .ToListAsync(cancellationToken);

        var partyIds = custTables
            .Select(c => c.Partyid)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct().ToList();

        // Step 2: ExuContractTables
        var contractTables = await context.ExuContractTables
            .Where(c => c.Dataareaid == "hol" && c.Custaccount == account)
            .ToListAsync(cancellationToken);

        var contractNums = contractTables.Select(c => c.Contractnum).Distinct().ToList();

        // Step 3: ExuContractPartTables
        var contractPartTables = contractNums.Count > 0
            ? await context.ExuContractPartTables
                .Where(c => c.Dataareaid == "hol" && contractNums.Contains(c.Contractnum))
                .ToListAsync(cancellationToken)
            : [];

        var instagreenums = contractPartTables
            .Select(c => c.Instagreenum)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct().ToList();

        // Step 4: DirPartyAddressRelationships
        var partyAddrRels = partyIds.Count > 0
            ? await context.DirPartyAddressRelationships
                .Where(r => r.Dataareaid == "hol" && partyIds.Contains(r.Partyid))
                .ToListAsync(cancellationToken)
            : [];

        var partyAddrRecids = partyAddrRels.Select(r => r.Recid).Distinct().ToList();

        // Step 5: DirPartyAddressRelationshi1066s
        var partyAddrLinks = partyAddrRecids.Count > 0
            ? await context.DirPartyAddressRelationshi1066s
                .Where(l => l.Dataareaid == "hol" && partyAddrRecids.Contains(l.Partyaddressrelationshiprecid))
                .ToListAsync(cancellationToken)
            : [];

        var addressRecids = partyAddrLinks.Select(l => l.Addressrecid).Distinct().ToList();

        // Step 6: Addresses
        var addresses = addressRecids.Count > 0
            ? await context.Addresses
                .Where(a => a.Dataareaid == "hol" && addressRecids.Contains(a.Recid))
                .ToListAsync(cancellationToken)
            : [];

        // Step 7: ExuAgreementTables (fallback legal address)
        var agreementTables = instagreenums.Count > 0
            ? await context.ExuAgreementTables
                .Where(a => a.Dataareaid == "hol" && instagreenums.Contains(a.Agreementnum))
                .ToListAsync(cancellationToken)
            : [];

        var agreementnums = agreementTables.Select(a => a.Agreementnum).Distinct().ToList();

        // Step 8: ExuDelpoints (fallback legal address)
        var delpoints = agreementnums.Count > 0
            ? await context.ExuDelpoints
                .Where(d => d.Dataareaid == "hol" && agreementnums.Contains(d.Attachmentnum))
                .ToListAsync(cancellationToken)
            : [];

        var meteringPoints = delpoints
            .Select(d => d.Meteringpoint)
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct().ToList();

        // Step 9: ExuDelpointContactInfoTables (fallback legal address)
        var contactInfoTables = meteringPoints.Count > 0
            ? await context.ExuDelpointContactInfoTables
                .Where(c => c.Dataareaid == "hol" && meteringPoints.Contains(c.Meteringpoint))
                .ToListAsync(cancellationToken)
            : [];

        var zipcodes = contactInfoTables
            .Select(c => c.Zipcode)
            .Where(z => !string.IsNullOrEmpty(z))
            .Distinct().ToList();

        // Step 10: AddressZipCodes (fallback legal address)
        var addressZipCodes = zipcodes.Count > 0
            ? await context.AddressZipCodes
                .Where(z => z.Dataareaid == "hol" && zipcodes.Contains(z.Zipcode))
                .ToListAsync(cancellationToken)
            : [];

        var delpointNums = delpoints.Select(d => d.Num).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();

        // Step 10b: EgsMeterpointTypecodeperiods (AccountQueryService)
        // Query by Attachmentnum (= agreement number) to capture El-ekstern-specific rows.
        // AccountQueryService joins on {Delpointnum, Attachmentnum, Companyid, Deliverycategory},
        // so querying by Attachmentnum ensures we get exactly the rows that match the delpoints.
        var meterTypePeriods = agreementnums.Count > 0
            ? await context.EgsMeterpointTypecodeperiods
                .Where(m => m.Dataareaid == "hol" && agreementnums.Contains(m.Attachmentnum))
                .ToListAsync(cancellationToken)
            : [];

        // Step 10c: ExuBillingMethodPeriods (AccountQueryService)
        var billingMethodPeriods = agreementnums.Count > 0
            ? await context.ExuBillingMethodPeriods
                .Where(b => b.Dataareaid == "hol" && agreementnums.Contains(b.Attachmentnum))
                .ToListAsync(cancellationToken)
            : [];

        // Step 10d: ExuDelpoint2delpointrela25505s (AccountQueryService child metering point exclusion)
        // Filter by both Primarydelpointnum AND Primaryattachmentnum to avoid pulling the entire table
        // (Primarydelpointnum values like "1", "2", "3" are shared across all accounts)
        var delpointRelations = delpointNums.Count > 0 && agreementnums.Count > 0
            ? await context.ExuDelpoint2delpointrela25505s
                .Where(r => r.Dataareaid == "hol"
                    && delpointNums.Contains(r.Primarydelpointnum)
                    && agreementnums.Contains(r.Primaryattachmentnum))
                .ToListAsync(cancellationToken)
            : [];

        // Step 11: DirPartyECommunicationRela2608s (fallback email)
        var commRelas = partyIds.Count > 0
            ? await context.DirPartyECommunicationRela2608s
                .Where(r => r.Dataareaid == "hol" && partyIds.Contains(r.Partyid))
                .ToListAsync(cancellationToken)
            : [];

        var valuesRecids = commRelas.Select(r => r.Valuesrecid).Distinct().ToList();

        // Step 12: DirECommunicationAddresses (fallback email)
        var commAddresses = valuesRecids.Count > 0
            ? await context.DirECommunicationAddresses
                .Where(a => a.Dataareaid == "hol" && valuesRecids.Contains(a.Recid))
                .ToListAsync(cancellationToken)
            : [];

        // --- Metering point extraction tables ---

        // Build delpoint composite keys for queries that need (Num, Attachmentnum)
        var delpointKeys = delpoints
            .Select(d => new { d.Num, d.Attachmentnum })
            .Distinct().ToList();

        // Step 13: ExuStreets (metering point addresses)
        var streetKeys = delpoints
            .Select(d => new { d.Streetnum, d.Countynum })
            .Where(k => !string.IsNullOrEmpty(k.Streetnum))
            .Distinct().ToList();
        var streetNums = streetKeys.Select(k => k.Streetnum).Distinct().ToList();

        var streets = streetNums.Count > 0
            ? await context.ExuStreets
                .Where(s => s.Dataareaid == "hol" && streetNums.Contains(s.Streetnum))
                .ToListAsync(cancellationToken)
            : [];

        // Step 14: ExuMeterTableTrades (meter assignments per delpoint)
        var meterTableTrades = agreementnums.Count > 0
            ? await context.ExuMeterTableTrades
                .Where(m => m.Dataareaid == "hol" && agreementnums.Contains(m.Agreementnum))
                .ToListAsync(cancellationToken)
            : [];

        // Step 15: ExuGroupTables (customer groups)
        var groupNums = custTables
            .Select(c => c.ExuTechnicgroup4)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct().ToList();

        var groupTables = groupNums.Count > 0
            ? await context.ExuGroupTables
                .Where(g => g.Dataareaid == "hol" && groupNums.Contains(g.Groupnum))
                .ToListAsync(cancellationToken)
            : [];

        // Step 19: ExuInstallations (installation-based extraction path)
        var installations = instagreenums.Count > 0
            ? await context.ExuInstallations
                .Where(i => i.Dataareaid == "hol" && instagreenums.Contains(i.Instnum))
                .ToListAsync(cancellationToken)
            : [];

        var installationNums = installations.Select(i => i.Instnum).Distinct().ToList();

        // Step 22: ExuMeterJournalTrans (installation meter readings)
        var meterJournalTrans = installationNums.Count > 0
            ? await context.ExuMeterJournalTrans
                .Where(m => m.Dataareaid == "hol" && installationNums.Contains(m.Installationnum))
                .ToListAsync(cancellationToken)
            : [];

        return new XellentSeedData
        {
            CustTables = custTables,
            ExuContractTables = contractTables,
            ExuContractPartTables = contractPartTables,
            DirPartyAddressRelationships = partyAddrRels,
            DirPartyAddressRelationshi1066s = partyAddrLinks,
            Addresses = addresses,
            ExuAgreementTables = agreementTables,
            ExuDelpoints = delpoints,
            ExuDelpointContactInfoTables = contactInfoTables,
            AddressZipCodes = addressZipCodes,
            DirPartyECommunicationRela2608s = commRelas,
            DirECommunicationAddresses = commAddresses,
            EgsMeterpointTypecodeperiods = meterTypePeriods,
            ExuBillingMethodPeriods = billingMethodPeriods,
            ExuDelpoint2delpointrela25505s = delpointRelations,
            ExuStreets = streets,
            ExuMeterTableTrades = meterTableTrades,
            ExuGroupTables = groupTables,
            ExuInstallations = installations,
            ExuMeterJournalTrans = meterJournalTrans
        };
    }
}
