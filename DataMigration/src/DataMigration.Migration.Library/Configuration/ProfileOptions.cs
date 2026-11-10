namespace DataMigration.Migration.Configuration;

public class ProfileOptions
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Explicit migration scope. Empty when the scope is resolved at run time instead
    /// (<c>create --all-active</c>), which is the normal case for whole-commodity runs.
    /// </summary>
    public string[] Accounts { get; set; } = [];

    public required ExtractionOptions Extraction { get; set; }
    public MappingOptions? Mappings { get; set; }
}

public class ExtractionOptions
{
    public string DataAreaId { get; set; } = "hol";
    public required string DeliveryCategory { get; set; }
    public Dictionary<string, string>? GroupOwners { get; set; }
    public Dictionary<string, string>? AccountOwners { get; set; }
    public DateTime? ChangeOfSupplierDate { get; set; }
    public DateTime? ReferenceDate { get; set; }

    /// <summary>
    /// Single source of truth for the extraction reference date.
    /// Explicit ReferenceDate takes precedence; otherwise ChangeOfSupplierDate − 1 day.
    /// Returns null only when neither is set (CLI validates and throws in that case).
    /// </summary>
    public DateTime? EffectiveReferenceDate => ReferenceDate ?? ChangeOfSupplierDate?.AddDays(-1);

    public string[]? MeterPointTypeCodes { get; set; }

    /// <summary>
    /// TechnicGroup4-værdier der hverken må udløse representative-merge eller danne en
    /// OwnerCluster. Konti i disse grupper migreres standalone (egen Settl-customer), uanset
    /// CVR+Fingerprint-match.
    ///
    /// <para>Kun grupper der er særlige for netop denne profil hører til her. De grupper der
    /// gælder hele dataområdet (Medarbej, Hybel, Velk strøm) står i appsettings og lægges
    /// automatisk til — se <see cref="IgnoredGroupsLoader"/>. Profilens egne grupper lægges
    /// oveni basen; de erstatter den ikke, så en profil kan ikke tavst genåbne merge for
    /// medarbejderkonti. Tom/null betyder derfor "ingen ud over basen".</para>
    /// </summary>
    public string[]? IgnoredGroups { get; set; }

    /// <summary>
    /// Bagud-kompatibilitet for ældre profiler: når true bruges den gamle
    /// (CVR + ExuTechnicgroup4) merge-regel i stedet for default (CVR + Fingerprint).
    /// Bevares for profiler hvor TG4 bevidst samler kunder med forskellige adresser
    /// til én Settl-customer (fx PPA Spar Nord — 94 filialer under én customer).
    /// </summary>
    public bool LegacyTg4Merge { get; set; }

}

public class MappingOptions
{
    public Dictionary<string, string>? GroupNum { get; set; }
    public Dictionary<string, string>? AccountNum { get; set; }
    public Dictionary<string, ProductNumContractEntry>? ProductNum { get; set; }

    /// <summary>
    /// Xellent contract template (<c>ExuContractTable.Contracttemplatenum</c>) → product pattern →
    /// the Settl contract, per customer identification. More specific than <see cref="ProductNum"/>
    /// and consulted before it: it is the only way to say that one product maps two ways depending
    /// on which template it sits on and whether the customer has a CVR.
    ///
    /// <para>Merged per template, not per product: a profile that names a template replaces the
    /// base's product rules for that template outright.</para>
    /// </summary>
    public Dictionary<string, Dictionary<string, TemplateProductContractEntry>>? TemplateProducts { get; set; }

    /// <summary>
    /// El-ekstern productnum → charger's live Charge_Abo productnum (or
    /// <see cref="ElectricityChargerContractRules.NoChargeKey"/>) → Settl contract. Only consulted
    /// for metering points whose account has a linked EV charger — see
    /// <see cref="DataMigration.Migration.Services.ContractNameResolver"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? ElectricityChargerContractMap { get; set; }

    public Dictionary<string, string>? GridArea { get; set; }

    /// <summary>
    /// Maps Xellent <c>ExuContractTable.Billingplannum</c> to a Settl Settlement Group name.
    /// When null, the AssignSettlementGroups pipeline step is skipped entirely.
    /// When present but a metering point's BillingPlanNum is missing/unmapped, the bundle fails.
    /// </summary>
    public Dictionary<string, string>? SettlementGroup { get; set; }
}
