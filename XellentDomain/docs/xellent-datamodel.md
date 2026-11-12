# Xellent-datamodellen

Den del af Dynamics AX 2009 som dette repo læser. Skrevet fordi de her forhold
gentagne gange er blevet gættet forkert — også af folk der havde læst koden —
og fordi et gæt her forplanter sig direkte til hvad der bliver skrevet til Settl.

**Hver påstand er enten målt eller markeret som udledt.** Målinger står med
database og dato, så den næste læser kan se hvor gammelt belægget er i stedet
for at tage det på tro. Tallene nedenfor er `DATAAREAID='hol'` medmindre andet
står.

---

## 0. "Aftale" betyder to ting

Læs dette først. Ordet er overloaded i Xellent, og forvekslingen gør tal
meningsløse uden at nogen opdager det.

| | Felt | Hvad det er | Antal pr. målepunkt |
|---|---|---|---|
| **Installationsaftale** | `EXU_AGREEMENTTABLE.AGREEMENTNUM` | Leddet mellem kontraktdel og delpoint — et rent join-trin | Præcis én, altid |
| **Betalingsaftale** | `PBSAGREEMENTNUM`, `MOBILEPAYSUBSCRIPTIONAGRE20135` | Hvordan kunden betaler | Nul, én, eller delt med mange andre |

Dette dokument skriver **betalingsaftale** hver gang det er en betalingsaftale.
Står der bare "aftale", er det installationsaftalen i kæden nedenfor.

Samme regel i koden: rapporternes kolonner hedder `Betalingsaftale-ID`,
`Betalingsaftale-status`, `Målepunkter med egen betalingsaftale`. De hed
"Aftale-…" indtil 2026-08-07, hvor det viste sig at "målepunkter med egen
aftale" kunne læses som "dem alle sammen".

---

## 1. Kæden fra kontrakt til målepunkt

Et målepunkt hænger ikke på kontrakten. Der er tre led imellem:

```
EXU_CONTRACTTABLE          kontrakten
   │  CONTRACTNUM
EXU_CONTRACTPARTTABLE      kontraktdelen — ét produkt/en ydelse
   │  INSTAGREENUM
EXU_AGREEMENTTABLE         installationsaftalen
   │  AGREEMENTNUM = ATTACHMENTNUM
EXU_DELPOINT               delpointet
   │  METERINGPOINT
                           det 18-cifrede nationale målepunkts-id
```

**Join-nøglerne er sammensatte.** `DATAAREAID` og `COMPANYID` skal med fra
`EXU_AGREEMENTTABLE` og ned — udelades de, joiner rækker på tværs af selskaber.
`EGS_METERPOINTTYPECODEPERIOD` joines desuden på `DELIVERYCATEGORY`.

**Kun forbrugsmålepunkter tæller.** `EGS_METERPOINTTYPECODEPERIOD.METERPOINTTYPECODE`
skal være `E17/Forb` eller `FORB`. Uden det filter kommer produktions- og
udvekslingspunkter med.

Kanonisk implementering: `XellentPbsContractReader.FetchAsync`.

### Vand og varme går udenom

Delpoint-kæden gælder el (og FI, Tele, Supply, Service). Vand, Varme, Kloak og
Charge hænger på `EXU_INSTALLATION` i stedet, uden delpoint. Se
`MeteringPointStrategyFactory.DelpointCategories` i `DataMigration`.

---

## 2. Kontrakt-hierarkiet

`REFERENCETOMAINCONTRACT` peger fra en underkontrakt op på dens hovedkontrakt.

> **At være hovedkontrakt er en egenskab ved at HAVE børn — ikke ved at mangle en
> forælder.** Læses det omvendt, bliver hver eneste selvstændige kontrakt kaldt
> "hovedkontrakt". Det skete: 4.288 af 4.965 aftalebærende kontrakter var i
> virkeligheden enkeltkontrakter (AXDB50, 2026-07-31).

