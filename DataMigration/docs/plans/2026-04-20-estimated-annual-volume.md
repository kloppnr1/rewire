# Plan: Sæt forventet forbrug (EstimatedAnnualVolume) i Settl

## Scope
- **Kun el-ekstern** (delpoint-baseret). Vand/varme/kloak er ude — de har ingen `ExuReadingTableExt`-rækker (tabellen er keyed på `Delpointnum`).
- **Kun fix-runner** i denne iteration. `create`-flow-integration udsættes til en senere plan.

## Context

Forventet forbrug sættes aldrig i Settl i dag. `MeteringPointDto` har intet EAV-felt, og `ISettlMeteringPointClient` har ingen metode mod `/annualConsumptions`. CSV-kode i søsterrepos (CustomerSupport/Tools/UtilityOperations) populerer feltet fra `ExuReadingTableExt.Expectedconsumptionperyear`, men outputtet bruges ingen steder — dead code, rører vi ikke.

Settl-endpoint: **`PUT /api/v3/meteringPoints/{id}/annualConsumptions`** (market_internal_api.json:17875).
Body-schema: `UpdateAnnualConsumptionsRequest` = `{ TransactionId, AnnualConsumptions: QuantityPeriodItem }` (schema:35096); `QuantityPeriodItem` = `{ TimePeriod, Quantity }` (schema:39701).

Mål: ny `fix-annual-consumption` OpsCli-kommando der sætter EAV på allerede-migrerede el-ekstern-MP'er.

## Design

### Del 1 — Settl API client-udvidelse

