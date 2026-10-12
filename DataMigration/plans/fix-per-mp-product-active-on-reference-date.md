> **✅ IMPLEMENTERET** — `294549eb` (overlap med fix-mp-product-selection-spot-vs-real)
>

## Verifikation (start her)

### Hvad der beviser at det virker

**Trin 0 — UtilityData (core-ændringen ligger her, NuGet-pakke):**
Byg + kør Xellent integration tests efter ændringen i `NewSettlement.UtilityData`:
```
cd src/Tests/DataMigration.Xellent.IntegrationTests
dotnet test
```
(Goldens skal gennemgås + promoveres manuelt — vis diff, vent på eksplicit ja.)

**Trin 1 — DataMigration (efter pakke-bump → CI-preview → restore):**
Dry-run `create` mod featuretest for de tre repræsentanter, med eksplicit reference-dato:
```
dotnet run --project src/DataMigration.Migration -- create \
  --profile <lille-profil-med-de-tre-grupper> --env featuretest --dry-run \
  --reference-date 2026-04-30
```

### Forventet output / succeskriterium
- 0 failed integration tests.
- I dry-run-rapporten linkes hvert MP til **præcis ét** Settl-kontrakt = det produkt hvis periode dækker reference-datoen:
  - 123753's MP → `E_ErhKvt` (ikke `E_SME_Spot*`)
  - 41712's MP → det produkt der reelt er aktivt på reference-datoen (forventet `V ErhKvt`→`E_ErhKvt` ved refdato 2026-04-30; `V SMESpotH` starter 2026-04-01 — se åben beslutning om refdato)
- Ingen MP optræder med flere kontrakt-links fra samme konto.
- **Impact-tjek:** kør samme dry-run for en bredere erhverv-delmængde før/efter og diff antal MP→kontrakt-links. Forventning: kun MP'er med flere in-window-dele ændrer sig; resten uændret.

## Kontekst
Ved merge af samme-CVR-konti lander MP'er på forkert/sekundært produkts Settl-kontrakt (fx `E_SME_Spot`) i stedet for det reference-dato-aktive (`E_ErhKvt`), fordi extraction inkluderer alle dele der slutter på/efter reference-datoen uden `Startdate`-loft og uden at vælge én del pr. MP — forstærket af at `create` bruger rå `ChangeOfSupplierDate` (ikke `-1 dag`). Eksempelsager: 102284 / 123753 / 211817.

## Ændringer

### A. `NewSettlement.UtilityData` — `Internal/ContractMapper.cs` (core)
- **Del-query (linje ~114-138 el / ~143-164 vand):** tilføj `Startdate`-loft så fremtidige dele ikke fejlagtigt inkluderes: en del er kun kandidat hvis `Startdate <= referenceDate` OG (`Enddate == 1900-01-01` ELLER `Enddate >= referenceDate`).
- **Enkelt-udvælgelse pr. MP:** efter parts er hentet, gruppér på `Meteringpoint` og vælg **netop én** del = den hvis `[Startdate, Enddate]` dækker `referenceDate` (åben slutdato = dækker altid). Ved flere kandidater: senest-startende vinder (deterministisk). Emit én `MeteringPointAssignment` pr. MP i stedet for én pr. del.
- **Fallback (åben beslutning, se nedenfor):** når ingen del dækker `referenceDate`.
- Bevar eksisterende felter på assignment (Start/EndDate/AmrCode) fra den valgte del.

### B. `NewSettlement.DataMigration` — `Services/ContractNameResolver.cs`
- `ResolvePerMeteringPoint` (linje ~148-170): når ContractMapper allerede leverer én del pr. MP, vil `links.Add`-pr-del nu naturligt give ét link pr. MP. Verificér at `TryAdd`-first-wins (linje 160) ikke længere kan ramme to forskellige produkter for samme MP. Fjern den ikke-deterministiske afhængighed.

### C. Reference-dato-konsistens (åben beslutning)
- `create` (`DataMigration.Migration/Program.cs:135-139`) bruger rå `ChangeOfSupplierDate`. `fix-settlement-groups` + `export-customer-pricelists` bruger `ChangeOfSupplierDate - 1 dag`. Beslut om `create` skal aligne til `-1 dag` (så kontrakter der slutter på COS-aftenen bevares) — dette alene løser 123753-sagen uden separat fallback.

## Åbne beslutninger (kræver bruger-input før kodning)
1. **Reference-dato:** skal `create` bruge `ChangeOfSupplierDate - 1 dag` som de øvrige kommandoer? (Anbefalet — løser off-by-one for kontrakter der slutter på COS-eve.)
2. **Fallback** når ingen del dækker reference-datoen (kontrakt sluttet før): (a) seneste-sluttende del før refdato, (b) drop MP (intet link), (c) ingen — kunden skal ikke migreres. Hænger sammen med (1).
3. **Cross-repo:** core-ændringen (A) ligger i `UtilityData` (NuGet). Kræver at det repo frigives i sessionen + bump→CI-preview→restore-flow før (B) kan verificeres. DataMigration-ændringen (B/C) kan laves her.
4. **Impact-bredde:** ændringen rører ALLE migrerede kunder. Golden-diff + bred dry-run-diff er obligatorisk før commit.