At afgøre om en kontrakt *er* hovedkontrakt kræver derfor sin egen forespørgsel —
rækken selv ved det ikke.

**Målt (AXDB50, 2026-08-07):**

| | Antal |
|---|---|
| Kontrakter i alt | 325.298 |
| Med forælder (underkontrakter) | 28.296 |
| Navngivet som hovedkontrakt af mindst én anden | 19.152 |

Resten er enkeltkontrakter — langt de fleste.

### Paraply-kontrakter

`DELIVERYCATEGORY` kan være tom. Det er kontoniveau-kontrakter uden egen
leverance: de ejer intet målepunkt, men bærer kundens betalingsaftale og
fællesfakturering.

| Leveringskategori | Kontrakter |
|---|---|
| `El-Ekstern` | 215.792 |
| *(tom)* | 105.721 |
| `Tele` | 3.236 |
| `FI` | 326 |
| `Service` | 191 |
| `Supply` | 32 |

De tomme er ikke en fejl eller en randgruppe — de er en tredjedel af alle
kontrakter, og de er usynlige for enhver forespørgsel der joiner via delpoint.

---

## 3. Betalingsaftaler

### PBS (Betalingsservice)

To felter på `EXU_CONTRACTTABLE`: `PBSAGREEMENTNUM` og `PBSTRANSACTIONCODE`.

| Kode | Betydning | Kontrakter |
|---|---|---|
| 0 | Ingen PBS | 166.746 |
| 1 | Aktiv | 20.472 |
| 2 | Aktiv | 63.438 |
| 3 | Aftale afmeldt af PI | 27.782 |
| 4 | Afmeldt af kreditor | 3.943 |
| 5 | Afmeldt af BS | 42.913 |
| 6 | Aktiv | 4 |

`PBSTRANSDATE` er datoen for den seneste ændring — for kode 3/4/5 altså
afmeldingsdatoen.

**Der findes tre forskellige definitioner af "aktiv PBS" i kodebasen**, og de er
ikke ens:

| Sted | Regel |
|---|---|
| `PbsContractSelection.CountsAsAgreement` | active codes 1/2/6 only; known terminated codes 3/4/5 require opt-in and date after COS |
| `PbsStatusLabel` | 1, 2, 6 = Aktiv; 3 = Aftale afmeldt af PI |
| `PaymentInformation` (create) | kun 1 eller 2 |

Det er bevidst uafklaret. Vær eksplicit om hvilken du bruger.

### MobilePay

`MOBILEPAYSUBSCRIPTIONAGRE20135` bærer betalingsaftale-id'et,
`MOBILEPAYAGREEMENTCANCELDATE` opsigelsen. **Der er ingen statuskode** — en
betalingsaftale er opsagt eller ikke. Det er derfor MobilePay-rapporten ikke kan vise de
fire tilstande PBS kan.

Kolonnen er nullable: **både `NULL` og sentinellen `1900-01-01` betyder "ikke
opsagt"**.

### Hvor betalingsaftalen sidder

Der er **ingen regel**. En MobilePay-betalingsaftale følger den kontrakt kunden tilfældigvis
tegnede sig på — vand, varme, el, ladestander, hoved- eller underkontrakt
(verificeret mod Xellent 2026-07). PBS ligeså.

**Én betalingsaftale kan dække flere målepunkter.** Hovedkontraktens betalingsaftale betaler også
for underkontrakternes målepunkter, som ingen har selv.

**Målt (AXDB50, 2026-08-07) — konti med mindst ét el-målepunkt og mindst én aktiv PBS:**

| | Konti |
|---|---|
| Flere målepunkter end betalingsaftaler (én dækker flere) | 4.100 |
| Lige mange | 44.178 |
| Færre målepunkter end betalingsaftaler | 2.368 |
| **Flest målepunkter på én enkelt betalingsaftale** | **411** |

