> **✅ IMPLEMENTERET** — `294549eb` (Startdate-loft reverteret `f788eeb`)
>

> **UDFALD (2026-06-10):** Tier 1's `Startdate <= referenceDate`-loft blev implementeret
> (UtilityData `96d6bd8`) og **reverteret samme dag** (`f788eeb`). Fremtidige kontrakt-dele
> **skal** migreres — Settl holder bevidst både nuværende og fremtidige kontrakter. De
> "fremtidig-læk"-sager planen beskriver var i virkeligheden korrekt migrering af legitime
> fremtidige salg (annulleret i Xellent bagefter = drift, ikke bug). Kun COS−1
> (`294549eb`) står tilbage som fix. Resten af planen bevares som analyse-historik.

## Verifikation (start her)

### Hvad der beviser at det virker
**Trin 0 — UtilityData (core, NuGet):** byg + Xellent integration tests efter ændringen:
```
cd src/Tests/DataMigration.Xellent.IntegrationTests
dotnet test
```
(Golden-diff gennemgås + promoveres manuelt — vis diff, vent på eksplicit ja.)

**Trin 1 — DataMigration (efter bump→CI-preview→restore):** offline dry-run af den berørte gruppe (repræsentant 41712):
```
dotnet run --project src/DataMigration.Migration -- create --profile erhverv-all --offline --dry-run
```

### Forventet output / succeskriterium (konkrete prod-MP'er, kan slås op i Settl under kunde 41712)
- 0 failed integration tests.
- Pr. MP linkes **præcis ét** Settl-kontrakt (ingen MP med flere kontrakt-links fra samme konto).
- **Tier 1 (fremtidig-del-læk):** MP `571313115200068593` (konto 41714) → `E_ErhVari`, **ikke** `E_SME_SpotH`. (SMESpotH-delen starter 2026-05-01, dvs. efter reference-datoen 2026-04-30.)
- **Tier 2 (transitional spot — kun hvis reglen vælges):** MP `571313115200068708` (konto 41742) → `E_ErhKvt`, **ikke** `E_SME_SpotH`. Ægte spot-kunde MP (konto 508081, 7 mdr. spot) forbliver `E_SME_Spot`.
- **Impact-diff:** kør samme dry-run for en bred erhverv-delmængde før/efter og diff antal MP→kontrakt-links pr. MP. Forventning: kun MP'er med flere in-window-dele ændrer sig.

## Problemstilling (kernen)

### Udgangspunkt
Et målepunkt (MP) i Xellent har én kontrakt-del (`ExuContractPartTable`) pr. produkt-periode. Skifter kundens produkt over tid, får samme MP **flere dele i rækkefølge** — fx kvartal → ErhKvt → spot. Samtidig kollapser merge (samme CVR + fingerprint) flere konti til **én** Settl-kunde keyet på laveste accountnum, så kunden samler MP'er fra flere søskende, hver med sin egen del-historik.

Migrationen skal pr. MP vælge **det ene** produkt der er gældende ved leverandørskiftet (reference-dato = `ChangeOfSupplierDate - 1 dag`, her **2026-04-30**) og mappe det til ét Settl-kontrakt via profilens `ProductNum`-map (`V ErhKvt`→`E_ErhKvt`, `V ErhVari`→`E_ErhVari`, `V SMESpotH`→`E_SME_SpotH` osv.).

**Konkret kunde til hele forklaringen** (alt fra AXDB50 prod, dataarea `hol`): CVR-gruppe `41431717` "Sct Peders Sogn, Randers". Medlemmer 41712/41713/41714/41742/123752/123753/403402 → **repræsentant 41712** (laveste). Merget Settl-kunde = `CustomerNumber 41712`; de øvrige er ikke selvstændige Settl-kunder, deres MP'er hænger under 41712.

### Hvad koden gør i dag (efter `-1 dag`-fixet, commit `294549eb`)
Del-filteret i `ContractMapper` (UtilityData, el-query ~linje 114-138):
```
Startdate: (intet filter)
Enddate:   == 1900-01-01 (åben)  ELLER  >= referenceDate
```
Hver del der passerer bliver sit eget `MeteringPointAssignment`, og `ContractNameResolver` danner **ét link pr. del** + et first-wins primær-navn over en query **uden `ORDER BY`**. Det er ikke "aktiv på reference-dato" — det er "slutter på/efter reference-dato". Tre under-problemer:

**A. Del der slutter på COS-aftenen blev droppet** — `V ErhKvt` der slutter 2026-04-30 ved COS 2026-05-01. **Løst** af `-1 dag` (commit `294549eb`). Eksempel: konto 123753.

**B. Fremtidig del lækker ind** (intet `Startdate`-loft). En del der **starter efter** reference-datoen tages med hvis blot dens slutdato er ≥ reference-dato.
> **Prod-eksempel — konto 41714, MP `571313115200068593`, kontrakt 10267931:**
> | Produkt | Start | Slut |
> |---|---|---|
> | V Kvartal | 2016-04-01 | 2024-06-30 |
> | V Variabel | 2024-07-01 | 2026-02-28 |
> | **V ErhVari** | 2026-03-01 | **2026-04-30** |
> | **V SMESpotH** | **2026-05-01** | 2026-05-31 |
>
> På reference-datoen 2026-04-30 er `V ErhVari` gældende. Men `V SMESpotH` (starter 2026-05-01 — **efter** skiftet) har slutdato ≥ 04-30 og lækker ind. MP'et risikerer at ende på `E_SME_SpotH`, et produkt det endnu ikke er trådt i kraft på.

