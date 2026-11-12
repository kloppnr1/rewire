> **✅ IMPLEMENTERET** — alle fem ændringer verificeret i koden 2026-08-13. Felterne er siden omdøbt af `11f138f7` (Ejerkontrakt (COS-1) → `MigratedContractNumber`).

# MobilePay-match: kun målepunkter i levering, og vis ejerkontrakten

**Status 2026-07-31: alle kodeændringer er implementeret og pushet.** Ændring 0 = XellentDomain
`19dceb9`; 1, 1b, 2, 3 = DataMigration `dddf2df6` + `7188eb67`; 4 = MigrationDashboard `c1ce09a`.
Udestående: før/efter-kørslen mod featuretest under Verifikation — den køres af brugeren.

## Verifikation (start her)

### Hvad der beviser at det virker

Byg og kør enhedstests:

```
dotnet build src/DataMigration.OpsCli/DataMigration.OpsCli.csproj
dotnet test src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj
```

Kør matchen mod featuretest FØR ændringen og gem outputtet som reference:

```
cd src/DataMigration.OpsCli
dotnet run -- payment-match --type mb --all-accounts <sti-til-settl-payment-agreement.csv> --env featuretest -o ./output/mb-foer
```

Kør igen EFTER ændringen:

```
cd src/DataMigration.OpsCli
dotnet run -- payment-match --type mb --all-accounts <sti-til-settl-payment-agreement.csv> --env featuretest -o ./output/mb-efter
```

### Forventet output / succeskriterium

- `dotnet build` → 0 errors.
- `dotnet test` → alle tests passerer. Nye tests i `MobilePayRowBuilderTests` for kolonnerne Ejerkontrakt/Slutdato/COS.
- Konsollinjen `Scannet: N Settl-målepunkter → M konti → K aftaler` har **lavere N og M** efter ændringen end før. N falder med antallet af ikke-i-levering-målepunkter; M falder kun for konti hvis eneste målepunkter var ude af levering.
- Bemærkningen `Konto udledt, ikke matchet — målepunktet har aldrig været i levering` optræder **0 gange** i optællingen efter ændringen (før: et positivt tal).
- Excel-arket har tre nye kolonner efter "Målepunkt (match-nøgle)": **Ejerkontrakt (COS-1)**, **Kontraktslutdato**, **COS**. For hver række gælder `Kontraktslutdato = COS − 1 dag`.
- Verificér ved at åbne begge ark og bekræfte: hver række der findes i `mb-foer` men ikke i `mb-efter`, hører til en konto hvis målepunkter alle er ude af levering i Settl.

### Dashboardet (efter CI har publiceret OpsCli-pakken)

```
dotnet restore C:\Users\sen.makj\source\repos\MigrationDashboard --force
dotnet test C:\Users\sen.makj\source\repos\MigrationDashboard\src\Dashboard.Api.Tests\Dashboard.Api.Tests.csproj
cd C:\Users\sen.makj\source\repos\MigrationDashboard\src\dashboard
npm test
```

Forventet: begge suiter grønne. Start derefter dashboardet og åbn MobilePay-fanen — de tre nye kolonner skal være udfyldt på hver række, ikke tomme.

### Spot-check

Vælg 2 rækker fra `mb-efter` og bekræft i Xellent at `EXU_CONTRACTTABLE.CONTRACTENDDATE` for den viste ejerkontrakt er præcis COS − 1, og at kontraktens `CUSTACCOUNT` er kolonne 1.

## Kontekst

Matchen lader i dag ikke-i-levering-målepunkter være med til at bestemme populationen. De kan ikke COS-1-matches, så kontoen udledes ved gætværk — og udtrækket viser ikke hvilken kontrakt der pegede på kontoen, endsige om matchet var et gæt.

## Ændringer

Rækkefølgen er bundet af NuGet-flowet: ændring 0 i `XellentDomain` → afvent CI-preview → ændring 1–3 her → afvent CI-preview → ændring 4 i dashboardet.

### 0. Slå `ContractNumber` og `SourceContractNumber` sammen (repo `XellentDomain`)

- `MobilePayContract` sætter begge felter til samme værdi (`a.SourceContract`), så recorden ser ud til at bære to begreber hvor der er ét. `MobilePayRowBuilder` bruger `ContractNumber` i joint-invoice-fallbacken og `SourceContractNumber` i rapportrækken — samme streng, to navne.
- Behold `SourceContractNumber` (navnet svarer til spørgsmålet "hvor blev aftalen fundet", og det er navnet dashboardets wire-række allerede bruger). Fjern `ContractNumber` fra recorden og fra konstruktionen i `FetchAsync`.
- Brud på pakkens offentlige API → `VersionPrefix` bumpes i `Directory.Build.props`.
- Konsument-følge her i repoet: `MobilePayRowBuilder.cs:62-66` (joint-nøglen) og `:86` (`Hoofdcontrakt`-fallback) skal læse `SourceContractNumber`. Grep alle kaldssteder før commit — build alene fanger ikke stale NuGet-cache.

