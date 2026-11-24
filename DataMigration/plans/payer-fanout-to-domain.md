> **✅ IMPLEMENTERET** — `43382f6b`
>

# Flyt payer-fan-out fra SettlClient til Domain

## Verifikation (start her)

### Trin 1 — Unit tests i hver ny owner
**Du kører** (i XellentDomain repo):
```
dotnet test src\Tests\DataMigration.Domain.Xellent.Tests --filter PayerMapper
```
**Forventet:** Alle 3 scenarier fra `PayerTranslatorTests.cs` (no contract EAN, distinct EANs, null customer-EAN) lever videre som `PayerMapperTests` og er grønne.

**Du kører** (i SettlClient repo):
```
dotnet test src\Tests\DataMigration.SettlClient.Tests --filter PayerTranslator
```
**Forventet:** Tests reduceret til 1:1-shape — `Build(Domain.Payer, customerId)` → ét `CreatePayerDto` med felter mappet direkte. Ingen gruppering-tests her længere.

### Trin 2 — Xellent live-DB regression (golden bit-identity)
**Du kører** (i dette repo):
```
dotnet test src\Tests\DataMigration.Xellent.IntegrationTests
```
**Forventet:** Alle golden-profiler (`match`, `ppa-customers`, `vand-sample`) bit-identiske med dagens master. Refaktoren er ren mekanisk omplacering — output må ikke ændre sig.

### Trin 3 — Spot-check multi-payer-kunder mod featuretest
**Du kører:**
```
dotnet run --project src\DataMigration.Migration -- create `
  --profile <profil med 352639 Gribskov> --account 352639 --env featuretest --dry-run
```
**Forventet:** Dry-run-output viser **9 payers** for kunde 352639 (8 unikke contract-EAN + default). Identifikatorer: `352639` (primary) + `352639-{EAN}` × 8. Match dagens output linje-for-linje.

**Du kører** samme for 427549 (Køreteknikken-merge):
**Forventet:** **2 payers** for repræsentant 427549 (default + 1 extra fra merged-in 427789's afvigende EAN).

### Trin 4 — `report` runner konsistens
**Du kører:**
```
dotnet run --project src\DataMigration.OpsCli -- report --profile <samme profil> --output ./report-after.csv
```
**Forventet:** `PayersExtracted` matcher `create`-output (dagens setup har en kendt inkonsistens — `CreateRunner` bruger `Build`-singular og `ReportRunner` bruger `BuildAll`-multi; refaktoren fjerner det implicit ved at begge slår op i `customer.Payers`).

## Kontekst

`PayerTranslator.BuildAll` i SettlClient indeholder business-logik der hører hjemme i domænet: *"hvor mange payers skal denne kunde have, og hvilke kontrakter hører til hvilken payer"*. Translatorens rolle bør være rent strukturel — tag domæne-entitet, producér Settl-DTO 1:1.

Bivirkning i dag: `CreateRunner` populerer `report.Payers` via `PayerTranslator.Build` (single), mens `ReportRunner` (OpsCli) bruger `ExtractPayersAsync` → `PayerTranslator.BuildAll` (multi). Dvs. samme `MigrationReport.Payers` betyder forskellige ting afhængigt af kald-vej. Refaktor rydder op begge steder samtidig: `customer.Payers` bliver eneste sandhed.

Multi-repo-refaktor (3 repos). Krav: bit-identisk Settl-output før/efter, så ingen kunder ændrer Settl-state ved kørsel.

## Ændringer

### Repo 1 — `XellentDomain` (lokal: `UtilityData`) ✅ **Færdig**

Status: paket lokalt @ `0.7.2-preview` (output i `src/local-temp/`).

**Public API (sådan ser den faktisk ud):**
- `DataMigration.Domain.Customers.Payer` — record `(string Identifier, bool IsPrimary, string? ElectronicNumber, IReadOnlyList<string> ContractNumbers)`.
- `DataMigration.Domain.Customers.Customer.Payers` — `IReadOnlyList<Payer>` **init-only property** med default `Array.Empty<Payer>()`. Populeres via `with`-expression i orchestratoren — eksisterende `new Customer(...)`-positional-kald bryder ikke.
- `DataMigration.Domain.Xellent.Internal.PayerMapper.Build(Customer, IReadOnlyList<Contract>)` — public static, returnerer `IReadOnlyList<Payer>`.
- Orchestrering: `XellentDomainMapper.cs:73-82` (IKKE `CustomerMapper` — Customer bygges før Contracts kendes; orchestratoren har begge).

**Afvigelser fra original plan (bevidste):**
- EAN trim'es nu med `Trim()` før gruppering. Original `PayerTranslator.BuildAll` trim'er IKKE. **Bit-identitets-risiko i Repo 3** hvis nogle DB-EANs har whitespace. Skulle ikke ske, men hold øje når goldens kører.

**Test:** `src/Tests/.../Internal/PayerMapperTests.cs` (ny) — 4 tests, alle grønne (de 3 fra `PayerTranslatorTests.cs` + ekstra `PayerReference`-test).

**Pack-output:**
- `XellentDomain.Domain.0.7.2-preview.nupkg`
- `XellentDomain.Domain.Xellent.0.7.2-preview.nupkg`

(Bemærk: pakke-navne afviger fra forventet `XellentDomain.Core.*` — det er repoets faktiske pack-output.)

### Repo 2 — `SettlClient`

**Ændret:** `src/DataMigration.SettlClient/Internal/PayerTranslator.cs`
- Slet `BuildAll`.
- Erstat `Build(Customer, Guid)` med `Build(Customer customer, Payer payer, Guid customerId)` — 1:1 mapping, ingen branching:
  - `PayerIdentifier = payer.Identifier`
  - `ElectronicNumber = payer.ElectronicNumber`
  - `IsPrimaryCustomerPayer = payer.IsPrimary`
  - Resten (Address, Contact, InvoiceChannel, PaymentChannel, Cvr) tages fra `customer` — uændret.
- `BuildCore` privat helper fjernes (kollapses ind i `Build`).
- **Ingen** EAN-gruppering, **ingen** `customer.Contracts`-iteration i translatoren.

**Ændret:** `src/DataMigration.SettlClient/SettlClient.cs` step 5 (MP→payer-routing, linje 337-423)
- Slet `contractEanByMpId`-konstruktion + divergent-EAN-warning (den flytter til `PayerMapper` i Domain).
- Erstat med direkte slag i `customer.Payers` via en pre-built `Dictionary<string, string> payerIdentifierByContract` (ContractNumber → Payer.Identifier) — bygges éngang før loopet, O(1) lookup pr. MP via dens kontrakt.
- For MPs på kontrakter der ikke findes i map (kan kun ske hvis Domain er korrupt) → default payer, log warning. (Skal aldrig ske hvis PayerMapper kører konsistent.)

**Tests:** `PayerTranslatorTests.cs` — fjern multi-payer-scenarier (de hører nu i PayerMapperTests i Domain repo). Behold ét simpelt 1:1 round-trip-test.

**Pack:** `dotnet pack` → `SettlClient.0.5.0-preview.nupkg` (breaking på offentlig `BuildAll`-signatur, kræver minor-bump per preview-konvention).

### Repo 3 — `DataMigration` (dette repo)

**`nuget.config` / `Directory.Packages.props`:**
- Bump `XellentDomain.*` → ny preview-version.
- Bump `SettlClient` → ny preview-version.

**`src/DataMigration.Migration.Library/Services/SettlMappingService.Payers.cs`** (linje 13-27):
```csharp
foreach (var customer in customers)
{
    var customerId = customerIdByAccountNum.GetValueOrDefault(customer.AccountNumber);
    customerIdLookup[customer.AccountNumber] = customerId;
    payers.AddRange(customer.Payers.Select(p => PayerTranslator.Build(customer, p, customerId)));
}
```
Adfærd uændret — `customer.Payers` indeholder samme antal payers som `BuildAll` producerede.

**`src/DataMigration.Migration.Library/Runners/CreateRunner.cs`** (linje 298-301):
```csharp
report.Payers = loadResult.Customers
    .SelectMany(c => c.Payers.Select(p => (Customer: c, Payer: p)))
    .Zip(customerIdByAccount.Values, ...) // eller alternativ keying
    .Select(t => PayerTranslator.Build(t.Customer, t.Payer, ...))
    .ToList();
