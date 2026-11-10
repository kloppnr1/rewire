# Domænemodel — diskussion af designvalg

Diagrammet i `domain-model.md` rejser en række spørgsmål for en ny læser:
relationer der ligner redundans, FK'er der ser ud til at gå begge veje, felter
der duplikeres på flere entiteter. Dette dokument samler hvert spørgsmål, den
rationale der findes i den primære konsument (`NewSettlement.DataMigration`),
og hvor modellen stadig efterlader læseren i tvivl.

Princip: hvis modellen kræver et eksternt dokument for at give mening, er det et
modelproblem — ikke et dokumentationsproblem. Brug derfor dette dokument til at
identificere hvor modellen kunne forbedres, ikke til at forsvare den nuværende form.

---

## 1. `MeteringPoint.JointInvoiceGroupId` ↔ `JointInvoiceGroup.MemberMeteringPointIds`

**Observation**: To FK'er der peger på hinanden via string-ids. Hvem er sandhed?

**Rationale (DM)**: `MeteringPoint.JointInvoiceGroupId` er sandhed. Den skrives af
mapperen direkte fra Xellent-data (`ContractMapper.cs:221`, `MeteringPointMapper.cs:217-219`).
`JointInvoiceGroup.MemberMeteringPointIds` er **derived** — bygges
post-mapping ved at gruppere alle MPs på deres `JointInvoiceGroupId`
(`MeteringPointMapper.cs:381-391`). Ingen konflikt-risiko: én retning driver
den anden.

**Modellen siger det ikke**: Begge sider eksponeres som almindelige collections.
Læseren har ingen måde at vide hvem der er kanonisk uden at læse mapperen.

**Mulig forbedring**: Marker `MemberMeteringPointIds` som derived (kommentar, navngiv
`DerivedMemberMeteringPointIds`, eller gør det til en metode på et lookup-objekt
i stedet for et felt).

---

## 2. `Customer.Contracts` ↔ `Contract.CustomerNumber`

**Observation**: Customer ejer Contracts (navigation), men Contract har også string-FK
tilbage til Customer. Hvorfor begge?

**Rationale (DM)**: To-fase opbygning. ContractMapper kører før Customer-aggregatet
er samlet og kender kun string-ids på det tidspunkt. Contracts bygges med
`CustomerNumber: string`, og inlines først på Customer i en senere pass
(`XellentDomainMapper.cs:75-81`: `customer with { Contracts = customerContracts }`).
Begge populeres i samme orchestration-kald, men i forskellige faser.

**Modellen siger det ikke**: Læseren kan ikke se at `CustomerNumber` er en
mapper-mellemstation. Det ligner en almindelig FK der eksisterer for selvstændig
opslags-brug.

**Mulig forbedring**: Hvis `CustomerNumber` kun lever for at understøtte mapper-flow,
hører det måske til i en source-specifik DTO i `Domain.Xellent` — ikke i den
source-agnostiske `Domain.Customers.Contract`. Source-agnostiske models bør
afspejle "færdige" data, ikke mapper-stages.

---

## 3. `Payer.ContractNumbers: IReadOnlyList<string>` (ingen `Contract.Payer`)

**Observation**: Payer kender sine contracts, men Contract kender ikke sin Payer.
Hvorfor denne retning?

**Rationale (DM)**: Payers er **computed** efter contracts. PayerMapper splitter
contracts pr. EAN i buckets — én bucket = én Payer (`PayerMapper.cs:36-61`). Hver
bucket gemmer numrene på de contracts den ejer (`PayerMapper.cs:72, 86`). Mapperen
opretter aldrig en Contract→Payer reference, fordi grupperingen sker udefra.

**Modellen siger det ikke**: At Payer er en *partitionering* af Contracts efter EAN
fremgår ikke. Det er ikke synligt at `ContractNumbers` på tværs af alle Payers
for samme Customer er en disjoint partition af `Customer.Contracts`.

**Mulig forbedring**: Hvis invarianten (disjoint partition) holder, kunne den udtrykkes
i modellen — fx `Customer.PayersByContractNumber: IReadOnlyDictionary<string, Payer>`
i stedet for to løse lists hvor relationen kun lever i den ene retning.

---

## 4. Duplikerede e-faktura-felter på `Contract`, `Payer`, `MeteringPoint` — ✅ LØST i 0.8.0

**Status**: Refaktoreret i 0.8.0 (`plans/einvoice-identity-refactor.md`).

**Tidligere observation**: `ElectronicNumber`, `OrderIdentifier`, `AgreementIdentifier`,
`BuyerReferenceIdentifier` stod som 12 løse string-felter (4 felter × 3 entiteter)
uden at det fremgik at det var ét sammenhængende koncept.

**Løsning**: De fire felter er samlet i én `BuyerInvoicingReferences`-record der
udtrykker konceptet eksplicit: *"referencer som fakturamodtageren (køberen) har
bedt om at få anført på fakturaen, så modtagerens system kan håndtere fakturaen
korrekt: levere elektronisk, matche mod ordre/aftale, og rute internt."*

Samme nullable property `BuyerInvoicing` står nu på `Contract`, `Payer` og
`MeteringPoint`. Override-hierarkiet bliver selvforklarende via konsistent
navngivning: hvis sat på et niveau, gælder det; hvis null, arves fra parent.
Det er en almindelig pattern i fakturadomæner og kræver ingen ekstra resolver-
funktion.

