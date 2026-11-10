> **✅ IMPLEMENTERET** — `ea458e16` + `3a90098d`
>

# Plan: MP-niveau e-faktura-reference tag (`E-faktura-ref (MP)`)

## Verifikation (start her)

### Hvad der beviser at det virker
- **DataMigration build + unit:** `dotnet build` → `dotnet test src/Tests/DataMigration.Migration.Tests` → 0 errors, alle grønne.
- **Golden-suite (extraction):** `dotnet test src/Tests/DataMigration.Xellent.IntegrationTests/`
  — bekræft `MpBillingOverrides` er populeret for spot-case 453131 (se nedenfor); promovér kun goldens med eksplicit tilladelse.
- **Dry-run create (felt synligt i report):** kør `create --dry-run` mod featuretest for en profil med kendt MP-override (453131) og verificér at dry-run JSON/report indeholder `MpBillingOverrides[mpId]` med de divergerende felter.
- **Dashboard render:** efter MigrationDashboard-delen — start dashboard, åbn rapport for 453131, bekræft taggen `E-faktura-ref (MP)` på de MPs der har override.

### Forventet output / succeskriterium
- Spot-case **453131** (jf. payer-billing-per-field plan): `OrderIdentifier` på MP for 10490407/10490416; `AgreementIdentifier` på alle 8 MPs → disse MPs får taggen, payeren bærer kun uniforme felter.
- MPs uden divergens får **ikke** taggen (BuyerInvoicing = null).
- Ingen ændring i Settl-wire-payloads (rent read/display — ingen mutation).

## Kontekst
MP-niveau e-faktura-referencer (`MeteringPoint.BuyerInvoicing`, allerede korrekt i XellentDomain) føres frem til dashboardets dry-run-data så de kan markeres med en variation-tag. XellentDomain-modellen ændres IKKE.

## Ændringer

### NewSettlement.DataMigration (producer — først)
- `MigrationReport`: nyt felt `MpBillingOverrides` (dict `mpId` → {OrderIdentifier, AgreementIdentifier, BuyerReferenceIdentifier}).
- `CreateRunner`: populer fra `loadResult.MeteringPoints` hvor `mp.BuyerInvoicing != null` (samme mønster som `MpProductNums`/`MpBillingExemptions`).
- Bump `VersionPrefix` (libs.yml-pakke) → commit develop → vent CI-preview.
- CHANGELOG-række (rører `create`-adfærd/report-shape).

### NewSettlement.MigrationDashboard (consumer — efter CI-preview)
- `dotnet restore --force` → træk ny `DataMigration.Migration`-preview.
- `Dashboard.Api/Models/MigrationDtos.cs`: 3 felter på `DryRunMeteringPointDto`, populeret fra `report.MpBillingOverrides` (samme sted som exemption-mapping).
- `dashboard/src/data/api.ts`: 3 felter på `DryRunMeteringPoint`.
- `dashboard/src/lib/customerVariation.ts`: tilføj `'E-faktura-ref (MP)'` til `VARIATION_TAGS` + push når et MP har non-null override.
- (valgfrit) vis felterne i MP-detalje-sektionen.

## Rækkefølge (cross-repo NuGet)
1. DataMigration: felt + populate + tests → bump → commit develop → vent CI.
2. MigrationDashboard: restore --force → DTO + api.ts + tag → build/lint → commit.

## Åbne punkter
- Bekræft `MigrationDashboard`-repo er ledigt (anden session?).
- Afklar om golden-suite serialiserer report-DTO (om `MpBillingOverrides` dukker op i goldens → promovering kræves m. tilladelse).
- Dansk vs. eksisterende `E-faktura/EAN`-tag: navnet `E-faktura-ref (MP)` valgt for at skelne payer-EAN fra MP-override.
