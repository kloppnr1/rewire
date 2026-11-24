using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlClient;
using DataMigration.XellentData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Runs a full migration profile with DryRun=true against seeded Xellent data.
/// Same pipeline as production (CreateRunner), but no Settl API calls.
/// </summary>
public sealed class ProfileTestRunner : IDisposable
{
    private readonly SqliteConnection _connection;

    public XellentDbContext Context { get; }
    public MigrationReport Report { get; }
    public ProfileOptions Profile { get; }

    /// <summary>Exit code from <see cref="CreateRunner.RunAsync"/> — 1 when the run failed or aborted.</summary>
    public int ExitCode { get; }

    /// <summary>Per-customer outcomes in the order the runner emitted them.</summary>
    public List<CustomerOutcome> Outcomes { get; } = [];

    /// <summary>Payloads captured in capture-mode (reconcile). Empty unless <c>capture: true</c>.</summary>
    public List<SettlMigrationPlan> CapturePlans { get; } = [];

    /// <summary>Den fælles Settl-kontekst kørslen byggede — sådan som valgene i
    /// <see cref="CreateOptions"/> faktisk landede.</summary>
    public SettlMigrationContext? MigrationContext { get; private set; }

    /// <summary>Faserne i den rækkefølge kørslen meldte dem, afslutningens null medregnet.</summary>
    public List<string?> Phases { get; } = [];

    /// <summary>Hver fremdrifts-melding med den fase der var i gang da den kom — enheden følger
    /// fasen, så tallene kan ikke læses uden den.</summary>
    public List<(string? Phase, int Done, int Total)> Progress { get; } = [];

    public ProfileTestRunner(
        string profileName,
        bool offline = false,
        string[]? selectedAccounts = null,
        bool continueOnError = false,
        bool disableMerge = false,
        // Overskriver profilens eget valg. PPA-profilerne kører den gamle TG4-regel, og den
        // regner slet ikke fingeraftryk — en test om fingerprint-splits skal kunne slå den fra.
        bool? legacyTg4Merge = null,
        // Målepunkts-fasen kører kun i en levende kørsel, så en test af den kan ikke være dry-run.
        // Klienterne er mocks, så "levende" betyder her bare at skrive-stierne tages.
        bool dryRun = true,
        bool meteringPointsOnly = false,
        // null = lad standarden stå, så en test kan skelne "ikke sat" fra et valgt tal.
        int? searchConcurrency = null,
        Action<MigrationReport>? onExtractionComplete = null,
        // Egne klienter i stedet for de tomme mocks — så en test kan tælle hvad kørslen faktisk
        // spurgte Settl om, fx om den paginerede kunder og betalere den aldrig skulle bruge.
        ISettlCustomerClient? customerClient = null,
        ISettlPayerClient? payerClient = null,
        // The default mock answers with neither a contract list nor an error, so the run cannot
        // conclude that a name is missing. A test of exactly that must hand in a client that answers.
        ISettlContractClient? contractClient = null,
        // Non-null puts the runner in capture-mode (reconcile): same prep, payloads captured
        // instead of POSTed. The captured plans land in <see cref="CapturePlans"/>.
        bool capture = false)
    {
        var profilesDir = FindProfilesDir();
        var loader = new ProfileLoader(profilesDir);
        Profile = loader.Load(profileName);

        // Merge seed data from all profile accounts
        var seeds = Profile.Accounts
            .Select(a => { try { return XellentDbSeeder.LoadRawSeed(a); } catch (FileNotFoundException) { return null; } })
            .Where(s => s != null)
            .Cast<XellentSeedData>();

        var merged = MultiAccountXellentDbSeeder.Merge(seeds);
        XellentDbSeeder.ActivateContracts(merged);
        MultiAccountXellentDbSeeder.NormalizeDeliveryCategory(merged);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new XellentDbContext(options);
        Context.Database.EnsureCreated();
        SeedContext(Context, merged);
        Context.SaveChanges();

        // Run CreateRunner with DryRun — same pipeline as production
        var createOptions = new CreateOptions
        {
            Accounts = Profile.Accounts.Distinct().ToArray(),
            DataAreaId = Profile.Extraction.DataAreaId,
            DeliveryCategory = Profile.Extraction.DeliveryCategory,
            DryRun = dryRun,
            Offline = offline,
            MeteringPointsOnly = meteringPointsOnly,
            IncludeEndedCutoff = new DateTime(2000, 1, 1),
            ReferenceDate = new DateTime(2026, 4, 16),
            GroupOwners = Profile.Extraction.GroupOwners,
            AccountOwners = Profile.Extraction.AccountOwners,
            GroupNumContractMap = Profile.Mappings?.GroupNum,
            AccountNumContractMap = Profile.Mappings?.AccountNum,
            LegacyTg4Merge = legacyTg4Merge ?? Profile.Extraction.LegacyTg4Merge,
            SelectedAccounts = selectedAccounts,
            ContinueOnError = continueOnError,
            DisableRepresentativeMerge = disableMerge,
        };
        if (searchConcurrency is > 0) createOptions.SearchConcurrency = searchConcurrency.Value;

        var warnings = new WarningCollector();
        var commodity = createOptions.DeliveryCategory switch
        {
            MigrationConstants.DeliveryCategoryWater => Commodity.Water,
            MigrationConstants.DeliveryCategoryHeating => Commodity.Heat,
            MigrationConstants.DeliveryCategoryCharge => Commodity.EvCharging,
            _ => Commodity.Electricity
        };
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(createOptions.ReferenceDate),
            Commodity: commodity,
            IncludeEndedCutoff: new DateOnly(2000, 1, 1),
            LegacyTg4Merge: createOptions.LegacyTg4Merge,
            DisableRepresentativeMerge: createOptions.DisableRepresentativeMerge);
        var mapper = new XellentDomainMapper(Context, warnings, mapperOptions, createOptions.DataAreaId);
        var loadResult = mapper.LoadAsync(createOptions.Accounts).GetAwaiter().GetResult();