```
Detalje udløses ved skrivning — det vigtige: `report.Payers` indeholder nu samme antal som `ReportRunner` (fixer den eksisterende inkonsistens som bonus).

**`src/Tests/DataMigration.Xellent.IntegrationTests/`** — kør lokalt; goldens skal være bit-identiske. Hvis noget afviger → fejl i PayerMapper-portering. Promovér IKKE goldens uden eksplicit aftale (jf. memory `feedback_golden_promotion`).

## Pack/restore-workflow

Per memory `feedback_local_pack_workflow`: pack lokalt, brug temp-source — vent ikke på ADO feed.

1. `dotnet pack` i UtilityData → producerer `.nupkg`'er i et lokalt output-dir.
2. `dotnet restore --source <temp-feed> --source nuget.org` i SettlClient — får den friske XellentDomain.
3. `dotnet pack` i SettlClient → ny `.nupkg`.
4. I DataMigration: tilføj samme temp-feed midlertidigt til `nuget.config`, `dotnet restore`, build, kør integration tests.
5. Når alt passer: push branches → byg via ADO → vent på officielle preview-pakker → fjern temp-feed → bump til officielle versioner i én rydende commit.

## Risici

- **Bit-identitet:** `PayerTranslator.BuildCore` har et antal `?? string.Empty`-konstruktioner (linje 110-111, 121, 129) der er bevidst for Settl byte-compat. Disse skal blive **præcis** i `PayerTranslator.Build` efter refaktor — let at glemme.
- **Stable ordering:** `BuildAll` returnerer default først, derefter extras i den rækkefølge `Distinct().ToList()` giver (ordinal). Goldens er gemt med den rækkefølge. `PayerMapper` skal producere samme rækkefølge ind i `Customer.Payers`.
- **`ContractNumbers`-tildeling:** Hvis en MP hænger på kontrakt med tom EAN (eller divergent EAN på tværs af kontrakter for samme MP), routes den i dag til default-payer. Den semantik skal bevares — `PayerMapper` skal tildele kontrakter med tom EAN til default-payer; divergent-EAN-warning flytter med.
- **Inkonsistens i `report.Payers` count fixes utilsigtet:** Dashboard / consumers af `MigrationReport.PayersExtracted` kan se en ændring (count går fra single → multi i dry-run via CreateRunner). Det er en *ønsket* sideeffect men brugeren skal vide det.

## Out of scope

- Konsolidering af eksisterende duplikat-payers i Settl fra perioden hvor `Build` (single) blev brugt — kræver separat oprydningsplan.
- Andre translator-smells (`MeteringPointTranslator`, `CustomerTranslator`) — én ad gangen.
