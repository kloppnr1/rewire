# settlement-config: vis hvem der ikke kan få et skema

## Kontekst

Eksporten siger `customersWithoutSchedule: 2` og ikke et ord om hvem. For at finde dem
skal man trække kundenumrene i `schedules[].customers` fra dem i `segments[].customers` —
og i en kørsel fra dashboardet er `customers` og `customerFacts` `null`, så segment-
grupperne er eneste komplette kundeliste at trække fra. Det er ikke noget nogen gør;
tallet bliver bare stående.

I filen fra 13-08 er de to **600053 Cobo IX Fiktivgaded 7 ApS** og **600055
Specialpædagogisk Mandskab ApS**, begge med ét målepunkt og begge i segmentet SME. De
optræder hverken i `unresolved` eller i nogen konfliktliste — der er ikke to kilder der
er uenige, der er ingen kilde. Det er en beslutning nogen skal træffe, og den kan ikke
træffes ud fra et tal.

Alle de andre "her er noget galt"-tilfælde i eksporten har allerede deres egen liste:
`scheduleConflicts`, `segmentConflicts`, `segmentUnresolved`, `unresolved`. Det her er
det eneste hul.

## Ændringer

**DataMigration**

- Ny liste i dokumentet ved siden af `scheduleConflicts`, i samme familie: én række per
  kunde med kundenummer, navn, segment, antal målepunkter og målepunkts-id'erne. Tallet
  i `totals` bliver stående — det er listens længde.
- Hver række siger **hvorfor**, for de tre grunde kræver tre forskellige handlinger:
  - kunden findes ikke i Xellent og har ingen settlement group — der er intet at gå efter,
    og nogen skal vælge et skema
  - kunden findes ikke i Xellent, har en gruppe, men gruppen navngiver intet skema —
    det er en manglende `groupSchedules`-post, altså en rettelse i config og ikke i data
  - kunden findes i Xellent, men ingen af hendes målepunkter har en afregningsplan
  Den midterste er den der kan lukkes uden at spørge nogen; den skal kunne ses som sådan.
- Log-linjen navngiver dem i stedet for kun at tælle: kundenummer og navn, de ti første,
  resten som "+N flere" — samme form som de øvrige lister i runneren.

**MigrationDashboard** (bagefter, når pakken er ude)

- Panelet nævner i dag `segmentUnresolved` med en linje der henviser til JSON'en. Samme
  slags linje for kunder uden skema, med de tre grunde talt hver for sig.

## Verifikation

1. `dotnet build` + unit tests. Ny test på grupperingen: en kunde uden plan og uden
   gruppe kommer i listen med den rigtige grund, en kunde uden plan hvis gruppe navngiver
   et skema kommer slet ikke i listen, og listens længde er lig `totals`-tallet.
2. `export-settlement-config` mod production (read-only) og sammenlign med filen fra
   13-08: listen skal indeholde præcis 600053 og 600055, begge med grunden "findes ikke i
   Xellent, ingen settlement group" — hvis de da stadig er de to; ellers skal forskellen
   kunne forklares.
3. Kør den samme udregning med `node` på den nye fil og bekræft at listen og
   set-differencen giver det samme. Er de uenige, er det listen der er forkert.
4. Dashboard: build, tests, `npm run build`.