**Tilbageværende lugt**: `OioUblConfiguration` har de samme tre identifiers under
andre navne (`OrderReference`, `BuyerReference`, `AgreementIdentifier` — sidstnævnte
overlapper navngivnings-mæssigt med `BuyerInvoicingReferences.AgreementIdentifier`).
Konsolidering af OioUbl-terminologien med `BuyerInvoicingReferences` er en separat
refactor — ikke håndteret i 0.8.0.

---

## 5. `SubscriptionFee` uden FK

**Observation**: Ingen `MeteringPointId` eller `ContractNumber` på typen.

**Rationale (DM)**: Bevidst orphan-design. Doc-kommentaren
(`SubscriptionFee.cs:13-15`): *"Conceptually attached to a metering point or
contract; the consumer queries them per `(MeteringPointId, Period)` or per
contract depending on use-case."* Caller leverer konteksten ved opslag.

**Modellen siger det ikke**: At linket er **eksternt** (caller-supplied) er kun
beskrevet i en doc-kommentar. Typen i sig selv er en frittstående bag-of-fields.

**Mulig forbedring**:
- Wrap i en kontekst-bærende type: `SubscriptionFeeAttachment(MeteringPointId, Period, SubscriptionFee)`.
- Eller eksponer kun gennem en service: `ISubscriptionFeeLookup.Get(meteringPointId, period)`.
- Begge gør linket eksplicit i type-systemet i stedet for konvention.

---

## 6. `OwnerCluster`

**Observation**: Lookup-tabel der peger på Customers via string-ids. Hverken
Customer eller Contract ved at de er del af en cluster.

**Rationale (DM)**: Pure read-time grouping. Bygges af `XellentDomainMapper.cs:85`
efter customers er loadet. Bruges til **rapportering og UI** (dashboards der
viser ejergrupperinger) — driver ikke mapper- eller Settl-skrivninger.
"Repræsentant" er det laveste accountnummer i clusteret, et stabilt
gruppe-anker.

**Modellen siger det ikke**: At dette er en visning, ikke en del af aggregat-grafen,
fremgår ikke. Placeret i `Domain.Customers` antyder den hører til Customer-aggregatet.

**Mulig forbedring**: Flyt til separat namespace (`Domain.Customers.Grouping` eller
`Domain.Reporting`) eller marker som `IReadOnlyView`. Alternativt: dokumentér
eksplicit i type-kommentaren at det er afledt og ikke en del af skrivemodellen.

---

## 7. `MeteringPointAssignment` — navngivning og scope

**Observation**: Navnet "Assignment" antyder bridge/relation. Men typen indeholder
domæne-data (`Product`, `AmrCode`) — ikke kun en kobling.

**Rationale (DM)**: Value-object inde i Contract (`Contract.cs:23`,
`MeteringPointMapper.cs:210-221`). Én pr. `ExuContractPartTable`-række. Indeholder
data der hører til **kombinationen contract+MP+periode**, ikke til MP'en selv:
- `Product`: samme fysiske MP kan have forskellige produkter over tid →
  product-historik tracket via contract parts, ikke på MP master.
- `AmrCode`: per-contract-part konfiguration.
- MP'ens egne attributer er commodity-statiske (Electricity/Water sat én gang).

**Modellen siger det ikke**: At `MeteringPointAssignment` er en **temporal slice**
af (Contract, MP) — ikke en bridge — fremgår ikke af navnet. Læseren forventer
at "Assignment" er strukturel kobling og bliver overrasket af at finde
forretningsdata på den.

**Mulig forbedring**: Omdøb til noget der signalerer den temporale rolle:
`ContractMeteringPointEntry`, `MeteringPointContractTerm`, eller (DDD-stil)
`ContractedMeteringPoint`. Eller doc-kommentar der forklarer at det er en
periode-baseret slice.

---

## Opsummering — modelproblemer i prioriteret rækkefølge

| # | Problem | Type | Forslag |
|---|---------|------|---------|
| ~~4~~ | ~~Override-hierarki for e-faktura-identifiers er usynligt~~ | ✅ **Løst i 0.8.0** | `BuyerInvoicingReferences` |
| 3 | Payer↔Contract retning + disjoint-partition er usynlig | Manglende invariant | Dictionary/computed-property på Customer |
| 7 | `MeteringPointAssignment` lyder som bridge, er slice | Misvisende navngivning | Omdøbning |
| 2 | `Contract.CustomerNumber` er mapper-mellemstation i source-agnostisk model | Forkert lag | Flyt til `Domain.Xellent` DTO |
| 6 | `OwnerCluster` er read-view, placeret som aggregat | Forkert lag | Eget namespace / view-marker |
| 1 | Bidirektional FK på JointInvoiceGroup, derived ikke markeret | Mindre — let at læse i mapper | Kommentar eller derived-marker |
| 5 | `SubscriptionFee` har eksternt link kun i doc-kommentar | Konvention-baseret | Wrap i kontekst-type |
| **NY** | `OioUblConfiguration` overlapper navngivnings-mæssigt med `BuyerInvoicingReferences` | Terminologisk lugt (afledt af #4) | Konsolidér terminologi |
