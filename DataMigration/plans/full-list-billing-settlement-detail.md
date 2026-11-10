> **✅ IMPLEMENTERET** — `30dd66ea` + `d3cdab03` + `4621bd29`
>

# Kompakt billing/settlement-detalje for ALLE kunder i rapporten

Mål: konto-træet i den printbare migreringsrapport skal vise de **afgørende
billing/settlement-felter pr. kunde/MP for alle ~14k kunder** — i lettere
detaljegrad end de eksisterende 100 "fuld payload"-eksempelkort (som bevares
uændret). Ingen identitets-/adresse-/kontaktfelter.

## Verifikation (start her)

### DataMigration (pricelist-extraction + eksponering)
- Kør: `dotnet build` på solution → forventet: 0 errors.
- Kør: `dotnet test src/Tests/DataMigration.Migration.Tests` → forventet: alle passer, 0 failed.
- Kør offline-smoke (ingen CSV):
  `dotnet run --project src/DataMigration.Migration -- create --profile b2c-maaned-bagud --offline --report-json out.json`
- Verificér i `out.json`: nyt felt `MpPriceLists` udfyldt (navn + start + slut) for MPs med
  AMRCODE; `MpBillingExemptions`, `CustomerSettlementGroups`, `MpContractLinks` stadig udfyldt.
- Spot-check: vælg 2-3 MPs, verificér pricelist-navnet = `TranslateAmrcode(AMRCODE)` og at
  start/slut matcher ContractPart-vinduet (slut via log/tbl-go-forward-logikken).

### MigrationDashboard (rendering)
- Kør: `dotnet build` på Dashboard.Api → 0 errors. `npm run build` i `src/dashboard` → 0 errors.
- Kør: `npm test -- customerVariation` (+ evt. ny render-test) → passer.
- Generér en rapport for en profil der HAR exemption + settlement-groups (fx PPA) og
  for `b2c-maaned-bagud`, åbn HTML'en.
- Verificér i træet (alle kunder, ikke kun de 100):
  - hver MP viser kompakt: kontrakt, produktnr., betalingsbetingelse, samlefaktura-badge,
    billing-exemption-markør (hvor data), pricelist (hvor data).
  - hver kunde viser settlement-group(s) (hvor data).
- Spot-check: vælg en kunde der OGSÅ er i de 100 fuld-payload-kort; bekræft at de kompakte
  træ-felter matcher detaljekortets værdier.
- Bekræft at B2C stadig viser tomt for exemption/settlement-group (forventet — ingen data),
  mens PPA viser udfyldt.

### Succeskriterium
- Træet har billing/settlement-felter for ALLE kunder; de 100 fuld-payload-kort er uændrede.
- HTML-størrelse stiger acceptabelt (kompakte felter, ikke fuld payload × 14k).

## Kontekst
Rapporten renderer i dag fuld per-kunde-detalje for kun 100 variations-udvalgte kunder;
resten af listen (konto-træet) viser kun badges + kontraktnavn. Brugeren vil have de
afgørende billing/settlement-data synlige for hele listen.

## Kompakt feltsæt (afklaret)
Pr. MP i træet: Settl-kontrakt (navn + **start/slut**) · ProductNum · PaymentTerm ·
Samlefaktura · Billing-exemption (template + %) · Pricelist (navn + **start/slut**).
Pr. kunde: Settlement-group(s) (forventet).
**Start/slut-datoer er obligatoriske** for de periode-baserede data (kontrakt, pricelist).
**Owner-cluster-niveau skal understøttes**: de syntetiske owner-customers (≥900000, der
wrapper flere repræsentanter på samme TG4) skal indgå i den fulde liste — gruppering/rollup
over de underliggende repræsentanters billing/settlement-data. Data findes allerede
(`OwnerCustomers`, `ExternalOwners`, `customer.Owner.OwnerId`) — primært en træ-rendering-opgave.
SettlementMethod: **udeladt**. Payer-kanaler/EAN: ikke i scope. Kun dashboard-rapporten.

