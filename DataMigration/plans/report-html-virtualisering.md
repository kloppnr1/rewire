> **✅ IMPLEMENTERET** — `7d0d46c0` (CHANGELOG 2026-08-06).

# HTML-migreringsrapport: virtualisering til all-active (~45.000 kunder)

## Verifikation (start her)

### Trin 1 — build + unit tests

Kør:

```
dotnet build
dotnet test src/Tests/DataMigration.OpsCli.Tests
```

**Forventet:** 0 errors. Alle tests passed, 0 failed.

### Trin 2 — regression på lille profil (output skal være uændret i indhold)

Kør:

```
cd src/DataMigration.OpsCli
dotnet run -- report erhverv-3 --output ../../output/verify-erhverv-3
```

**Verificér i browseren** (`output/verify-erhverv-3/report.html`):
- Oversigt/Detaljer/Validering/Fejl/Advarsler viser samme tal som før ændringen
- Træet viser samme konti, kontrakter og målepunkter
- Klik på et kontonummer → modal med tre faner (Kunde / Betaler / Målepunkter)
- Klik på et målepunkt i træet → modal åbner på MP-fanen med det MP udfoldet
- Søgefeltet filtrerer og tælleren `n / total` opdateres

**Succeskriterium:** ingen synlig forskel fra den nuværende rapport bortset fra
at der ikke længere står et truncation-banner.

### Trin 3 — all-active

Kør (tager ~11 min i extraction — det er uændret):

```
cd src/DataMigration.OpsCli
dotnet run -- report erhverv-all --all-active --output ../../output/verify-all-active
```

**Forventet konsol-output:** scope ~45.000 konti, ~44.000 grupperinger,
~53.000 målepunkter.

**Verificér på filen:**
- `report.html` er under ~25 MB. Målt på syntetisk payload med 45.000 kunder
  og 67.488 MPs: 6,2 MB. Rigtige data har flere kontaktpunkter og mindre
  gentagelse, så tallet bliver højere — men ikke 4× højere.

**Verificér i browseren** — dobbeltklik filen (skal virke via `file://`, ikke
kun via en webserver):
- Siden er interaktiv under 5 sekunder efter load
- Scroll gennem træet er glidende — ingen frys ved lange spring
- Søg på et kontonummer langt nede i listen (fx et fra slutningen af konsol-
  outputtet): resultat under ~200 ms
- Klik på det kontonummer → modal åbner med rigtige kunde-/betaler-/MP-data
  (det er netop dét der ikke virker i dag ud over de første 150)
- Klik på 5-10 spredte konti i forskellige dele af listen → alle har data
- Tabs skifter uden hak

**Succeskriterium:** alle 45.000 konti er klikbare med detaljer. Fejler ét
enkelt opslag med tomt/manglende data er shard-opslaget forkert — stop.

### Trin 4 — spot-check mod kildedata

Vælg 2 konti fra rapporten (én enkeltkonto, én der indgår i en sammenlagt
gruppe). **Verificér** at kundenavn, CVR, adresse og MP-liste i modalen
matcher hvad `report erhverv-3`-stil-visningen viser for samme konto, og at
den sammenlagte gruppes repræsentant er den med lavest kontonummer.

## Kontekst

Rapporten renderer i dag hele træet som DOM server-side og kan derfor ikke
bære all-active: ~150.000 DOM-noder, og detaljedata er hårdt afkortet til de
første 150 kunder. Data skal flyttes til browseren og rækkerne virtualiseres,
så rapporten forbliver én fil man kan dobbeltklikke.

## Ændringer

### 1. `report`-kommandoen skal kunne køre all-active

- Nyt `--all-active`-flag på `report` med samme semantik som `create --all-active`
  (`src/DataMigration.Migration/Program.cs:181`): scope udtrykkes af loaderens
  egne prædikater, ikke som en konto-liste.
- `IXellentDomainLoader.LoadAsync` tager `string[]?` i stedet for `string[]`;
  `null` = hele dataområdet. Den underliggende `XellentDomainLoader.LoadAsync`
  (NuGet) accepterer allerede `null` — ingen producer-bump.
- `ReportRunOptions` får `LoadEntireDataArea`; `ReportRunner` sender `null`
  videre når den er sat.
- Mapping-servicen behøver ingen ændring — `SettlMappingService.Extract*Async`
  ignorerer allerede sit `accountNumbers`-argument og læser alt fra
  `_loadResult`.
