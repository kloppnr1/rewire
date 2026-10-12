# Whole-area extraction: performance + resolver-scope

## Verifikation (start her)

### Trin 1 — korrekthed: hvilke kunder skifter nummer?

Ændringen flytter 1.060 kontis repræsentant og fjerner 15 kunder fra
extraction-outputtet. Repræsentanten ER `CustomerNumber` i Settl, så en
allerede migreret kunde der skifter nummer bliver oprettet som ny ved næste
`create`. Det skal afklares FØR ændringen går videre.

Kør (production migration-state, ikke den lokale 276-entry kopi):

```
type C:\Services\DataMigration\OpsCli\data\migration-state.json | findstr /C:"518696" /C:"523209" /C:"523259" /C:"523405" /C:"525111" /C:"525358" /C:"525364" /C:"525474" /C:"525475" /C:"525476" /C:"525477" /C:"525478" /C:"525480" /C:"525486" /C:"525487"
```

**Succeskriterium:** ingen træffere. Én eneste træffer = den kunde findes
allerede i Settl under et nummer der forsvinder → stop, ændringen skal
justeres eller droppes.

### Trin 2 — output-ækvivalens fra dashboardet

Kør en all-active dry-run (El-ekstern, samme referencedato som sidst) fra
dashboardet efter at pakken er landet.

**Succeskriterium:** scope ~45.000, 0 fejlede, ingen "internal resources" i
loggen, og kunde-/MP-antal inden for de 16 rækker der er redegjort for
ovenfor. Afvigelser derudover = uforklaret, ikke godkendt.

### Trin 3 — tid

Samme dry-run, mål vægurets tid på extraction-fasen.

**Succeskriterium:** markant under den nuværende ~11 min. Målt i harness:
661,1s → 192,2s (3,4×). Bemærk at harness sætter `NoTracking` +
1800s timeout; produktionsvejen gør ikke (se ændring 3), så tallet
overføres ikke 1:1.

## Kontekst

En all-active dry-run bruger ~11 minutter på at extracte ~45.000 konti,
fordi repræsentant-resolveren læser hele dataområdet (414.900 konti) og
sender ~396.000 repræsentanter videre til de chunkede nedstrøms-queries.

## Ændringer

### 1. Afgræns resolverens whole-area-scope (ikke committet, målt)

- `AccountRepresentativeResolver.ResolveAsync` får `wholeAreaDeliveryCategory`,
  kun konsulteret når `accounts` er null. Anvendt som korreleret `EXISTS`,
  så konto-id'erne bliver i databasen.
- `CustomerMapper` sender mapperens commodity med.
- Effekt: 414.900 → 44.986 konti i resolveren; 396.062 → 43.786 videre
  nedstrøms. Resolver-queryen selv bliver langsommere (17,1s → 28,3s);
  gevinsten ligger i de ~9× færre chunks nedstrøms.
- **Åbent:** 15 kunder forsvinder, 1 kommer til. Færre merges burde give
  flere kunder, ikke færre — mekanismen er ikke forklaret endnu. Skal
  forstås før commit, ikke bare accepteres fordi tallet er lille.

### 2. Allerede leveret (committet + pushet)

- `9925c8b` — kontrakter grupperes én gang i stedet for scan pr. kunde
  (kvadratisk, målt 66s).
- `650fab1` — `.Distinct()` på tre MP-hjælpe-queries, dedup i SQL.
- Dashboard `2024c89` — `BuildAllActiveOptions` sender ikke længere hele
  scopet tilbage som query-parametre (årsagen til SQL-fejl 8623).

### 3. `XellentDomainLoader` mangler `NoTracking`

- Produktionsvejen sætter ikke `QueryTrackingBehavior.NoTracking` og har
  `CommandTimeout = 120s`. Harness sætter begge.
- Målt i harness: −48s. Skal måles på den rigtige vej før den tælles.
- Change tracking på ~48.000 entities koster også hukommelse uden gevinst
  — loaderen er read-only.

### 4. Uafklarede punkter (ikke startet)

- `MeteringPointMapper` har en query der returnerer 1.124.661 rækker.
  Ikke undersøgt.
- ~578s klient-side CPU var uforklaret ved sidste profilering. Efter
  ændring 1 er billedet et andet og profileringen skal gentages.
- `BuildMpOrderIdentifiersAsync` bruger `TryAdd` uden stabil tie-break;
  `mp order ids` flyttede sig 53.402 → 53.403 mellem de to kørsler.
  Sandsynligvis samme nondeterminisme som `PayerMapper.FirstOrDefault`.
- Journal-entry `all-active-El-ekstern-20260901-20260804-071341` hænger som
  `Running` i `all-active-runs/`.

### 5. Udrulning (når 1 er afklaret)

- XellentDomain: bump `VersionPrefix`, commit til `develop`, vent på CI
  preview-pakke.
- DataMigration + MigrationDashboard: `dotnet restore --force`, build,
  test, commit.
- Pak ikke lokalt — floating refs ignorerer `-dev` alligevel.
