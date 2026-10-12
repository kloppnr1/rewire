# NewSettlement.XellentDomain

Producer-repoet i bunden af afhængighedsgrafen. Alt hvad der ændres her, når ud i hvert andet NewSettlement-repo gennem NuGet. Behandl enhver ændring af en offentlig type eller signatur som en tværgående begivenhed.

## Projekter og pakker

| Projekt | Pakke | Indhold |
|---|---|---|
| `DataMigration.Core` | `NewSettlement.XellentDomain.Core` | Fælles modeller og hjælpere — konstanter, WarningCollector, CSV |
| `DataMigration.Domain` | `…Domain` | Kildeuafhængig domænemodel (Customer, Contract, MeteringPoint …) |
| `DataMigration.Domain.Engros` | `…Domain.Engros` | Engros-typer (afregningsgrundlag) |
| `DataMigration.Domain.TimeSeries` | `…Domain.TimeSeries` | Tidsserie-typer (målte målepunktsværdier) |
| `DataMigration.Domain.Xellent` | `…Domain.Xellent` | Xellent → Domain-mapper + forespørgselshjælpere (fakturaer, kontraktdele, kontogrupperinger) |
| `DataMigration.XellentData` | `…XellentData` | EF Core-entiteter for Xellent-databasen (Dynamics AX 2009) |

Assembly- og namespace-navne hedder stadig `DataMigration.*`, så consumers' `using`-linjer virker uændret. Repoet hed tidligere UtilityData; ældre dokumenter bruger det navn.

Unit tests ligger i `src/Tests/`. Den levende Xellent-regressionssuite bor i `NewSettlement.DataMigration`, fordi den kører hele extraction-pipelinen.

## Hvad repoet ejer

- **Den eneste definition af `MigrationConstants.EndDateNull` (`1900-01-01`) og `EndDateFuture` (`2154-12-31`).** Der er ingen fork at holde i sync længere — compileren håndhæver det gennem pakken. Kopier der stadig ligger i `Tools` og `UtilityOperations` er ude af brug; lad dem ligge.
- **Xellent EF-entiteterne i `src/DataMigration.XellentData/Entities/`** — sandhedskilden for SQL-kolonnenavne. Entitets-properties dropper underscores, SQL-kolonnerne beholder dem, og det er `[Column(...)]`-attributten der tæller. Gæt aldrig et kolonnenavn.

## Dokumentation — læs før du skriver en forespørgsel

- `docs/xellent-datamodel.md` — hvordan Xellent selv hænger sammen: kæden fra kontrakt til målepunkt, hoved- og underkontrakter, paraply-kontrakter uden leveringskategori, betalingsaftalernes felter, dato-sentinels, og hvem der ejer et målepunkt. Den findes fordi netop de forhold er blevet gættet forkert gentagne gange. Hver påstand er enten målt med dato eller markeret som udledt.
- `docs/domain-model.md` — domænetyperne og loader-kontrakten.

## Versionering

`Directory.Build.props` holder `<VersionPrefix>` + `<VersionSuffix>dev</VersionSuffix>` som eneste sandhedskilde. Csproj sætter `<PackageId>`, men **aldrig** `<Version>` — en hardkodet version dér overskriver props og bryder modellen.

Serien er i dag **0.8.x**. CI publicerer `{version}-preview.{BuildId}` ved hver commit til develop.

Skal consumers hente en ændring: bump `VersionPrefix` — og tjek feedet først. Sætter du prefix under en eksisterende preview, bliver floating consumers ved med at hente den gamle, højere version.

**Serie-skift floater ikke.** En consumer med `0.7.*-*` følger ikke med til 0.8.x. Skifter du serie, skal hver consumer rettes i hånden — find dem med et grep efter `NewSettlement.XellentDomain` i `*.csproj` og `Directory.Packages.props` på tværs af repos.

**Pak aldrig lokalt for at teste en tværgående ændring.** Lokal `dotnet pack` giver `-dev`, som sorterer under feedets `-preview.{BuildId}`, så floating refs ignorerer pakken. Flowet er: bump → commit → vent på CI → restore i consumer.

Bemærk: `README.md`'s versionerings-afsnit er forældet (det beskriver manuel `<Version>` og `0.1.0-preview`). Props er det der gælder.

## Consumers

DataMigration, MigrationDashboard, TimeSeriesMigration, SettlClient og CustomerSupport (via Central Package Management). Før du ændrer eller fjerner noget offentligt: grep consumer-repos for brugssteder og rapportér hvad der ville knække. De repos er read-only herfra — du rapporterer, du retter ikke.

## Verifikation før du melder færdig

1. `dotnet build`
2. `dotnet test` — alle fire testprojekter
3. Ved ændret offentlig signatur: grep consumers og list hvert kaldsted der rammes

Consumer-siden kan ikke verificeres herfra — det kræver at CI har publiceret først. Sig det eksplicit i stedet for at lade det fremstå som bevist.

## Grænser

- Xellent (`xel2012`) er et produktionssystem — kun læsning. Brug `xellent-investigator`-agenten til databasespørgsmål.
- Commits går til `develop`, aldrig direkte til `master`. `develop → master` sker via release-PR, og det er brugerens beslutning hvornår.