## Datatilgængelighed
- **Allerede på frontendens report-objekt for alle:** mpContractNames, mpProductNums,
  mpPaymentTerms, joinedMeteringPoints. → ingen plumbing (men uden datoer).
- **Findes i create-`MigrationReport`, men ikke eksponeret på report-niveau:**
  MpBillingExemptions, CustomerSettlementGroups, **MpContractLinks** (har start/slut/joined
  pr. mp+kontrakt — kilden til kontrakt-datoerne). → plumbing (ingen ny extraction).
- **Pricelist (navn + datoer) — delvis opløsning UDEN CSV:**
  - Navn = `TranslateAmrcode(mp.AmrCode)` ("KLI "→"ST ", ellers uændret). AMRCODE er
    allerede i MP-mappingen. Ren Xellent-data, ingen CSV.
  - Start = `ContractPartStartdate`. Slut = log/tbl-logikken fra
    `ExportCustomerPriceListsRunner` (`LookupContractPartLogEndDatesAsync` +
    go-forward-håndtering) — Xellent-lookup, stadig ingen CSV.
  - **Caveat:** uden CSV ingen Settl-eksistens-validering — navnet er "som Xellent har det",
    ikke Settl-bekræftet. Settl PriceList-ID udelades bevidst (brugeren vil ikke have GUIDs).

## Ændringer

### NewSettlement.DataMigration (dette repo)
- Ny report-felt `MpPriceLists` på `MigrationReport`: mp-identification → liste af
  (pricelist-navn, start, slut). Genbrug navn- + slutdato-logikken fra
  `ExportCustomerPriceListsRunner` (`TranslateAmrcode` + log/tbl-end-date + go-forward) —
  flyt den til en delt helper. **Ingen CSV.**
- Datoer skal også med på kontrakt: bekræft at `MpContractLinks` (start/slut/joined) er
  populeret i create-pipelinen (det er den) — ingen ny extraction, kun eksponering.
- CHANGELOG.md-række (adfærdsændring i create — ny report-data).
- Intet `VersionPrefix`-bump (additivt felt; Dashboard.Api floater `0.1.*-*`).
  Commit develop → CI publisher ny `0.1.x-preview.{BuildId}` automatisk.

### NewSettlement.MigrationDashboard — Dashboard.Api (cross-repo)
- `dotnet restore --force` → ny DataMigration.Migration-preview.
- `FromReport` (MigrationDtos.cs): eksponér på report-DTO (for ALLE kunder, ikke kun de 100):
  `mpBillingExemptions` (mp→code/template/%), `customerSettlementGroups` (account→grupper),
  `mpContractLinks` (mp→navn/start/slut/joined), `mpPriceLists` (mp→navn/start/slut).

### NewSettlement.MigrationDashboard — frontend (cross-repo)
- `api.ts`: udvid `MigrationReport`-interface med de nye felter (inkl. dato-felter).
- `PrintableMigrationReport.tsx` (`AccountTree` / `MpsByContract`): render de kompakte
  billing/settlement-felter **med start/slut-datoer** pr. MP + settlement-group pr. kunde.
  De 100 `CustomerDetailCard` (fuld payload) ændres ikke.
- **Owner-cluster-niveau**: udvid træ-grupperingen så syntetiske owner-customers (≥900000)
  vises som et niveau over deres repræsentanter, med rollup af de kompakte data. Brug
  `customer.Owner.OwnerId` + `OwnerCustomers`/`ExternalOwners` (allerede på report-objektet).
- Evt. render-test for det kompakte træ-output + owner-cluster-grupperingen.

## Åbne beslutninger
- Ingen tilbage. Feltsæt afklaret, pricelist uden CSV, kun dashboard, datoer obligatoriske.
- (Caveat noteret: pricelist-navne er ikke Settl-eksistens-validerede uden CSV.)

## Rækkefølge (cross-repo NuGet-flow)
1. DataMigration: `MpPriceLists` (navn+datoer, ingen CSV) + delt helper + bump →
   commit develop → CI preview.
2. Dashboard.Api: restore → FromReport-eksponering (4 felter) → commit.
3. Frontend: api.ts + AccountTree-render med datoer → commit.
