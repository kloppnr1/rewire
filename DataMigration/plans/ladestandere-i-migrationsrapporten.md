# Ladestandere synlige i migrationsrapporten

## Kontekst

Når en el-kunde migreres, lukkes el-kontrakten i Xellent — men ladestanderen ligger på sin
egen Charge-kontrakt og bliver stående. Ingen kan se det på forhånd, fordi ladestander og
el-målepunkt ikke er koblet nogen steder.

Koblingen findes implicit: el-kontrakten har en kontraktdel med produktet `V LadVariP`
eller `V LadVarUA`, og den del peger via sin aftale på ét E17-forbrugsmålepunkt.
Ladestanderen har ingen reference tilbage. Verificeret mod produktion 13-08-2026:
entydig for 3.861 konti, tvetydig for 115.

**Afgrænsning:** rapporten dækker de kunder der er ved at blive migreret, ikke dem der
allerede er. 452830 og 443210 — de to der udløste opgaven — er begge migreret, så deres
el-kontrakter er lukkede og de kommer aldrig med i en kørsel igen. Planen stopper bunken i
at vokse. At gøre op hvor stor den allerede er, er fravalgt.

## Ændringer

**XellentDomain (færdig, 0.8.40).** Ved en el-kørsel slås det op hvilke el-målepunkter der
forsyner en ladestander, og hvilke ladestander-dele der hænger på dem. Oplysningen ligger i
to opslag ved siden af målepunkterne i loader-resultatet — ét pr. målepunkt, ét pr. konto
for dem der ikke kunne kobles. Både `Charge` og `Charge_Abo` kan udtrykkes: hver del bærer
sin kategori, sit produkt og sin periode. Kun dele der løber på referencedatoen tages med.
Flere kandidat-målepunkter giver en advarsel og ingen kobling, aldrig et gæt.
`Commodity.EvCharging` er urørt og læser fortsat kun `Charge`.

**DataMigration (denne commit).** Rapportmodellen bærer de to opslag videre, som rent
display på linje med settlement-grupperne. Kunde-filtreringen skærer dem med — konto-
opslaget følger både repræsentanterne og deres merge-gruppemedlemmer, da ladestander-
kontrakten kan hænge under en medlemskonto.

**Dashboardet (mangler).** Et mærke på målepunkt-linjen som "samlefaktura" har det i dag,
en linje under målepunktet i samme form som fritagelse og e-fakturareference med
installationsnummer, adresse og de åbne dele fordelt på forbrug og abonnement, og en
sektion på konto-niveau med de ukoblede. Antallet rapporten ikke dækker står øverst. Det
skal med i sammenfoldningen på all-active-kørsler, ellers forsvinder det lydløst på præcis
de kørsler det er tiltænkt, og rapport-runtimen til den downloadede fil skal genbygges.

## Verifikation

Gennemført for de to første ben: build uden advarsler, unit tests i begge repos, gyldne
Xellent-integrationstests 7/7 uden baseline-drift, og en kørsel mod AXDB50 hvor konto
100406 giver målepunkt 571313114900379701 med installation 1086589, kontrakt 10562433,
"Fiktivgadec 90, 8920 Randers NV" og ét åbent abonnement `OAboLejMEL` fra 19-03-2025.
484622 og 493759 forbliver ukoblede med en advarsel der navngiver kandidaterne.

Antallet af ladestandere følger referencedatoen: 493759 har fem ved 01-01-2026 og tre ved
13-08-2026, fordi to abonnementer er lukket imellem. Repræsentant-merge ændrer intet.

`MigrationCli` skriver aldrig rapporten ud — den serialiseres ingen steder, og dashboardet
læser den in-process. Der findes derfor ingen CLI-vej til at se det her, og dashboard-benet
er ikke kosmetik.

Dashboardets visning kan ikke bekræftes uden en skærm. Build, tests og genbygget
rapport-runtime dækker resten; det visuelle meldes som et hul.
