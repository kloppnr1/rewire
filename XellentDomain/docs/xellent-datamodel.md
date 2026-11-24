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

## 7. DataHub-beskeder og bundling

DataHub sender beskeder for mange målepunkter samlet i én besked (en bundle).
Xellent gemmer dem **ikke** samlet: hver bundle deles ved modtagelsen op i én
hændelse pr. målepunkt. Den rå bundle findes ikke i databasen.

Målt i Xellent 2026-11-18 (produktion), læst af brugeren, kun id'er og antal.

### Tabellerne

| Tabel | Hvad den holder | Nøgle |
|---|---|---|
| `EMS_ZDATAHUBLOG` | Én logrække pr. målepunkt pr. besked (`METERINGPOINT`, `MESSAGEDIRECTION` 1 = ind, 2 = ud, `MESSAGEINTYPE`, `CREATEDDATETIME`) | `ID` |
| `EMS_ZDATAHUBLOGMESSAGES` | Kobler logrække og hændelse; `DCMESSAGEID` er fælles for alle dele af én DataHub-besked | `DATAHUBLOGID` = log `ID`, `ZEVENTQUEUEREFRECID` |
| `EXU_ZEVENTQUEUEDATATABLE` | Hændelsens XML i `XMLDATA` (ntext) — kun det ene målepunkts del | `ZEVENTQUEUEREFRECID` |
| `EXU_ZEVENTQUEUETABLE` | Hændelseskøens hoved, som `ZEVENTQUEUEREFRECID` peger på. Ikke mappet i `XellentData` | — |

Alle joins går også på `DATAAREAID`.

### Sådan deles en bundle

- **Op til 100 målepunkter pr. DataHub-besked.** 2026-08-31 (cutover, 52.651
  indgående, næsten alle `MESSAGEINTYPE` 100 = `DK_NotifyChangeOfSupplier`):
  510 `DCMESSAGEID` med præcis 100 målepunkter og 100 hændelser, resten 1–96.
- **Én hændelse og én logrække pr. målepunkt.** Alle 52.651 hændelser den dag
  hører til præcis ét målepunkt.
- **XML'en er kun målepunktets egen del.** I en bundle på 100: 100 hændelser,
  100 forskellige målepunkter i XML'en, og alle 100 svarer til logrækkens
  `METERINGPOINT`. Hver XML er 1.331 tegn med ét `MeteringPointDomainLocation`.
- **Opdelingen sker ved modtagelsen.** Hændelserne har fortløbende id'er
  (5713740040, …041, …) og logrækkerne oprettes 3–5 i sekundet, så en bundle på
  100 tager omkring 25 sekunder.
- **Står samme målepunkt to gange i en bundle, får det to hændelser.** 2026-08-31:
  tre beskeder med 2 hændelser for 1 målepunkt, én med 10 for 8.
- **`DCMESSAGEID` er det eneste der binder delene sammen igen.**

Det gælder alle indgående beskedtyper i loggen. Juni 2026: ingen hændelse nævner
mere end ét målepunkt (`DK_NotifyMasterDataMeteringPoint`, `…Charge`,
`…Consumer`, `DK_NotifyChangeOfSupplier`, `DK_ConfirmUpdateMasterDataConsumer`,
`DK_NotifyVolumes`, `DK_RequestReallocateChangeOfSupplier`).
`DK_NotifyChargeInformation` (priser) og `DK_Acknowledgement` er ikke bundet til
et målepunkt og nævner ingen.

### Den rå bundle findes ikke

Hver kolonne i en DataHub-, event-queue- eller ebIX-tabel der kan holde en stor
tekst eller binær værdi blev størrelsesmålt. En bundle på 100 er omkring
266.000 bytes i `ntext`.

| Kolonne | Største værdi (bytes) | Over 100.000 |
|---|---|---|
| `EXU_ZEVENTQUEUEDATATABLE.XMLDATA` | 141.906 | 4.599 |
| `EMS_ZDATAHUBLOGNOTIFICATI25024.ACTION` | 31.295 | 0 |
| `EXU_ZEVENTQUEUELOGTABLE.ACTION` | 7.946 | 0 |
| `EXU_ZEVENTQUEUELOGTABLE.TEXT` | 5.856 | 0 |
| `EMS_EBIXERRORDESCRIPTIONS.ERRORDESCRIPTION` | 5.206 | 0 |
| `EMS_ZDATAHUBLOGERRORS.ERRORDESCRIPTION` | 1.322 | 0 |

