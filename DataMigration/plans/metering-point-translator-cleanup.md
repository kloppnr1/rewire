> **✅ IMPLEMENTERET** — `c341a7ed`
>

# MeteringPointTranslator → ren 1:1 mapping

> **Multi-repo:** `NewSettlement.XellentDomain` (Repo 1), `NewSettlement.SettlClient` (Repo 2), `NewSettlement.DataMigration` (Repo 3 / her). Forudsætter at payer-refaktor branch `refactor/payer-fanout-to-domain` er committet (✓).

## Verifikation (start her)

### Trin 1 — Unit tests pr. ny owner

**Du kører** (Repo 1):
```
dotnet test src\Tests\DataMigration.Domain.Xellent.Tests --filter MeteringPoint
```
**Forventet:** Ny `MeteringPointMapperTests.cs` med 4 scenarier — 1×electricity, 1×water, 1×heat, 1×EvCharging — alle grønne. Bekræfter at `Meter.Decimals` / `DigitsOfPrecision` / `ReadingOccurrence` sættes korrekt pr. commodity, og at `InstallationAddress` er populeret (ikke null) for alle commodities.

**Du kører** (Repo 2):
```
dotnet test src\Tests\DataMigration.SettlClient.Tests --filter MeteringPoint
```
**Forventet:** Translator-tests reduceret til 1:1-shape — ingen branching-tests, ingen address-fallback-tests.

### Trin 2 — Xellent live-DB regression (golden bit-identity)

**Du kører** (her):
```
dotnet test src\Tests\DataMigration.Xellent.IntegrationTests
```
**Forventet:** Alle 3 golden-profiler (`match`, `ppa-customers`, `vand-sample`) bit-identiske med dagens master. Refaktoren er ren mekanisk omplacering — Settl-output må ikke ændre sig.

### Trin 3 — Spot-check mod featuretest

**Du kører:**
```
dotnet run --project src\DataMigration.Migration -- create `
  --profile <vand-profil> --account <vand-kunde> --env featuretest --dry-run
```
**Forventet:** MeteringPointDto for vand-MP har `Decimals=1`, `DigitsOfPrecision=1`, `MeterReadingOccurrence="Hourly"`, og `InstallationAddress` matcher kundens primary-adresse. Bit-identisk med master.

```
dotnet run --project src\DataMigration.Migration -- create `
  --profile <el-profil> --account <el-kunde> --env featuretest --dry-run
```
**Forventet:** El-MP har `Decimals=0`, `DigitsOfPrecision=0`, `MeterReadingOccurrence="Hourly"` eller `"QuarterHourly"` afhængigt af målerens ReadingInterval. Bit-identisk.

## Kontekst

`MeteringPointTranslator.Build` i SettlClient har to leaks der hører hjemme i domænet:

1. **Address-fallback** (`mp.InstallationAddress ?? customer.Address`, linje 68): for water/heat-MPs uden delpoint falder den tilbage til kundens postal-adresse. **Faktisk** populerer `MeteringPointMapper.MapInstallationAsync` allerede `InstallationAddress` fra kundens primary-adresse (via `DirPartyAddressRelationship.Isprimary=1`-join), så fallback'en er dead code i praksis — men den dokumenterer en domæne-fact der bør ligge ét sted.

2. **Commodity-defaults** (linje 126-138): `isElectricity = mp.Grid != null` styrer `Decimals=0 vs 1`, `DigitsOfPrecision=0 vs 1`, `MeterReadingOccurrence=fra ReadingInterval vs hardcoded "Hourly"`. Det er fagdomæne-viden om hvad et el-meter vs et vand-meter målefyldigt er. `Domain.Meter` har i dag ingen sådanne felter — translatoren udleder dem fra `Grid != null`.

Bonus: linje 23-26 har en ignoreret `start`-parameter "bevaret for binær-kompat" — død kode der kan ryddes når signaturen alligevel ændres.