### 1. Luk ikke-i-levering-målepunkter ude

- `MobilePaySettlMatchService`: `includeNotInDelivery: false` ved kaldet til `SettlMeteringPointCosLoader.LoadAsync`.
- Samme sted: fjern `noHistory`-sættet og send `null` som `noHistoryMpIds` til `MeteringPointAccountResolver.ResolveAsync` — der er ingen ikke-i-levering-målepunkter tilbage at falde tilbage på.
- Fallbacken i `MeteringPointAccountResolver` selv røres ikke — `reconcile` bruger den.
- Dækningsgrænsen i konsol-outputtet udvides: udover "kun el-målepunkter" skal der stå at kun målepunkter i levering indgår.

### 1b. Fjern det der bliver uopnåeligt

- `InferredAccountRemark` + `accountInferred`-parameteren i `MobilePayRowClassifier.BuildRemarks`, og `inferredMpIds`-parameteren i `MobilePayRowBuilder.Build`: uden fallback kan de aldrig sættes. Fjernes sammen med deres enhedstests.
- `MobilePayMatchStatus.NotMigrated`: allerede uopnåelig siden `e3d4cfda`, og bliver det endnu tydeligere her — `settlMpsByAccount` indeholder pr. konstruktion kun konti Settl opløste. Fjernes fra enum'et, fra `StatusLabel`-switchen i runneren og fra TS-unionen i dashboardet.
- Begge er brud på det offentlige API i `DataMigration.OpsCli`-pakken → `VersionPrefix` bumpes, og dashboardet skal følge (se ændring 4).

### 2. Ejerkontrakten skal med ud af resolveren

- `MpAccountResolution` udvides additivt med `OwnerContractByMpId`: kontraktnummer + `Contractenddate` pr. målepunkt, fyldt fra samme række som `Pairs`/`BillingPlanByMpId` — altså aldrig fra en anden kontrakt end den der udpegede kontoen.
- Rent additivt: `reconcile` og `export-settlement-config` er upåvirkede.

### 3. Vis matchet i arket

- `MobilePayMatchReportRow` får `OwnerContractNumber`, `OwnerContractEndDate`, `Cos`.
- `MobilePayRowBuilder.Build` får COS pr. målepunkt og ejerkontrakt-opslaget ind, og udfylder felterne for det målepunkt der gav E-nummer-matchet (kolonne "Målepunkt (match-nøgle)"). Er der intet match, bruges kontoens første målepunkt.
- `MobilePayMatchRunner`: tre nye kolonner efter kolonne 9, resten rykker. Kolonnetæller (14) opdateres alle steder.
- Afviger `Kontraktslutdato` fra `COS − 1`: bemærkning på rækken og gul markering. Bør ikke kunne ske efter ændring 1 — det er en assertion i arket, ikke en forventet tilstand.

### 4. Dashboardet (repo `MigrationDashboard`)

Kører i et separat repo og kan først laves når OpsCli-pakken er publiceret af CI. Rækkefølge: DataMigration `develop` → afvent CI-preview → `dotnet restore --force` i dashboardet → ændringerne nedenfor.

- `Dashboard.Api/Services/MobilePayWireMapper.cs`: `MobilePayMatchWireRow` er en positionel record — tre nye felter (`OwnerContractNumber`, `OwnerContractEndDate`, `Cos`) og tilsvarende linjer i `ToWire`. Fjern `InferredAccountRemark`-afledte antagelser hvis nogen findes.
- `Dashboard.Api.Tests/MobilePayWireMapperTests.cs`: opdatér konstruktionen af testrækken.
- `dashboard/src/data/api.ts`: tre nye felter på MobilePay-rækketypen; `MobilePayMatchStatus`-unionen mister `'NotMigrated'`.
- `dashboard/src/pages/FilesView.tsx`: tre kolonner i tabellen (efter "Målepunkt") og i CSV-eksporten — både header-arrayet og rækkefunktionen. `STATUS_LABEL`/`STATUS_STYLE` mister `NotMigrated`-nøglen.
- `dashboard/src/test/msw/handlers.ts` + `__tests__/api.test.ts` + `__tests__/pages/FilesView.test.tsx`: fixtures opdateres med de nye felter.
- Verifikation: `npm test` i `src/dashboard`, `dotnet test` for `Dashboard.Api.Tests`, og dashboardet startet lokalt med MobilePay-fanen åbnet — kolonnerne skal være udfyldt, ikke tomme.

## Allerede gjort

- Dedup-grupperingen på `(Account, AgreementId)` i `XellentMobilePayContractReader` er fjernet (UtilityData `develop`, commit `39e1bb6`, ikke pushet). Verificeret over AXDB50 2026-07-31: 0 konti har samme aktive aftale-id på flere kontraktnumre. Ren intern ændring — ingen `VersionPrefix`-bump, ingen konsument-koordinering.

## Ikke i denne ændring

- Fallbacken i `MeteringPointAccountResolver.cs:152` bevares uændret — `reconcile` er afhængig af den. Kun MobilePay-stien holder op med at kalde den.
