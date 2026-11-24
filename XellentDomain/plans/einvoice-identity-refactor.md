# `BuyerInvoicingReferences` — domæne-koncept for fakturareferencer

## Verifikation (start her)

### Hvad der beviser at det virker

Brugeren kører i UtilityData-roden:

```powershell
dotnet build UtilityData.sln
dotnet test src/Tests/DataMigration.Domain.Tests/DataMigration.Domain.Tests.csproj
dotnet test src/Tests/DataMigration.Domain.Engros.Tests/DataMigration.Domain.Engros.Tests.csproj
dotnet test src/Tests/DataMigration.Domain.Xellent.Tests/DataMigration.Domain.Xellent.Tests.csproj
dotnet test src/Tests/DataMigration.Domain.Xellent.IntegrationTests/DataMigration.Domain.Xellent.IntegrationTests.csproj
```

### Forventet output / succeskriterium

- `dotnet build`: **0 Error(s)**
- Hver `dotnet test`: alle tests **passed**, 0 failed
- Især `PayerMapperTests` (filen med flest direkte references til de 4 felter) skal være grøn

### Spot-check af refaktoreret API (manuel inspektion)

Brugeren åbner `Contract.cs`, `Payer.cs`, `MeteringPoint.cs` og verificerer:

- De 4 strings (`ElectronicNumber`, `OrderIdentifier`, `AgreementIdentifier`, `BuyerReferenceIdentifier`) er væk som top-level properties
- Erstattet med én nullable property `BuyerInvoicingReferences? BuyerInvoicing` (samme navn på alle tre entiteter)
- `BuyerInvoicingReferences.cs` har XML-doc der forklarer hvad konceptet er (information modtageren har bedt om at få på fakturaen) og override-hierarkiet (Contract = rå, Payer = arvet fra rep-contract, MP = finkornet override)

### Cross-repo verifikation (brugerens ansvar, separat session)

Efter merge til develop + CI publish af `0.8.0-preview.{BuildId}`:

1. **DataMigration**: `dotnet restore --force` + build + test. Tilpas SettlClient request-building (MeteringPointTranslator, PayerTranslator) der formentlig læser felterne.
2. **TimeSeriesMigration**: build/test — pakken bruges, men berører måske ikke disse felter.
3. **CustomerSupport**: build/test.

Disse repos ligger udenfor denne sessions write-scope. Brugeren opretter dedikerede sessions pr. consumer-repo.

---

## Kontekst

`ElectronicNumber`, `OrderIdentifier`, `AgreementIdentifier`, `BuyerReferenceIdentifier`
står som top-level string-properties på **Contract**, **Payer** og **MeteringPoint**.
En læser ser 12 identisk-navngivne string-felter fordelt over tre typer uden at
vide hvad de udtrykker eller hvordan de relaterer.

De fire felter er ét domæne-koncept: **referencer som fakturamodtageren har bedt
om at få anført på sin faktura** — så modtagerens system kan levere elektronisk
(EAN), matche mod ordre (OrderIdentifier), matche mod aftale (AgreementIdentifier),
og rute internt (BuyerReferenceIdentifier). Konceptet navngives
`BuyerInvoicingReferences`.

Override-hierarkiet (Customer/Payer-niveau som default, Contract og MP som finkornet
override) er naturligt i fakturadomænet — det er ikke en lugt, det er forretning.
Når konceptet får et ordentligt navn og samme property-navn på alle entiteter
("`BuyerInvoicing`"), bliver hierarkiet selvforklarende: hvis sat, gælder det;
hvis null, arves fra parent.

`OioUblConfiguration` (parallel-navngivning `OrderReference`/`BuyerReference`) er
en separat lugt — ude af scope for denne refactor. Noteret i
`docs/domain-model-discussion.md` #4 til opfølgning.

---

## Ændringer

### Ny type

**`BuyerInvoicingReferences`** (ny record i `DataMigration.Domain.Customers`):

```csharp
/// <summary>
/// Referencer som fakturamodtageren (køberen) har bedt om at få anført på fakturaen,
/// så modtagerens system kan håndtere fakturaen korrekt: levere elektronisk, matche
/// mod ordre/aftale, og rute internt.
///
/// <para>Kan stå på flere niveauer (Contract, Payer, MeteringPoint). Den mest
/// specifikke ikke-null værdi vinder. MeteringPoint = null betyder "arv fra Payer".</para>
/// </summary>
public sealed record BuyerInvoicingReferences(
    string? ElectronicNumber = null,        // EAN — elektronisk leverings-adresse
    string? OrderIdentifier = null,         // Reference til indkøbsordre
    string? AgreementIdentifier = null,     // Reference til rammeaftale
    string? BuyerReferenceIdentifier = null // Intern køber-reference (person/afdeling)
);
```

Nullable property på entiteterne. Ingen separat resolver-funktion — semantikken
("ikke-null vinder, ellers arv fra parent") er almindelig fakturadomæne-pattern
og udtrykkes af konsistent navngivning på de tre entiteter.

### Refaktorerede typer

- **`Contract`**: 4 felter fjernes; `BuyerInvoicingReferences? BuyerInvoicing` tilføjes
- **`Payer`**: 4 felter fjernes; `BuyerInvoicingReferences? BuyerInvoicing` tilføjes
- **`MeteringPoint`**: 4 felter fjernes; `BuyerInvoicingReferences? BuyerInvoicing` tilføjes

### Opdaterede mappers og tests (i samme repo)

- `ContractMapper.cs` (5 references): byg `new BuyerInvoicingReferences(...)` i stedet for 4 stringe på constructor
- `PayerMapper.cs` (12 references): samme; kopierer fra rep-contract
- `CustomerMapper.cs` (2 references): samme
- `XellentDomainMapper.cs` `ApplyMpBillingOverrides` (15 references): sammenligning bliver `!Equals(contract.BuyerInvoicing, payer.BuyerInvoicing)` (record-equality på hele blobet); tilskrivning bliver `mp with { BuyerInvoicing = contract.BuyerInvoicing }`
- `XellentLoadResult.cs`: hvis berørt
- `PayerMapperTests.cs` (10 references): konstruktion + asserts opdateres

### Version-bump

- `Directory.Build.props`: `<VersionPrefix>0.7.10</VersionPrefix>` → `<VersionPrefix>0.8.0</VersionPrefix>`
- Breaking change i source-agnostisk model — minor-bump i pre-1.0 (0.7→0.8)

### Diagrammer (denne sessions opfølgning, efter kode er grøn)

- `docs/domain-model.md`: opdater både overview og detaljeret med `BuyerInvoicingReferences`
- `docs/domain-model-discussion.md`: marker #4 som løst (samme koncept har samme navn alle steder; override-hierarki er domæne-naturligt)

### Eksplicit out-of-scope

- `OioUblConfiguration`-navngivning (overlap med disse 4 felter under andre navne): separat refactor
- Resterende model-problemer fra discussion-filen (#1, #2, #3, #5, #6, #7): separate plans
- Consumer-repo ændringer (DataMigration/TimeSeriesMigration/CustomerSupport): brugerens ansvar i separate sessions efter CI har publiceret 0.8.0-preview