Filer:
- `src/DataMigration.SettlApi/Clients/ISettlMeteringPointClient.cs`
- `src/DataMigration.SettlApi/Clients/SettlMeteringPointClient.cs`
- `src/DataMigration.SettlApi/Models/` (nye DTO'er hvor nødvendigt)

Tilføj:
```csharp
Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(
    Guid meteringPointId, decimal annualVolumeKwh, DateTime effectiveFrom,
    CancellationToken cancellationToken = default);
```

Implementation spejler `UpdateBillingAsync` (SettlMeteringPointClient.cs:106-111):
- URL: `{GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{id}/annualConsumptions`
- Metode: `PutAsync(url, request, JsonOptions, ct)` (PascalCase — markedet-API'et bruger `JsonOptions`, ikke camelCase)

DTO'er — verificér eksisterende før nye oprettes:
- `TimePeriodDto` **findes allerede** (MeteringPointModels.cs:139-143, `DateTimeOffset Start/End`). Bekræft at Settl-schemaets `TimePeriod` matcher (Start/End som DateTimeOffset) inden genbrug; hvis schema kun har `Start`, så opret ny.
- `Quantity`, `QuantityPeriodItem`, `UpdateAnnualConsumptionsRequest` — nye.

**Åben:** Bekræft om der findes GET-modpart. Mulige shapes: (a) selvstændigt endpoint `/annualConsumptions`, (b) inkluderet i `GET /api/v3/meteringPoints/{id}` response (`MeteringPointCurrentDto`). Undersøg i `market_internal_api.json` før idempotens-check designes. Hvis (b): udvid `MeteringPointCurrentDto` med et felt; hvis (a): tilføj `GetAnnualConsumptionsAsync(Guid)`.

### Del 2 — Ny Xellent-entitet

Fil: `src/DataMigration.XellentData/Entities/ExuReadingTableExt.cs` (ny)

Kopiér definition fra CustomerSupport-repoet. Felter vi har brug for: `Delpointnum`, `Attachmentnum` (= Agreementnum), `Dataareaid`, `Companyid`, `Deliverycategory`, `Readingdate`, `Recid`, `Expectedconsumptionperyear`.

Registrér `DbSet<ExuReadingTableExt>` + `OnModelCreating`-config på `XellentDbContext`.

### Del 3 — Ny OpsCli fix-kommando: `fix-annual-consumption`

Filer:
- `src/DataMigration.OpsCli/Runners/FixAnnualConsumptionRunner.cs` (ny, spejler FixElectronicNumberRunner)
- `src/DataMigration.OpsCli/Program.cs` (registrér kommandoen)

**Options** (shape som FixElectronicNumberRunOptions):
`ProfileName, Accounts[], DryRun, Scan, DataAreaId, ReferenceDate`.

**Delivery category lock:** runner er hardcoded `deliveryCategory = "El-ekstern"` — `ISettlMeteringPointClient.SetDeliveryCategory("El-ekstern")` kaldes i starten. I `--scan` mode springes MPs over hvis deres tilknyttede Xellent-delpoint ikke er el-ekstern (join kontrol, ellers støj-warnings på vandmålere).

**Flow per Accountnum:**
1. `AccountRepresentativeResolver.ResolveAsync` — repræsentations-princip (FixElectronicNumberRunner.cs:75).
2. `SearchByCustomerNumberAsync` → `GetMeteringPointsAsync` (same pattern linje 85-101).
3. For hver MP: translate Settl-MP Guid → Xellent `Meteringpoint`-ID via søgning / GetById; slå så op i `ExuReadingTableExt` for seneste værdi:
   ```
   .Where(r => r.Delpointnum tilhørende denne Meteringpoint && r.Dataareaid == "hol" && r.Deliverycategory == "El-ekstern")
   .OrderByDescending(r => r.Readingdate).ThenByDescending(r => r.Recid)
   .FirstOrDefaultAsync()
   ```
   (Tiebreaker `Recid` DESC — samme mønster som ExuMeterJournalTrans-joinet SettlExtractionService.MeteringPoints.cs:459.)
4. Hent current EAV i Settl (Del 1 GET). Sammenlign:
   - `Expectedconsumptionperyear <= 0` / mangler: `Skipped++`, warning.
   - Settl-værdi = Xellent-værdi: `AlreadyCorrect++`, skip.
   - `DryRun`: log `[DRY] MP X: would set EAV = Y kWh`, `WouldUpdate++`.
   - Ellers: `UpdateAnnualConsumptionsAsync` → `Updated++` / `Failed++`.

**`--scan` mode:** paginér `FindBillingMeteringPointsAsync` som FixElectronicNumberRunner.cs:117-160. Til hver MP: lookup i Xellent for at verificere `Deliverycategory == "El-ekstern"` før behandling (eller skip).

**`effectiveFrom`-dato:** brug `Readingdate` fra den valgte ExuReadingTableExt-række. Alternativt den faktiske agreement startdate — afklar i implementering hvad Settl kræver (afvises fremtidige/for gamle datoer?). Noter valget i runner-docstring.

**Report:** `FixAnnualConsumptionReport { CustomersChecked, MpsChecked, AlreadyCorrect, Updated, Skipped, Failed, Errors[] }`.

**Produktion:** kræver eksplicit `--env production` + eskalering jf. verification-workflow. Dry-run default mod featuretest.

### Kritiske filer

| Fil | Ændring |
|-----|---------|
| `DataMigration.SettlApi/Clients/ISettlMeteringPointClient.cs` | `UpdateAnnualConsumptionsAsync` (+ evt. `GetAnnualConsumptionsAsync`) |
| `DataMigration.SettlApi/Clients/SettlMeteringPointClient.cs` | Implementation |
| `DataMigration.SettlApi/Models/MeteringPointModels.cs` eller ny fil | `Quantity`, `QuantityPeriodItem`, `UpdateAnnualConsumptionsRequest` |
| `DataMigration.XellentData/Entities/ExuReadingTableExt.cs` | Ny |
| `DataMigration.XellentData/XellentDbContext.cs` | `DbSet` + config |
| `DataMigration.OpsCli/Runners/FixAnnualConsumptionRunner.cs` | Ny (el-ekstern only) |
| `DataMigration.OpsCli/Program.cs` | Registrér `fix-annual-consumption` |
| `CHANGELOG.md` | Ny række (adfærdsændring i Runners/) |

## Verifikation

### 1. Build + unit tests
```
dotnet build
dotnet test src/Tests/DataMigration.SettlApi.Tests
dotnet test src/Tests/DataMigration.OpsCli.Tests
```

### 2. OpsCli integration tests (live-run)
`src/Tests/DataMigration.OpsCli.IntegrationTests/` — skriv ny test mod AXDB50_TEST + featuretest-profilen der dækker FixAnnualConsumption-runneren. Følger FixTestAccounts cleanup-mønsteret.

### 3. CLI smoke (dry-run featuretest)
```
cd src/DataMigration.OpsCli
dotnet run -- fix-annual-consumption --profile featuretest --accounts <test-account> --dry-run
```
Forvent rapport "would set EAV = X kWh" pr. MP.

### 4. Hot-run featuretest
```
dotnet run -- fix-annual-consumption --profile featuretest --accounts <test-account>
```
Verificér i Settl featuretest UI: MP-detalje → Estimated Annual Volume viser korrekt kWh.

### 5. Xellent spot-check
For 2-3 MP'er, SQL mod AXDB50_TEST:
```sql
SELECT TOP 1 Expectedconsumptionperyear, Readingdate
FROM EXU_READINGTABLEEXT
WHERE Delpointnum=<num> AND Dataareaid='hol' AND Deliverycategory='El-ekstern'
ORDER BY Readingdate DESC, Recid DESC
```
Sammenlign med Settl-værdi.

### 6. Production
**Altid dry-run først** (prod = altid dry-run default per global regel):
```
dotnet run -- fix-annual-consumption --profile production-hev --scan --dry-run
```
Hot-run kræver eksplicit bruger-eskalering.

## Out of scope (denne iteration)
- Create-flow-integration (POST + PUT annualConsumptions ved MP-oprettelse) — ny plan senere.
- Vand/varme/kloak/andre kategorier — andre datakilder end ExuReadingTableExt.
- `EstimatedAnnualVolume2` og historisk `EstimatedAnnualVolumes`-array.
- Sletning af død CSV-kode i CustomerSupport/Tools/UtilityOperations (nævnes i commit-besked som opfølgning).