**C. Ingen enkelt-udvælgelse + non-determinisme.** Passerer flere dele filteret for samme MP, dannes **flere** kontrakt-links, og primær-navnet afgøres af `TryAdd` first-wins over en query uden `ORDER BY` — databasens vilkårlige rækkefølge. MP'et kan linkes til både fx `E_ErhVari` og `E_SME_SpotH`, og hvilket der vises som aktivt er reelt tilfældigt.

### Det egentlige domæne-spørgsmål (under-problem D)
Selv med korrekt "aktiv på reference-dato"-udvælgelse er nogle søskende-MP'er **reelt** på et spot-produkt på 2026-04-30:
> **Prod-eksempel — konto 41742, MP `571313115200068708`, kontrakt 10267941:**
> | Produkt | Start | Slut |
> |---|---|---|
> | V Kvartal | 2016-04-01 | 2026-02-28 |
> | **V ErhKvt** | 2026-03-01 | **2026-03-31** |
> | **V SMESpotH** | **2026-04-01** | 2026-05-31 |
>
> Her er "aktiv på 04-30" = `V SMESpotH` (apr-maj). Brugerens note siger "skulle på E ErhKvt" — men det forudsætter en **forretningsregel** om at en kort spot-periode lige før leverandørskiftet er en *overgang* der ikke skal migreres som kontrakten, mens den foregående `V ErhKvt` (kun marts) er den "rigtige".

**Sondringen der ikke kan udledes af data/kode:** 41742 har spot i **1 måned** umiddelbart før COS (ligner overgang) — men konto **508081** (anden CVR-gruppe) har `V SMEspot` i **7 måneder** (2025-09-26 → 2026-04-30), altså en ægte spot-kunde der skal *forblive* på spot. En regel skal fange 41742 men ikke 508081. Det er et domæne-kald.

## Ændringer

### Tier 1 — korrekthed (domæne-neutral, kan shippes nu — løser B + C)
I `NewSettlement.UtilityData`, `Internal/ContractMapper.cs`:
- **Tilføj `Startdate`-loft** til del-query (el + vand): kandidat kun hvis `Startdate <= referenceDate` OG (`Enddate == 1900-01-01` ELLER `Enddate >= referenceDate`). Lukker fremtidige dele ude (B → 41714's MP rammer `V ErhVari`).
- **Vælg netop én del pr. MP:** gruppér kandidater på `Meteringpoint`, vælg den hvis `[Startdate, Enddate]` dækker `referenceDate` (åben slut = dækker altid); flere overlappende → senest-startende vinder (deterministisk). Emit ét assignment pr. MP (C).
- Følgeop i `ContractNameResolver.ResolvePerMeteringPoint` (dette repo): bekræft ét link pr. MP; fjern afhængigheden af `TryAdd` first-wins.

### Tier 2 — "transitional spot"-regel (kræver domæne-beslutning — løser D)
Kun hvis brugeren bekræfter at en kort spot-periode lige før COS skal erstattes af det foregående ikke-spot-produkt:
- Definér **hvilke produkter** er "spot" (kandidater: `V SMEspot`, `V SMESpotH`, `VGGSpot*`, `V SMEPulje`?) — se `xellent-products`-skill.
- Definér **hvornår** spot er "transitional" vs. ægte (fx: spot er sidste del OG kortere end X mdr. OG forudgået af et ikke-spot-produkt). 41742 (1 md.) skal fanges; 508081 (7 mdr.) må ikke.
- Tier 1-udvælgelsen falder da tilbage til den foregående ikke-spot-del når den aktive del er "transitional spot" → 41742's MP rammer `E_ErhKvt`.

## Åbne beslutninger (kræver bruger-input før Tier 2)
1. **Skal Tier 2 overhovedet laves?** Eller er spot-på-reference-dato korrekt, så Tier 1 er nok? Tier 1-dry-run-diff'en viser præcist hvilke MP'er det gælder.
2. **Hvilke produktkoder er "spot"?** (autoritativ liste — `xellent-products`).
3. **Definition af "transitional"** (varighed, position, foregående produkt) så ægte spot-kunder ikke fejlkonverteres.
4. **Fallback** når der ingen ikke-spot-del findes før spot-perioden.

## Noter
- Core-ændringen (Tier 1 + 2) ligger i `UtilityData` (NuGet) → kræver repo frigivet + bump→CI-preview→restore før verifikation fra DataMigration. `ContractNameResolver`-følgeop er i dette repo.
- Adfærdsændring i `create` → CHANGELOG-række i samme commit.
- Bredde: ændringen rører ALLE migrerede kunder → golden-diff + bred dry-run-diff obligatorisk før commit.
- `OBS` (fra prod-udtræk): 41742's kontrakt har `REFERENCETOMAINCONTRACT = 10006742` (joint-invoice-gruppe). Tjek at hovedkontrakten også er i det extraherede sæt for 41712, så enkelt-udvælgelsen ikke utilsigtet rører joint-group-tilknytningen.