De 4.599 store `XMLDATA`-rækker er alle `EbixRequestChargeInformationConfirmedEventBody`
(`hol` 2.752, `han` 1.847): bekræftede pristabeller, ikke målepunkter. Små
bundles under 100.000 bytes udelukkes af juni-optællingen ovenfor.

Er den rå besked nødvendig, ligger den hos det der tog imod DataHub-beskederne
foran Xellent, ikke i Xellent.

### Hvad det ikke dækker

- **Måleværdier.** Ingen tidsseriebesked findes i `EMS_ZDATAHUBLOG`; den eneste
  med mængder er `DK_NotifyVolumes` (25 i juni 2026). Måleværdierne kommer en
  anden vej ind og lander i `EMS_TIMESERIESVALUES`. Hvordan deres bundles
  håndteres er ikke undersøgt.
- **Udgående beskeder** (`MESSAGEDIRECTION` 2) er ikke undersøgt.

### Hvornår loggen stopper

Sidste indgående 2026-09-17 09:07, sidste udgående 2026-09-17 09:40. Efter
cutover (2026-08-31: 52.651 indgående) kun en hale på nogle hundrede om dagen.
Retning 0 er en gammel kode, sidst brugt 2016-08-26.

### Forespørgsler

XML-metoder er ikke tilladt i `GROUP BY`; beregn i en underforespørgsel. Kast
`XMLDATA` via `nvarchar(max)` og fjern UTF-8-erklæringen, ellers afviser SQL
Server konverteringen. Læs aldrig hele `XMLDATA` ud: den kan indeholde navne og
installationsadresse. Tæl elementer i stedet:

```sql
SELECT rootElement, mpsInXml, COUNT(*) AS events
FROM (
  SELECT c.x.value('local-name(/*[1])', 'nvarchar(200)') AS rootElement,
    c.x.value('count(//*[local-name()="MeteringPointDomainLocation"])', 'int') AS mpsInXml
  FROM EMS_ZDATAHUBLOG l
  JOIN EMS_ZDATAHUBLOGMESSAGES m ON m.DATAHUBLOGID = l.ID AND m.DATAAREAID = l.DATAAREAID
  JOIN EXU_ZEVENTQUEUEDATATABLE d ON d.ZEVENTQUEUEREFRECID = m.ZEVENTQUEUEREFRECID AND d.DATAAREAID = m.DATAAREAID
  CROSS APPLY (SELECT CAST(REPLACE(CAST(d.XMLDATA AS nvarchar(max)), 'encoding="UTF-8"', '') AS xml) AS x) c
  WHERE l.DATAAREAID = 'hol' AND l.MESSAGEDIRECTION = 1
    AND l.CREATEDDATETIME >= '2026-06-01' AND l.CREATEDDATETIME < '2026-07-01'
) t
GROUP BY rootElement, mpsInXml
```

Bundles pr. `DCMESSAGEID`:

```sql
SELECT events, mps, COUNT(*) AS messages
FROM (
  SELECT m.DCMESSAGEID, COUNT(DISTINCT m.ZEVENTQUEUEREFRECID) AS events, COUNT(DISTINCT l.METERINGPOINT) AS mps
  FROM EMS_ZDATAHUBLOG l
  JOIN EMS_ZDATAHUBLOGMESSAGES m ON m.DATAHUBLOGID = l.ID AND m.DATAAREAID = l.DATAAREAID
  WHERE l.DATAAREAID = 'hol' AND l.MESSAGEDIRECTION = 1
    AND l.CREATEDDATETIME >= '2026-08-31' AND l.CREATEDDATETIME < '2026-09-01'
  GROUP BY m.DCMESSAGEID
) x
GROUP BY events, mps
```

---

## Sådan holdes dokumentet ærligt

Tallene her er øjebliksbilleder. Bliver et af dem afgørende for en beslutning,
så mål det igen og opdatér datoen frem for at stole på den gamle måling — data
flytter sig med hver migreringsbølge.

Er en påstand her uden måling, er den udledt af koden og bør behandles som
sådan.