- `--output` defaulter til `output/{profil}` — uændret, profilnavn er stadig
  påkrævet på `report`.

### 2. Træet emitteres som data, ikke som DOM

- Serveren bygger et fladt række-array i stedet for at appende HTML i
  `MigrationReportGenerator.cs:492-575`. Én række pr. owner-gruppe, konto,
  kontrakt-label og målepunkt, som kompakte tupler (type, indryk, tekst, id,
  badge, MP-antal) — ikke navngivne objekter.
- Owner-grupperækker bærer antal efterfølgende børn, så collapse kan udregnes
  som interval-udeladelse i et synligt-indeks-array uden at røre DOM.
- Klienten renderer kun de synlige rækker plus overscan i en container med
  fast rækkehøjde.
- `content-visibility`-hacket på `.tree` fjernes — det er ikke længere nødvendigt.

### 3. Detaljedata komprimeres og lazy-parses

- Kunder, betalere og målepunkter bucketes i shards (start: 500 kunder pr.
  shard, med betalere og MPs for de samme kunder i samme shard, så ét klik
  koster netop ét shard-opslag).
- **Hvert shard gzip'es og base64-encodes for sig** og lægges i et array i
  filen. Ét samlet gzip-blob ville tvinge hele payloaden gennem
  dekomprimering ved første klik — det er hele pointen med at shard'e.
- Klienten dekomprimerer med `DecompressionStream('gzip')` ved klik og cacher
  det parsede shard.
- Række-index + søgeindeks ligger i sit eget shard som dekomprimeres ved load.
- `maxTreeCustomers`-parameteren og truncation-banneret
  (`MigrationReportGenerator.cs:59, 90, 475, 479`) fjernes.

### 4. Søgning bliver index-baseret

- `filterTree` (`MigrationReportGenerator.cs:922`) erstattes. Den scanner i dag
  alle `.acct`/`.merged-acct` med sibling-walk pr. tastetryk.
- Serveren bygger et lowercase søgefelt pr. kontorække (kontonr + navn + CVR)
  og et MP→rækkeindeks-opslag.
- Klienten filtrerer indeks-arrayet, bygger et nyt synligt-indeks og
  genrenderer den virtuelle liste. Debounce på input.

### 5. Blast radius

- `OpsMigrationReportGenerator.GenerateHtmlReport` har præcis én caller
  (`ReportRunner.cs:196`). Dashboardet bruger den ikke — det har sin egen
  create-dry-run-rendering i `MigrationDashboard`.
- `IXellentDomainLoader`-signaturændringen rammer kun implementeringer i
  `DataMigration.OpsCli.Library` og test-doubles.

## Målt (syntetisk 45.000-kunde-payload, 67.488 MPs)

Genereret lokalt og kørt gennem klient-JS'en i node med DOM-stub:

| | |
|---|---|
| Filstørrelse | 6,2 MB |
| Index (base64) | 2,4 MB — 188.153 rækker, 883 grupper, 45.000 blokke |
| Shards | 90 stk. à 44 KB base64 |
| Pak index ud + parse | 506 ms (engangs ved load) |
| Pak ét shard ud | 68 ms (pr. første klik i shardet) |
| Søgning over 45.000 blokke | 8 ms |
| Genberegn synlige rækker | 11 ms |
| Heap efter load | 84 MB |
| Rækker i DOM ad gangen | 45 |

## Risici og åbne punkter

- **Rigtige data er tungere end den syntetiske payload.** Den syntetiske
  mangler `ContactPoints` og gentager sig selv mere, hvilket gzip belønner.
  Rammer filen alligevel over ~25 MB, er `ContactPoints` den oplagte kandidat
  til at slanke shard-indholdet.
- **`DecompressionStream` kræver moderne Chrome/Edge.** Ingen fallback —
  fejler den, viser træet en fejlbesked i stedet for at stå tomt.
- **Extraction-tiden er urørt** — ~11 min for all-active, jf.
  `plans/whole-area-extraction-performance.md`.
- Fejl/advarsels-sektionerne er allerede mønster-grupperede server-side og
  skalerer, men er ikke målt ved all-active-volumen.
- Adfærdsdelta: en sammenlagt konto åbnede før en tom `alert()` (den findes
  ikke som selvstændig kunde i Settl). Den åbner nu gruppens repræsentant.