De 2.368 med *færre* målepunkter end betalingsaftaler er paraply-kontrakterne
igen: betalingsaftaler der slet ikke hænger på et målepunkt.

**Derimod er disse to 1:1** (AXDB50, 2026-08-07):

- Intet PBS-betalingsaftalenummer sidder på to kontrakter (0 af 158.548), og
  ingen går på tværs af konti
- Ingen betalingsaftalebærende kontrakt navngiver to forbrugsmålepunkter (0 af
  97.546)
- Samme for MobilePay: 0 af 7.033 betalingsaftale-id'er på to kontrakter

Det er **ikke** det samme som at én betalingsaftale dækker ét målepunkt. Forskellen er
arven ned til underkontrakterne, og den forveksling har kostet flere fejlmålinger.

---

## 4. Datoer og sentinels

**`1900-01-01` betyder "ingen dato".** Ikke null, ikke `DateTime.MinValue`.
Gælder `CONTRACTENDDATE`, `MOBILEPAYAGREEMENTCANCELDATE`, `PBSTRANSDATE` m.fl.
Konstanten hedder `EndDateNull` og er defineret flere steder i kodebasen.

Skrives den ud i en rapport uden at blive blankstillet, står der `1900-01-01` i
en dato-kolonne, hvilket læses som data frem for som fravær.

### Migrering afslutter kontrakten

Når en kunde migreres til Settl, afsluttes deres Xellent-kontrakt **dagen før
leverandørskiftet** (COS − 1 dag).

To konsekvenser der har brændt flere gange:

1. **Et filter på "åbne kontrakter" fjerner præcis de kunder en migrations-rapport
   handler om.** De er alle afsluttede.
2. **Målestokken er cutover, ikke i dag.** Måler man mod dagens dato, forsvinder
   hele den migrerede population.

`PbsContractSelection.RanAtCutover` er den kanoniske regel: en kontrakt løb ved
cutover hvis den aldrig slutter, eller slutter tidligst dagen før COS.

Samme skelnen gælder afmeldte betalingsaftaler: afmeldt **efter** COS er
migreringens eget værk, afmeldt **før** er kundens. Gælder begge betalingsformer. Selve skiftedagen hører til
før-siden.

---

## 5. Hvem ejer et målepunkt

Flere kontrakter kan navngive samme målepunkt over tid — kunder flytter. Xellent
siger ikke selv hvem der ejer det på en given dato.

`PbsContractSelection` afgør det: blandt de kontrakter der løb ved cutover vinder
den med nyeste startdato. Filtreringen skal ske **før** valget, ellers kan en
udløbet kontrakt med senere startdato skygge for den der stadig er i kraft.

> Produktionstilfælde: målepunkt 571313115200207046 gav to rækker, der navngav
> Tank Ejendomme ApS og Randers Kommune under konto 406242, hvis kontrakt dér
> sluttede 2026-01-31. Kunden var flyttet fem måneder før cutover.

Settl kender svaret uafhængigt via sin change-of-supplier-dato pr. målepunkt, og
den kan bruges som krydstjek — se `MeteringPointAccountResolver`.

---

## 6. Slettede rækker

`EXU_CONTRACTPARTLOG` er slette-loggen for kontraktdele. En slettet del findes
**kun** der — ikke i live-tabellen. Tjek den før du konkluderer at et produkt
aldrig har eksisteret.

Bemærk at sletning i Xellent ikke i sig selv betyder at noget skal fjernes i
Settl: ved cutover flyttes fremtidige salg til Settl og fjernes fra Xellent, og
det er korrekt migrering, ikke drift.

---

## Sådan holdes dokumentet ærligt

Tallene her er øjebliksbilleder. Bliver et af dem afgørende for en beslutning,
så mål det igen og opdatér datoen frem for at stole på den gamle måling — data
flytter sig med hver migreringsbølge.

Er en påstand her uden måling, er den udledt af koden og bør behandles som
sådan.
