> **✅ IMPLEMENTERET** — `0c5d9820`
>

## Verifikation (start her)

### Hvad der beviser at det virker
Kør `export-customer-pricelists` for de to bevis-kunder og inspicér output-Excel'en (read-only — læser Xellent + log, skriver kun Excel):
```
dotnet run --project src/DataMigration.OpsCli -- export-customer-pricelists \
  --profile <lille-profil-med-327315-og-345476> --pricelist-csv <csv> --output <xlsx>
```

### Forventet output / succeskriterium
- **327315:** SME-tillæg-rækken (`E_SME_Tillæg_Kd.pl.`) har slutdato **2028-06-30** (ikke 2026-12-31).
- **345476:** der emitteres nu **et SME-tillæg** (`E_SME_Tillæg`, prisliste fra `ST 0250`) for produkt 2 — ikke kun KAM-tillægget.
- Ingen regression: kunder uden slettede go-forward-dele får uændret output (diff før/efter på en bred profil).
- Unit tests grønne.

## Kontekst
Prisliste-tillæg i Settl får forkert slutdato (note 4) eller mangler helt (note 6), fordi `ExportCustomerPriceListsRunner` kun læser den **levende** `EXU_CONTRACTPARTTABLE`. Kundens go-forward-produkter (fremtidige salg gældende fra COS) blev slettet fra Xellent i cutover-batchen 2026-05-01 og lever nu kun i `EXU_CONTRACTPARTLOG`.

## Databevis (to bekræftede sager)
- **327315 / 10514077:** live `V SMEspot`/`ST 0500` (log-end 2026-12-31). Go-forward `V SMESpotH`/`ST 0800` (2026-05-01→**2028-06-30**) slettet ved cutover → kun i log. Tillægget arver ST 0500's 2026-12-31.
- **345476 / 10513865 m.fl.:** live `VGGSpot10`/`ST 0300` (→ KAM-tillæg ✅). Go-forward `V SMESpotH`/`ST 0250` (2027-01-01→2027-12-31) slettet ved cutover → SME-tillæg **mangler**.

Begge produkter (`V SMEspot`, `V SMESpotH`) mapper til **samme ProductElement** `E_SME_Tillæg_Kd.pl.` (regel `Contains "SME"`), men til **forskellig prisliste** (ratecode `ST 0500`/`ST 0800`/`ST 0250`).

## Foreslået ændring (`ExportCustomerPriceListsRunner`, OpsCli) — INGEN ny query
**Nøgleindsigt:** den eksisterende `LookupContractPartLogEndDatesAsync` (eneste consumer af
`EXU_CONTRACTPARTLOG`) returnerer allerede den nyeste log-række pr. `(ContractNum, AmrCode,
ProductNum, Startdate)` — så `contractPartLogEndDates`-dictionary'en **indeholder allerede**
go-forward-delene (fx `(10514077, ST 0800, V SMESpotH, 2026-05-01)` → 2028-06-30). Exporten
slår dem bare kun op på **levende** del-keys (linje 184) og emitterer aldrig rækker for de
log-only keys.

- Identificér **log-keys uden levende modstykke** = slettede go-forward-dele (de keys i
  `contractPartLogEndDates` hvis `(ContractNum, AmrCode, ProductNum, Startdate)` ikke optræder
  i `accountMpMapping`).
- Emittér en prisliste-række for hver (ProductNum → ProductElementCode; AmrCode →
  PriceListIdentifier via CSV; periode = log-end-date; Start = COS som de øvrige).
- Mangler: account/representative pr. log-key. ContractNum → account via eksisterende
  `contractAccountMap`; account → representative skal eksponeres (merge-resolution findes
  allerede i loadResult). Genbrug `dedup`/grupperingen uændret.

## Åbne beslutninger (kræver dit input — designet afhænger af dem)
1. **Forgænger vs. efterfølger i samme ProductElement.** `V SMEspot`/`ST 0500` (slut 2026-12-31) og go-forward `V SMESpotH`/`ST 0800` (slut 2028-06-30) er **samme** ProductElement (`E_SME_Tillæg`) men **forskellig prisliste**. Settl tillader kun én prisliste pr. ProductElement pr. periode. Skal go-forward-delen (ST 0800) **erstatte** forgængeren (ST 0500) for SME-tillægget, eller skal grupperingen ændres til at vælge go-forward-prislisten + periode? (Nuværende gruppering er pr. `(kunde, ProductElement, PriceList)` — to forskellige prislister giver to rækker → overlap-konflikt i Settl.)
2. **Startdato.** Eksisterende kode kollapser Start til `ChangeOfSupplierDate` for alle rækker. For en go-forward-del der starter senere (fx 345476's SMESpotH 2027-01-01) — skal Start stadig være COS, eller delens egen startdato? (COS-start ville dække et hul 2026-05-01→2026-12-31 hvor kunden ikke er på SMESpotH.)
3. **Hvilke log-rækker tæller som "go-forward-del".** Kun `Startdate >= COS` OG ikke-i-levende-tabel (slettet)? Eller også ændrede dele? Skal undgå at medtage rene end-date-ændringer på levende dele.
4. **Proces vs. kode.** Buggen opstår kun når exporten køres **efter** cutover (go-forward-dele slettet). Skal exporten fremover altid køres **før** cutover (proces-fix), og kode-fix'et kun for allerede-cutover'ede kunder? Eller altid læse loggen?

## Noter
- Read-only runner (producerer Excel til bulk-import) — ingen Settl-mutation i selve exporten.
- Adfærdsændring i `export-customer-pricelists` → CHANGELOG-række ved commit.
- **Ingen ny query/loader-metode** → ændringen er isoleret til `ExportCustomerPriceListsRunner` i **dette repo** (OpsCli). Ingen XellentDomain-bump, intet cross-repo flow. Den eksisterende `LookupContractPartLogEndDatesAsync` genbruges som den er.