        var apiOptions = new SettlApiOptions { BaseUrl = "http://test" };
        var runner = new CreateRunner(
            apiOptions,
            customerClient ?? new MockCustomerClient(), payerClient ?? new MockPayerClient(),
            new MockMeteringPointClient(), contractClient ?? new MockContractClient(),
            new MockSettlementGroupClient(), warnings);

        string? currentPhase = null;
        runner.OnPhase = phase => { currentPhase = phase; Phases.Add(phase); };
        runner.OnProgress = (done, total) => Progress.Add((currentPhase, done, total));
        runner.OnCustomerCompleted = outcome => Outcomes.Add(outcome);
        runner.OnMigrationContextBuilt = ctx => MigrationContext = ctx;
        if (onExtractionComplete != null) runner.OnExtractionComplete = onExtractionComplete;

        var plans = capture ? CapturePlans : null;
        var (exitCode, report) = runner.RunAsync(createOptions, loadResult, default, plans).GetAwaiter().GetResult();
        ExitCode = exitCode;
        Report = report;
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }

    private static string FindProfilesDir()
    {
        var dir = Path.GetDirectoryName(typeof(ProfileTestRunner).Assembly.Location)!;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "profiles");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("profiles directory not found");
    }

    private static void SeedContext(XellentDbContext context, XellentSeedData data)
    {
        if (data.CustTables.Count > 0) context.CustTables.AddRange(data.CustTables);
        if (data.ExuContractTables.Count > 0) context.ExuContractTables.AddRange(data.ExuContractTables);
        if (data.ExuContractPartTables.Count > 0) context.ExuContractPartTables.AddRange(data.ExuContractPartTables);
        if (data.DirPartyAddressRelationships.Count > 0) context.DirPartyAddressRelationships.AddRange(data.DirPartyAddressRelationships);
        if (data.DirPartyAddressRelationshi1066s.Count > 0) context.DirPartyAddressRelationshi1066s.AddRange(data.DirPartyAddressRelationshi1066s);
        if (data.Addresses.Count > 0) context.Addresses.AddRange(data.Addresses);
        if (data.ExuAgreementTables.Count > 0) context.ExuAgreementTables.AddRange(data.ExuAgreementTables);
        if (data.ExuDelpoints.Count > 0) context.ExuDelpoints.AddRange(data.ExuDelpoints);
        if (data.ExuDelpointContactInfoTables.Count > 0) context.ExuDelpointContactInfoTables.AddRange(data.ExuDelpointContactInfoTables);
        if (data.AddressZipCodes.Count > 0) context.AddressZipCodes.AddRange(data.AddressZipCodes);
        if (data.DirPartyECommunicationRela2608s.Count > 0) context.DirPartyECommunicationRela2608s.AddRange(data.DirPartyECommunicationRela2608s);
        if (data.DirECommunicationAddresses.Count > 0) context.DirECommunicationAddresses.AddRange(data.DirECommunicationAddresses);
        if (data.EgsMeterpointTypecodeperiods.Count > 0) context.EgsMeterpointTypecodeperiods.AddRange(data.EgsMeterpointTypecodeperiods);
        if (data.ExuBillingMethodPeriods.Count > 0) context.ExuBillingMethodPeriods.AddRange(data.ExuBillingMethodPeriods);
        if (data.ExuDelpoint2delpointrela25505s.Count > 0) context.ExuDelpoint2delpointrela25505s.AddRange(data.ExuDelpoint2delpointrela25505s);
        if (data.ExuStreets.Count > 0) context.ExuStreets.AddRange(data.ExuStreets);
        if (data.ExuMeterTableTrades.Count > 0) context.ExuMeterTableTrades.AddRange(data.ExuMeterTableTrades);
        if (data.ExuGroupTables.Count > 0) context.ExuGroupTables.AddRange(data.ExuGroupTables);
        if (data.ExuInstallations.Count > 0) context.ExuInstallations.AddRange(data.ExuInstallations);
        if (data.ExuMeterJournalTrans.Count > 0) context.ExuMeterJournalTrans.AddRange(data.ExuMeterJournalTrans);
    }
}
