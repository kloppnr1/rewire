namespace DataMigration.Migration.Runners;

/// <summary>
/// De faser en kørsel melder ind gennem <see cref="CreateRunner.OnPhase"/>, samlet ét sted så
/// den der viser dem (dashboardet oversætter til dansk) kan matche på en kendt værdi i stedet
/// for at gætte på en tekst. Navnene er engelske som de to der var der i forvejen.
///
/// <para>Fasernes rækkefølge i en fuld, levende kørsel: <see cref="LoadingXellentData"/> (meldt
/// af <see cref="Services.MigrationDomainLoad"/>, som kører før kørslen får sit udtræk),
/// <see cref="PreloadingSettlCache"/>, <see cref="ResolvingContracts"/>,
/// <see cref="CreatingMeteringPoints"/>, <see cref="MigratingCustomers"/>. Kun de to sidste har
/// et tal at tælle med — og de tæller <b>hver sin enhed</b>: målepunkter henholdsvis kunder.
/// Derfor står enheden i fasenavnet.</para>
///
/// <para>En kørsel springer de faser over den ikke laver: offline henter hverken snapshot eller
/// kontrakt-id'er, en dry-run opretter ingen målepunkter, og en målepunkts-kørsel stopper før
/// kunderne. En fase meldes kun ind når den faktisk har arbejde.</para>
/// </summary>
public static class MigrationPhases
{
    /// <summary>Udtrækket fra Xellent. Ingen deltælling at hente — den ene forespørgsel er
    /// enten undervejs eller færdig — og det er med vilje at der ikke opfindes en.</summary>
    public const string LoadingXellentData = "Loading Xellent data";

    /// <summary>Opvarmningen af Settls øjebliksbillede af kunder, betalere og målepunkter.</summary>
    public const string PreloadingSettlCache = "Preloading Settl cache";

    /// <summary>Kontraktnavne slås op og oversættes til Settl-kontrakt-id'er.</summary>
    public const string ResolvingContracts = "Resolving contracts";

    /// <summary>El-målepunkterne oprettes i bundter, før den første kunde. Tæller
    /// <b>målepunkter</b>, ikke konti.</summary>
    public const string CreatingMeteringPoints = "Creating metering points";

    /// <summary>Kunde-løkken: kunde, betalere, faktureringsopsætning og koblinger. Tæller
    /// kunder.</summary>
    public const string MigratingCustomers = "Migrating customers and links";
}