Multi-repo, samme pattern som payer-refaktoren. Krav: bit-identisk Settl-output før/efter. Golden integration tests er styrende.

## Lærte praktiske noter fra payer-cyklus

- Repo 2 `dotnet pack` skal forudgås af `bin/obj clean + dotnet build -c Release` for at undgå stale DLL i nupkg.
- Alle 6 XellentDomain.* pakker bumpes samtidigt via central `Directory.Build.props` (Core, Domain, Domain.Engros, Domain.TimeSeries, Domain.Xellent, XellentData).
- `nuget.config` her har allerede `packageSourceMapping` for local-temp; ingen skygning denne gang.

## Critical files

### Repo 1 — `NewSettlement.XellentDomain` (lokal: `NewSettlement.UtilityData`)

**Ændret:** `src/DataMigration.Domain/MeteringPoints/Meter.cs`
- Tilføj `int Decimals` og `int DigitsOfPrecision` (init-only, default 0).
- Tilføj `MeterReadingOccurrence ReadingOccurrence` (init-only, default `Hourly`).
- Behold eksisterende `ReadingInterval` (TimeSpan?) — er stadig kilde for ReadingOccurrence-derivation men ikke længere translator-konsumeret.

**Ny:** `src/DataMigration.Domain/MeteringPoints/MeterReadingOccurrence.cs`
```csharp
public enum MeterReadingOccurrence
{
    Hourly,
    QuarterHourly
}
```

**Ændret:** `src/DataMigration.Domain.Xellent/Internal/MeteringPointMapper.cs`
- I `MapElectricityAsync` (linje 205-207): byg `Meter` med:
  - `Decimals = 0`
  - `DigitsOfPrecision = 0`
  - `ReadingOccurrence = ReadingInterval?.TotalMinutes == 15 ? MeterReadingOccurrence.QuarterHourly : MeterReadingOccurrence.Hourly` (default `Hourly` hvis ReadingInterval==null)
- I `MapInstallationAsync` (linje 312-340): byg `Meter` med:
  - `Decimals = 1`
  - `DigitsOfPrecision = 1`
  - `ReadingOccurrence = MeterReadingOccurrence.Hourly`
- `InstallationAddress` skal være ikke-null efter mapping for alle commodities — bekræft at både electricity (delpoint-flow) og installation-flow sætter den.

**Test:** Ny `src/Tests/DataMigration.Domain.Xellent.Tests/Internal/MeteringPointMapperTests.cs` med scenarier:
- Electricity: 15min ReadingInterval → `QuarterHourly`, Decimals=0
- Electricity: 1h ReadingInterval → `Hourly`, Decimals=0
- Electricity: null ReadingInterval → `Hourly`, Decimals=0
- Water/Heat: alle defaults til Hourly + Decimals=1

