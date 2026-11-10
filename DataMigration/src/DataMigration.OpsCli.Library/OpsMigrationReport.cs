using DataMigration.Core.Models;
using DataMigration.Migration.Runners;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Identifikations-felterne som de står i Xellent for en kunde der hverken har CVR eller CPR.
/// De normaliserede felter er tomme for netop hende — mapperen kasserer et CVR der ikke er 8
/// cifre og et CPR der ikke er 10 tegn — så modalen viser to tomme felter og intet svar på
/// hvorfor. <see cref="CauseText"/> er gruppeoverskriften fra
/// <c>MissingIdentityClassifier</c>, den samme som create-rapporten grupperer efter, så de to
/// rapporter ikke siger hver sit om samme kunde.
/// </summary>
public sealed record UnknownIdentitySource(
    string? RawCvr,
    string? RawCpr,
    string? AlternativeNumber,
    int AlternativeNumberCode,
    string CauseText);

public class OpsMigrationReport
{
    /// <summary>Kontonummer → hvad der faktisk står i Xellent. Kun kunder uden CVR og CPR.</summary>
    public Dictionary<string, UnknownIdentitySource> UnknownIdentities { get; set; } = [];

    public List<AccountGrouping> AccountGroupings { get; set; } = [];
    public Dictionary<string, List<string>> AccountMeteringPoints { get; set; } = [];
    public Dictionary<string, List<string>> ContractMeteringPoints { get; set; } = [];
    public int CustomersExtracted { get; set; }
    public int CustomersCreated { get; set; }
    public int CustomersExisting { get; set; }
    public int CustomersFailed { get; set; }
    public int PayersExtracted { get; set; }
    public int PayersCreated { get; set; }
    public int PayersExisting { get; set; }
    public int PayersFailed { get; set; }
    public int MeteringPointsExtracted { get; set; }
    public int MeteringPointsCreated { get; set; }
    public int MeteringPointsExisting { get; set; }
    public int MeteringPointsFailed { get; set; }
    public int CustomerMpsLinked { get; set; }
    public int CustomerMpsFailed { get; set; }
    public int PayerMpsLinked { get; set; }
    public int PayerMpsFailed { get; set; }
    public int ContractMpsLinked { get; set; }
    public int ContractMpsFailed { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Log { get; set; } = [];
    public List<CreateCustomerDto> Customers { get; set; } = [];
    public List<CreatePayerDto> Payers { get; set; } = [];
    public List<MeteringPointDto> MeteringPoints { get; set; } = [];
    public HashSet<string> JoinedMeteringPoints { get; set; } = [];
    public Dictionary<Guid, string> ExternalOwners { get; set; } = [];
    public Dictionary<string, string> MpContractNames { get; set; } = [];
    public Dictionary<string, string> MpPriceAreas { get; set; } = [];
    public Dictionary<string, string> MpProductNums { get; set; } = [];
    /// <summary>Per-MP e-faktura-reference override (mp-identification → divergerende felter på MP-niveau). Kun MPs hvor BuyerInvoicing != null.</summary>
    public Dictionary<string, MpBillingOverrideInfo> MpBillingOverrides { get; set; } = [];
}

public class CreateOptions
{
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public string DeliveryCategory { get; set; } = "El-ekstern";
    public bool DryRun { get; set; }
    public Dictionary<string, string>? GroupOwners { get; set; }
    public Dictionary<string, string>? AccountOwners { get; set; }
    public Dictionary<string, DataMigration.Migration.Configuration.ProductNumContractEntry>? ProductNumContractMap { get; set; }
    public Dictionary<string, string>? GroupNumContractMap { get; set; }
    public Dictionary<string, string>? AccountNumContractMap { get; set; }
    public Dictionary<string, string>? GridAreaMap { get; set; }
    public DateTime? IncludeEndedCutoff { get; set; }
}