**Pack:** Bump til `0.7.3-preview` via central `Directory.Build.props`. Pak alle 6 pakker (`dotnet pack` solution-niveau), kopier til `c:\tmp\local-nupkgs\`.

### Repo 2 — `NewSettlement.SettlClient`

**Ændret:** `src/DataMigration.SettlClient/Internal/MeteringPointTranslator.cs`
- Slet `start`-parameter (linje 23) — død kode. Ny signatur: `Build(MeteringPoint mp, Customer customer)`.
- `BuildMeterCharacteristics` (linje 120-146):
  - Slet `isElectricity`-branching for `Decimals`, `DigitsOfPrecision`, `MeterReadingOccurrence`.
  - Læs direkte fra `mp.Meter`: `Decimals = mp.Meter.Decimals`, `DigitsOfPrecision = mp.Meter.DigitsOfPrecision`, `MeterReadingOccurrence = mp.Meter.ReadingOccurrence switch { … }` (enum→string-mapping bevares her — det er Settl-DTO-shape).
- `BuildInstallationAddress` (linje 63-74): slet `mp.InstallationAddress ?? customer.Address`-fallback. Brug `mp.InstallationAddress` direkte. Hvis nogen kalder med null → NRE i stedet for silent fallback. Defensiv null-check kan tilføjes som ArgumentException hvis ønsket.
- `Customer customer` parameter beholdes (bruges stadig til `BuildConsumer` for navn/CVR/CPR-mapping — det er en legitim cross-cutting reference).
- Grid/SettlementMethod-branching omkring `isElectricity = mp.Grid != null` (linje 28, 44-47) BEVARES — det er Settl-DTO-shape (sektioner emiteres for el, ikke for vand/varme), ikke domæne-logik.

**Ændret:** Tilsvarende kaldere af `MeteringPointTranslator.Build`:
- `SettlClient.cs` — fjern `start`-argument fra alle kald.

**Tests:** `src/Tests/DataMigration.SettlClient.Tests/MeteringPointTranslatorTests.cs` — slank til 1:1 round-trip. Bevar en test pr. commodity-shape (el med Grid, vand uden Grid) for at verificere sektion-emission.

**Pack:** Bump til `0.6.0-preview` (breaking på `Build`-signatur). Husk clean + Release-build inden pack.

### Repo 3 — `NewSettlement.DataMigration` (her)

**Ingen kode-ændringer forventet** — `MeteringPointTranslator` kaldes kun fra SettlClient internt. Vi bumper:

**`Directory.Packages.props` / individuelle csprojs:**
- `XellentDomain.*` 0.7.2 → 0.7.3-preview (Migration.Library, OpsCli.Library, Xellent.IntegrationTests, OpsCli.IntegrationTests)
- `SettlClient` 0.5.0 → 0.6.0-preview (Migration.Library, OpsCli.Library)

**`src/Tests/DataMigration.Xellent.IntegrationTests/`** — kør lokalt; goldens skal være bit-identiske. Promovér IKKE goldens uden eksplicit aftale.

## Pack/restore-workflow

Per memory `feedback_local_pack_workflow` + lærte noter:

1. **Repo 1** (UtilityData): `dotnet build -c Release` + `dotnet pack -c Release` på solution-niveau → alle 6 pakker @ 0.7.3-preview. Kopier til `c:\tmp\local-nupkgs\`.
2. **Repo 2** (SettlClient): Tilføj/bekræft local-temp source. `Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force` → `dotnet build -c Release` → `dotnet pack -c Release`. Verificer at den nye DLL er i nupkg'en (`Expand-Archive` + tjek `lib/net9.0/*.dll` LastWriteTime).
3. **Repo 3** (her): Ryd cache (`dotnet nuget locals all --clear` + `Remove-Item ~/.nuget/packages/newsettlement.settlclient` + tilsvarende for XellentDomain.*), bump csprojs, `dotnet restore --force` → build → tests.

## Risici

- **Bit-identitet:** Mapping af `MeterReadingOccurrence` enum → string i translatoren skal være præcis: `Hourly` → "Hourly", `QuarterHourly` → "QuarterHourly". Default-case skal være "Hourly" for at matche eksisterende `MapReadingInterval`-adfærd.
- **InstallationAddress null-defense:** Hvis vi fjerner `?? customer.Address`-fallback'en og en MP slipper igennem mapperen med null, får vi NRE i prod. Tilføj evt. ArgumentNullException-guard i translator + assertion i mapperen (eller behold fallback som dead defensive — det er 1 linje).
- **Meter null:** `BuildMeterCharacteristics` returnerer null hvis `mp.Meter == null` (linje 122). Den path bevares uændret — kun de tre felter på den indre Meter-objekt ændres.
- **Stable ordering:** Meters er en liste i Settl-DTO men kun ét `Meter`-objekt i Domain. Ingen ordering-bekymring.

## Out of scope

- Yderligere translator-smells (CustomerTranslator er ren — ingen).
- Conversion af `Domain.Meter` til en list (`Meters`) — i dag er det singular og det matcher Xellent (én meter pr. MP).
- Refaktor af Grid/SettlementMethod-sektioner (de er Settl-shape, ikke domæne).
