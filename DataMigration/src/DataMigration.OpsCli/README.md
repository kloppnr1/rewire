# DataMigration.OpsCli

Read-only ops CLI for migration reports, comparisons, and seed/state-data generation. Wraps Xellent extraction and Settl API queries behind profile-driven commands. Mutations sker via `DataMigration.Migration` (`create`); OpsCli skriver ikke længere til Settl.

## Payment-match rules

For reports supplied with a list of MP IDs (`payment-match-mp`), see the [complete MobilePay and PBS rules](../../docs/payment-match-mp.md). They cover private and business agreement selection, cross-commodity searches, the water/heat business error, matching to Settl E-numbers and report fields.

## Commands

| Command | Purpose |
|---------|---------|
| `report <profile>` | Generate HTML migration report from Xellent extraction |
| `report-meteringpoints` | Read-only scan: check currency, EAN, joint, grid, type, subtype, occurrence, EAV og meters for alle MPs i Settl. Skriver CSV til disk. Scanner kun målepunkter **i levering** (parent-MPs) — market-v3-udvælgelse som `reconcile`; `--include-not-in-delivery` tager resten med. |
| `report-payers` | Read-only scan: verify at alle payers i Settl har valid customer reference. Skriver CSV til disk. |
| `report-meter-info <profile>\|--accounts\|--scan` | Read-only TSV report af EstimatedAnnualVolume, type, meters og MeterReadingOccurrence — Settl vs Xellent. El-ekstern only. `--scan` bruger samme in-delivery-udvælgelse som `report-meteringpoints`. |
| `report-delivery-start` | Read-only TSV report: MeteringPointIdentification + DeliveryStatus.Start for alle MPs i Settl |
| `compare <profile>` | Compare extracted Xellent customers against Settl API |
| `payment-match <profile> <csv> --type pbs\|mb` | Match Xellent agreements to the Settl payment export by metering point or joint invoice. PBS produces HTML, CSV and Excel; MobilePay produces handover Excel. In this CLI, PBS can inherit only from the explicitly referenced main contract on the same account when the owning contract has code 0; the main contract may belong to another commodity. The MP-list report (`payment-match-mp`) has separate private/business selection rules; those rules do not change this command. |
| `payment-match-mp <mp-list> <csv> --type pbs\|mb --cutover <yyyy-MM-dd>` | Match an explicit list of 18-digit metering point IDs against the Settl payment export. Ownership and agreements are read in Xellent on the day before cutover. Writes report, coverage and agreements CSVs (Excel BOM) to `--output` (default `./output`). Read-only; never calls Settl. `PaymentMatch:PbsWaterHeatRuleExcludedMeteringPoints` in appsettings lists the MPs allowed a PBS agreement from water/heat. [Rules](../../docs/payment-match-mp.md). |
| `generate-seed <accounts>` | Generate seed data for regression tests fra Xellent DB og Settl API |
| `generate-state <profiles> --profiles-dir <dir> --reference-date <date>` | Generate `migration-state.json` (tracking + invoices) for én eller flere profiler |
| `pull-state-sources --env <environment> [--only all|invoices|contracts|xellent|settl] [--reference-date <yyyy-MM-dd>] [--output-dir <dir>]` | Save invoice and contract sources in one offline snapshot. `--reference-date` is required whenever Xellent contract data is pulled (`all`, `contracts`, or `xellent`). `--only xellent` or `--only settl` refreshes just that contract source without invoices. |
| `report-contract-impact --input-dir <dir> [--env production] [--product "V AboRabat"] [--output <new.csv>]` | Read contract sections of a completed offline snapshot and count Xellent MPs with the product and MPs linked to a Settl contract of that name. Does not contact either system. |
| `build-migration-model --report-dir <dir> --output-dir <new-dir>` | Read migration reports once and build a versioned reusable model of completed non-dry-run runs, covering all reported MPs. |
| `generate-state-from-settl` | Generate invoice state using each electricity point's first confirmed ChangeOfSupplier start, its dated Settl customer, and the Xellent contract ending at COS−1. `--include-unknown-delivery-starts` additionally allows a dated first start whose reason is `Unknown`; customer and Xellent ownership checks still apply. A point with no payer link at COS is looked up through its customer link at COS, then through one current payer lookup (both skipped and logged with `--offline`). The Xellent account or its migration representative must match the customer number after production padding. If neither does, the customer number is still accepted when it is a Xellent account with the source account's non-blank CVR (never the placeholder 11111111, never a source account that its own Settl customer claims; skipped and logged with `--offline`), listed in `<state>-same-cvr-accepted.csv`. Direct and same-CVR matches keep only the source account; representative matches include verified group members, except members that their own Settl customer claims. An account claimed by several customers that all carry its real CVR goes to the one its representative at that customer's COS−1 points to, then the earliest COS, then the lowest customer number, listed in `<state>-ownership-tie-breaks.csv` (skipped and logged with `--offline`); other multiple claims remove the account. A rejected point drops only its own claim: the source account stays when another valid claim on it remains. `--include-active` retains invoices from accounts still being billed. `--no-invoices` writes source accounts without invoice lists. `--offline` uses locally captured sources and requires no directory argument. `--reference-date` is accepted for older scripts but ignored. `--handled-issues <path>` reads a semicolon CSV `Reason;SourceAccount;MeteringPoint;SettlCustomers;Note;HandledDate` of issues already handled (default `C:\Services\DataMigration\data\handled-ownership-issues.csv` when it exists; the log names the file used). An issue whose first four columns all match exactly goes to `<state>-handled-issues.csv` with its note instead of the ownership-issues CSV; it is still omitted from the state as before. The log counts issues still handled, new issues and handled rows no longer seen; the last are listed in `<state>-handled-no-longer-seen.csv` so the list can be cleaned up. A missing or malformed list stops the run. `--settlement-check` also writes `<state>-settlement-check.csv` (`SettlCustomer;SourceAccount;MeteringPoint;ContractNumber;ContractPartId;Cos;LatestStatementDate;Status`), one row per metering point in the state. It takes the latest `EXU_LATESTBILLINGDATES.LATESTSTATEMENTDATE` billed for the contract the COS−1 resolver pinned and the point's agreement (company `for` or blank): `Settled` when it is COS−1, `NotSettled` when it is earlier or later (the date is shown either way), `Missing` when there are no billing rows. `ContractPartId` lists the contract's parts in force on COS−1, joined with `|`, for reference. The log counts each status. It reads Xellent in chunks of 500 points, is read-only, leaves the state unchanged, and cannot be combined with `--offline`. |
| `export-active-customers --env <env> [--deliver-to <dir> [--keep 14]] [--payment-agreements <csv>] [--raw] [--pseudonymise]` | Read-only JSON export of every Settl customer linked today to an electricity metering point in delivery, in the shape of the approved customer examples (`--raw`: the Settl responses whole). Carries customer, payers, metering points in delivery (with market price lists, billing and settlement groups), full contracts with lines, customer price lists, extra invoice lines and, with `--payment-agreements`, the rows of the Settl 'Payment Agreement' CSV. Links to those metering points are kept unless ended. `--deliver-to` writes a share delivery instead of one file: `<dir>/<env>/<yyyy-MM-ddTHHmmssZ>/` with `customers.jsonl`, `delta.jsonl` (added, changed, removed against the last published run), `manifest.json` and, last, `_READY`; a failed run is kept as `.failed` without `_READY`, a payment CSV older than 24 hours is refused, and a `.lock` stops overlapping runs. With `--limit` or `--customer` the run is a trial in `<dir>/<env>-trial`. [Runbook](../../docs/settl-export-runbook.md), [plan](../../plans/settl-export-sonwin.md). |
| `check-share-delivery --env <env> --deliver-to <dir> [--trial] [--run <name>] [--max-age-hours 26]` | Read-only check of the newest run delivered by `export-active-customers --deliver-to`: files and checksums against the manifest, line counts, unique customer numbers, no read errors, 18-digit metering point ids, and the run's age. Prints a summary with ids and counts only; exit 0 = OK. Normally run through `settl-export.ps1` ([runbook](../../docs/settl-export-runbook.md)). |
| `export-eav-excel --account\|--metering-points\|--profile --output <file.xlsx>` | Generate Settl bulk-import Excel (Type=MeteringPointMeter, Version=8) for backfilling EstimatedAnnualVolume on already-migrated el-ekstern MPs. Read-only — user uploads xlsx via Settl bulk-import UI. |
| `export-missing-eav-excel --output <file.xlsx> [--value <kWh>] [--metering-points ...] [--offline <snapshot.json> [--snapshot-max-age-hours 24]]` | Generate Settl bulk-import Excel (Type=MeteringPointMeter, Version=8) that gives every active el MP that was not part of the migration an EstimatedAnnualVolume (default 2000 kWh). Active = parent MP that Settl's market list shows in delivery. Part of the migration: Settl's first delivery start (the first `InDelivery=true` period, whatever reason Settl records on it: ChangeOfSupplier, Unknown or other) is day D, and Xellent has an El-ekstern contract for the MP, in any Xellent company, running on D−1 (not necessarily ending then: the 2026-08-31 batch restamped some ends; `TimeSeriesRunner` picks the migrated account the same way); those are left out and counted. Without such a contract the MP gets rows, whatever the start reason. An MP with no `InDelivery=true` start is left out, listed in the output, by hand. `<file>-decisions.csv` next to the workbook gives, per active MP, the first delivery start and its reason, whether Xellent has a contract running the day before, and the result. Every live run also saves what it read from Settl as `<file>-settl-snapshot.json`; `--offline <that file>` repeats the run from it without reading Settl (Xellent is still read live, so a changed rule applies to the same Settl data). An offline run refuses a snapshot from another `--env` or older than `--snapshot-max-age-hours` (default 24), because an Update row replaces the meter version in Settl and must not come from old data. Reads Settl and Xellent (read-only); Xellent needs the VPN. Each MeterCharacteristics version from the one in force at the current delivery start onward that has no volume gets its own `Update` row at its own date, so one MP can get several rows. An Update row replaces the version (empty columns blank it, production 2026-11-11), so each row repeats MeterReadingOccurrence, series name and the meter's DigitsOfPrecision, Decimals, MultiplicationFactor and Ampere. A version with no meter, several meters or no Hourly/QuarterHourly reading leaves the MP out (listed in the output, by hand). Any Settl or Xellent read error writes no file. Read-only — user uploads xlsx via Settl bulk-import UI. |
| `export-exemption-excel --account\|--metering-points\|--profile --output <file.xlsx>` | Generate Settl bulk-import Excel (Type=MeteringPoint, Version=16) for backfilling 100% billing exemptions on already-migrated MPs whose active contract template ends with `--template-suffix` (default `_Erh_UG`). `--product-element-code` defaults to `Fakturagebyr`. Run separately per `--delivery-category` (default `El-ekstern`). Read-only — user uploads xlsx via Settl bulk-import UI. |
| `export-settlement-config --output <file.json> [--limit N]` | Read-only post-migration JSON-udtræk: Settl-kundenumre grupperet efter **Settlement Schedule** og **Customer Segment**. Population og Xellent-join følger `reconcile`: market-v3-paginering der beholder parent-MPs i levering, derefter `MeteringPointAccountResolver` som pinner hver MP til den kontrakt der sluttede ved `COS-1` (den migrerede kontrakt). Schedule læses fra `Billingplannum`; segmentet tager udgangspunkt i `Contracttemplatenum` og kundens identifikation, og for ladekunder i den tilknyttede Settl-kontrakt. Navnene oversættes via den faste mapping-fil `settlement-config.json` (sektionerne `schedules` + `segments`; ligger i OpsCli-projektet, kopieres til output). Koder uden entry beholder deres rå værdi (`mapped:false`) og listes i stderr-summary + `unmappedBillingPlanNums`/`unmappedContractTemplates`. Kundenumre er repræsentanter (= Settl `CustomerNumber`). Ikke profil-baseret. Segment er kunde-niveau. Private ladekunder får Eje/Leje fra deres tilknyttede Settl-ladekontrakt eller, hvis den mangler, fra Xellents Charge_Abo-produkt på COS-1. Ladesegmentet gælder også når de har andre målepunkter. Uden entydigt ejerskab står `V_LadePriv` i `segmentConflicts[]` med MP-liste. MPs uden skabelon listes i `segmentUnresolved[]`. |
| `fix-settlement-groups --profile <name> [--account <list>] [--metering-points <list>] [--dry-run]` | **Mutation**: tilføjer allerede-migrerede MPs til **eksisterende** Settl Settlement Groups baseret på `profile.Mappings.SettlementGroup` (BillingPlanNum → GroupName). Opretter ikke grupper — fejler hvis mål-gruppe mangler. Kræver `Extraction.ChangeOfSupplierDate` på profilen. Idempotent: springer MPs over som allerede er medlem eller ikke er migreret endnu. Brug `--dry-run` for read-only preview. |
| `fix-payment-config --profile <name> [--account <list>] [--metering-points <list>] [--skip-payer-phase] [--skip-mp-phase] [--dry-run]` | **Mutation**: backfilder PaymentConfiguration på **Payer-niveau** (OrderIdentifier/AgreementIdentifier/BuyerReferenceIdentifier) via raw-JSON round-trip mod `PUT /api/v1/Payers/{id}` (full-replacement, alle ikke-modificerede felter roundtrippes verbatim), og **MP-niveau overrides** (ElectronicNumber + samme 3) via typed `UpdateBilling`. Bruges til at backfille kunder migreret før commit `ea55399b`. Kræver `Extraction.ChangeOfSupplierDate`. El-ekstern only. Idempotent: skipper hvor Settl allerede matcher Xellent; logger Mismatch hvor Settl har en anden ikke-blank værdi. |

Most commands accept `--env <environment>` (`featuretest`, `production`, `production-hev`, `production-vhel`). Default: `featuretest`.

For invoice enrichment, the first historical delivery start must explicitly be ChangeOfSupplier by default. `--include-unknown-delivery-starts` also accepts a dated first start with reason `Unknown`; it never accepts `MoveIn`, conflicting reasons, failed lookups or a missing first start. Only points with a FOR or legacy blank-company Xellent electricity delpoint are in scope for FOR invoices; Settl-native and MEH-only points cannot supply FOR Xellent invoices and are excluded before payer lookup. The delivery-start audit lists these as `NoForXellentOrigin`. The payer link's CustomerId is resolved through Settl's customer list; its PayerIdentifier may contain a suffix or be five digits and is not used as the customer number. A missing or ambiguous customer link on an in-scope point, Xellent contract, or account match stops the run by default and is reported. With `--skip-unresolved-source-accounts`, the point and every Xellent account that could carry its invoices are omitted; a source account shared with a valid point is omitted from that point too. Past occupants of the same metering point are never added as invoice sources merely because they used that point. `--flat-reference-date` and `--include-not-in-delivery-cos` are rejected because they cannot establish this ownership. `pull-state-sources` saves source lookups independently of ownership and invoice rules. `generate-state-from-settl --offline` then rechecks the first-start, customer, account and invoice decisions from these files without Settl or Xellent connections. Pass `--include-active`, `--skip-active`, `--skip-unresolved-source-accounts`, `--no-invoices` and the history window options on each offline run as needed. The default pull includes invoice rows, so both invoice and `--no-invoices` state runs can use it. See [invoice-state-comparison](../../docs/invoice-state-comparison.md).

The pull writes JSON Lines sections and a checksum manifest to `data/generate-state-from-settl-offline/<env>/` beside the deployed OpsCli directory. For example, `C:\Services\DataMigration\OpsCli\DataMigration.OpsCli.exe` uses `C:\Services\DataMigration\data\generate-state-from-settl-offline\production\`. Each finished section is kept on disk if the pull stops early; the manifest then marks the set incomplete, so offline generation requires another pull. A failed state run never changes the saved source files. Reports remain beside the requested output file. To use `--include-unknown-delivery-starts --offline`, run `pull-state-sources` once with this version: older snapshots did not capture Xellent candidates for unknown starts.

In COS-driven mode, the command checks the first delivery start before assigning a customer. `MoveIn` and missing, conflicting, or unavailable start reasons are excluded in both invoice and `--no-invoices` runs. An `Unknown` reason is included only with `--include-unknown-delivery-starts`; the run log counts these points. The `*-delivery-start-audit.csv` lists excluded points and, when the flag is used, accepted unknown starts as `IncludedUnknownDeliveryStartReason`. Invoice mode places each invoice through its Xellent contract's parent metering point and the Settl customer at that MP's COS date.

## Contract impact from offline sources

`pull-state-sources` writes a checksum manifest and JSONL sections. Choose `--only`
by what you need next:

| `--only` value | Use it for | Data captured |
|---|---|---|
| `contracts` | Contract repair or impact analysis | Xellent El parts/logs, `Charge_Abo` parts and charger assignments for all `Charge_Abo` accounts; Settl contract links; no invoices |
| `xellent` | Refresh Xellent contract evidence in a snapshot that already has Settl contract links | Xellent contract and charger sections for all `Charge_Abo` accounts only |
| `settl` | Refresh Settl contract links in an existing snapshot | Settl contract links only |
| `invoices` | Invoice-state work without refreshing contract sections | Invoice-state source sections |
| `all` (default) | Both invoice-state and contract work | Invoice-state sections and both contract sources |

`--reference-date` is required whenever using `contracts`, `xellent`, or `all`,
including refreshes of an existing snapshot. The Xellent
mapper uses it to select historical contract and charger parts; postal code and
address then determine the physical charger-to-MP match. The date does not itself
decide which MP an installation matches. The pull captures charger assignments for every
account with a `Charge_Abo` part, regardless of electricity product; offline commands
choose which MPs to review. Account-only installations have a null MP. The fixed
31 August log restoration window and address matching follow the migration
mapper. A refresh preserves the other sections already in the same directory.
Settl collection uses one contract-list operation and one link operation per
electricity contract; the client may paginate.
Existing snapshots containing the four contract sections remain valid input for
the offline contract-impact report.

For a new AboRabat snapshot, use `--only contracts` where both systems are reachable, then
copy that directory for offline analysis:

```powershell
dotnet run --project src/DataMigration.OpsCli -- pull-state-sources --env production --only contracts --reference-date 2026-08-31 --output-dir "C:\path\to\snapshot"
dotnet run --project src/DataMigration.OpsCli -- report-contract-impact --env production --input-dir "C:\path\to\snapshot" --product "V AboRabat" --output "C:\path\to\impact.csv"
```

To add the charger assignment section to an existing snapshot, rerun only the
Xellent side with `--only xellent`. Copy the updated directory locally before
running the offline impact report. The CSV separates linked charger subscriptions
from account-only candidates. This makes no Settl calls and does not pull invoices:

```powershell
dotnet run --project src/DataMigration.OpsCli -- pull-state-sources --env production --only xellent --reference-date 2026-08-31 --output-dir "C:\path\to\snapshot"
```

Older snapshots remain readable; their impact CSV says `ChargerEvidenceCaptured=No`
until the Xellent section is refreshed.

The Xellent count includes historical live and logged product rows. The Settl count
is the number of distinct MPs returned as linked to a contract named `V AboRabat`.

## Profiles

Profiles live in `src/profiles/<name>.json` and define extraction parameters + account mappings:

```json
{
  "name": "match",
  "extraction": {
    "dataAreaId": "hol",
    "deliveryCategory": "El-ekstern",
    "groupOwners": { ... },
    "accountOwners": { ... }
  },
  "accounts": ["403522", "446977"],
  "mappings": {
    "groupNum": { ... },
    "accountNum": { ... },
    "gridArea": { ... }
  }
}
```

Override profile by passing `--accounts <list>` for commands that support it (`report-meter-info`).

## Configuration

`appsettings.json` + `appsettings.local.json`:

```json
{
  "ConnectionStrings": {
    "XellentDatabase": "Server=xel2012;Database=AXDB50_TEST;Integrated Security=True;TrustServerCertificate=True;"
  },
  "Infrastructure": {
    "ProfilesDirectory": "../profiles"
  },
  "SettlApi": {
    "BaseUrl": "...",
    "Credentials": { "ClientId": "...", "ClientSecret": "..." },
    "Environments": {
      "featuretest": { "BaseUrl": "..." },
      "production": { "BaseUrl": "..." }
    }
  }
}
```

Credentials go in `appsettings.local.json` (gitignored) or user secrets.

## Mutations

Næsten alle kommandoer er read-only. Tidligere `fix-*` kommandoer (`fix-billing`, `fix-grid`, `fix-joint`, `fix-electronic-number`, `fix-payer`, `fix-orphan-payer`) er erstattet af `report-meteringpoints` + `report-payers` — disse rapporterer drift uden at rette. Mutations til kunder/payers/MPs skal nu køres via `create`-pipelinen i `DataMigration.Migration`.

**Eneste mutation-kommando i OpsCli:** `fix-settlement-groups` — tilføjer MPs til allerede-eksisterende Settlement Groups (Settlement Group-trinet ramte `create` efter at de første kunder var migreret, så denne runner findes til catch-up). Som `create` muterer den default; brug `--dry-run` for read-only preview. Verificér altid mod featuretest først.

## Examples

```bash
# Generate HTML report for a profile
dotnet run -- report match

# Compare Xellent vs Settl for a profile
dotnet run -- compare match --env production --verbose

# Read-only scan af alle MPs i Settl (CSV-output)
dotnet run -- report-meteringpoints --env production

# Read-only scan af payers (CSV-output)
dotnet run -- report-payers --env production

# Generate seed data for regression tests
dotnet run -- generate-seed 403522 446977 --output ./seed

# Invoice state with source accounts and invoice history — dates per customer's own COS
dotnet run -- generate-state-from-settl --env production --skip-active --output ./settl-state-production.json

# Save Settl and Xellent source inputs once, then generate repeatedly offline
dotnet run -- pull-state-sources --env production --reference-date 2026-08-31
dotnet run -- generate-state-from-settl --env production --offline \
  --include-active --skip-unresolved-source-accounts --output ./settl-state-offline.json

# Opt in to dated first starts labelled Unknown (requires a new pull with this version)
dotnet run -- generate-state-from-settl --env production --offline \
  --include-unknown-delivery-starts --include-active \
  --skip-unresolved-source-accounts --output ./settl-state-with-unknown.json

# Identity only, for Bifrost: source accounts but no invoice history
dotnet run -- generate-state-from-settl --env production \
  --no-invoices --output ./migration-state.json

# PBS contract matching
dotnet run -- payment-match match payment-agreements.csv --type pbs --env production

# MobilePay handover Excel
dotnet run -- payment-match match payment-agreements.csv --type mb --env production

# Settlement Groups: dry-run (read-only) — viser hvad der ville blive tilføjet
dotnet run -- fix-settlement-groups --profile match --env featuretest --dry-run

# Settlement Groups: hot-run mod featuretest (faktisk mutation)
dotnet run -- fix-settlement-groups --profile match --env featuretest
```

## Dependencies

- `DataMigration.Core` — shared constants and helpers
- `DataMigration.XellentData` — EF Core access to Xellent DB
- `DataMigration.Migration` — `SettlExtractionService` (extraction + validation)
- `DataMigration.SettlApi` — Settl REST API clients

## See also

- Root [README.md](../../README.md)
- [CHANGELOG.md](../../CHANGELOG.md) — per-command behavioral history for `create` og report/state-runners
- [docs/settl-update-map.md](../../docs/settl-update-map.md) — Xellent↔Settl↔Repo lineage table

### V AboRabat: Settl import workbook

Create the Settl Excel import file from the completed production snapshot. This command reads local files only (contract parts, logs, charger assignments and Settl contract links); it does not read invoices or call Xellent/Settl. It identifies supported repairs and leaves unresolved MPs out of the workbook.

```powershell
dotnet run --project src/DataMigration.OpsCli -- prepare-aborabat-repair `
  --env production `
  --input-dir "C:\Users\sen.makj\OneDrive - Verdo Holding A S\Skrivebord\state" `
  --output-xlsx "v-aborabat-repair-import.xlsx" `
  --verification-csv "v-aborabat-verification.csv" `
  --review-csv "v-aborabat-review.csv"
```

The command includes MPs with a relevant Xellent V AboRabat part active on the reference date or starting later and a captured Settl V AboRabat link. If the Xellent discount ended earlier but the Settl V AboRabat link still covers the reference date, the workbook contains a Delete for that stale link and no replacement Create; the review files show the Xellent end and Settl link as evidence. If both ended before the reference date, the MP is out of scope. Among in-scope MPs, the command automatically includes every supported correction and excludes MPs with insufficient or conflicting evidence. No per-MP approval file is needed. Review the whole import workbook against the separate verification CSV before importing it into Settl. The verification CSV keeps the same 12 fields as US 45869, with Danish headings: `Målepunkt; Xellent-kundenummer; Xellent-kontraktnummer; Importfil; Excel-række; Excel-kolonne; Excel-værdi; Kildesystem; Kildepost og felt; Kildeværdi; Forklaring; Resultat`. It points to actual workbook rows and columns, captured Xellent parts and logs, charger assignment evidence, and existing Settl links. It also lists retained links, a resulting timeline, and unresolved MPs without import-cell references. Keep the part-level `--review-csv` if you want the original effective-part view.

The workbook contains UA periods while V AboRabat applies and ordinary contract periods outside the discount, supported by the El product and charger subscription. When V LadVariP continues before a known charger subscription starts or after one ends, the period without an active charger uses V Variabel (V Variabel UA while AboRabat still applies). A later supported charger subscription starts the corresponding charger contract. The charger restoration uses Xellent's separate 4 September 2026 termination window (12:53:27 through 13:35:43, LOGEMPLID Xellent). A deleted future Charge_Abo part remains relevant; its first matching log in that window supplies its previous end date. The import omits contract periods that ended before the reference date; a period still active on that date keeps its true Xellent start date. If Xellent has no matching Charge_Abo part or charger assignment for the electricity period and the captured Settl link is LadeAbo_Afventer_Installation, use LadeAbo_Afventer_Installation_ElUA during the discount and LadeAbo_Afventer_Installation afterward. If electricity starts before that pending link, use V Variabel for the earlier period (V Variabel UA if the discount already applies). Without that Settl evidence, the MP stays unresolved. Products that already specify UA retain the UA variant. Xellent determines these periods; an existing Settl charger link cannot justify continuing a charger contract after its subscription ends. Existing links with exactly the right dates are kept. Incorrectly dated links are replaced using Delete + Create; valid earlier periods are preserved. Rows are sorted by MP identification, then Delete before Create, then Create start date. Delete rows identify the link by AssociationId and also include ContractName and MeteringPointIdentification for readability. The V2 template documents Create and Delete; it does not document processing order, so the single-workbook workflow depends on Settl processing each MP's deletions before its creations. Conflicting periods are excluded as Unresolved. Import dates are native Excel dates displayed as `yyyy-mm-dd`, matching the customer/payer import files; the importer rejected the template's documented `dd-mm-yyyy` display. The verification CSV's Excel-value column uses the same ISO dates. Open-ended Create rows leave End blank. No Settl changes are made by generating these files.

Older offline snapshots do not contain Settl link `AssociationId` values and cannot produce a valid workbook. With the updated OpsCli in production, refresh just the Settl section using `DataMigration.OpsCli.exe pull-state-sources --env production --only settl --output-dir "C:\Services\DataMigration\data\generate-state-from-settl-offline\production"`. Copy the refreshed snapshot locally, then run `prepare-aborabat-repair` again with a new workbook path. The prepare command stops without writing a workbook if any selected old link lacks its `AssociationId`.

The charger assignment export now includes the raw Xellent charger address and DAR id plus each candidate electricity delivery-point address and DAR id. To refresh an older snapshot, run `DataMigration.OpsCli.exe pull-state-sources --env production --only xellent --reference-date 2026-08-31 --output-dir "C:\Services\DataMigration\data\generate-state-from-settl-offline\production"` on the production host and copy the updated snapshot locally. The review CSV shows both Xellent addresses and the address comparison. If the source values are missing, different, or ambiguous, the MP remains unresolved; the Settl address is not used to decide the match.

The optional `--review-csv` covers every affected MP and writes one Danish review row per effective Xellent contract part. The `Status` column is `Afklaret` for MPs included in the workbook and `Uafklaret` for MPs excluded from it. Contract creation uses parts belonging to the correct customer with a `V AboRabat` part active on the reference date or starting later, after restoring previous end dates. For a Delete-only correction, an ended V AboRabat part is shown as evidence that the Settl discount link is stale. Other customers' parts do not determine the correction. El parts and linked `Charge_Abo` parts show their effective end date in `Slutdato`. Superseded source values and supporting end-date log rows are omitted. Deleted parts restored from the migration window still have their own row. End-date-change logs remain evidence for the calculation. `Xellent-grundlag for slutdato` identifies the current part or exact log record (record ID, timestamp, change and recorded end) used for each row. `Grundlag for kontraktvalg` explains electricity-only continuation using the charger end, continuing El product and the uncovered charger period. These columns retain one row per effective part. The `Settl-kontrakter og perioder` column shows UA and ordinary contracts with their periods; charger address match repeats on each row; the reason for exclusion is blank on resolved MPs. Agreement and charger installation numbers are omitted. If any overlapping product or charger period cannot be mapped, the whole MP is excluded from the import. The reference date also selects the captured physical charger-to-MP match; `--reference-date <yyyy-MM-dd>` selects a captured date when several exist.

Migration restoration follows XellentDomain's bounded window: 31 August 2026 from 09:13:49 inclusive to 09:59:33 exclusive, with a blank log user. The earliest log row per part (timestamp, then record ID) determines the restoration. This window stays fixed when the reference date changes. Only deletions from that window are restored; later deletions are not labelled as migration deletions. Ordinary end-date logs can still supply the previous end date of a current part at the reference date. Start-date changes inside the window require review because the part identity has changed.

Older Xellent offline sections do not contain the complete part identity, log row order or log user needed to match end-date changes safely. Refresh only Xellent on the production host with `DataMigration.OpsCli.exe pull-state-sources --env production --only xellent --reference-date 2026-08-31 --output-dir "C:\Services\DataMigration\data\generate-state-from-settl-offline\production"`, copy the updated snapshot locally, and prepare new output files. The command refuses the old snapshot and version 1–6 API plans.

The JSON plan is only needed if you choose the separate API repair workflow. Add `--output "v-aborabat-repair-plan.json"` to the prepare command to save one. The API command below reads that file; Excel import does not. Older version 1–6 plans must be prepared again.

Plans containing replacements, ordinary contracts or only already-correct targets must use the Excel import. The API command rejects these plans before making calls.

Run this next command **only in the authorized production environment** with the production Settl configuration. It checks the old association ID, adds and confirms every planned UA link, then removes the old `V AboRabat` link. It writes a result row after each MP. A rerun needs a new report path and safely completes MPs where some UA links were added but the old link was not removed. Changed or ambiguous links are skipped.

```powershell
DataMigration.OpsCli.exe apply-aborabat-repair `
  --env production `
  --plan "C:\path\to\v-aborabat-repair-plan.json" `
  --reviewed-xlsx "C:\path\to\v-aborabat-repair-import.xlsx" `
  --report "C:\path\to\v-aborabat-repair-results.csv" `
  --execute
```

Keep the generated Excel import local; it contains metering point identifiers. If using the API workflow, keep its JSON plan and result CSV local too.


## Offline customer and payer Excel repair

`pull-state-sources --scope-file targets.txt` scopes the persisted snapshot to the listed
Xellent customer accounts (1–6 digits) and/or metering point identifications (18 digits).
Use one identifier per line; blank lines and duplicates are ignored. Invalid or unresolved
entries are rejected with their line numbers. Customer entries include their electricity
points. Point entries include their source/representative/payer dependencies, without
adding sibling points to the repair target. No scope file keeps the full capture behavior.
Scoped captures require an explicit output directory. A refresh requires the same scope
file and reference date; changes in resolved scope require a new directory.

`--include-sensitive-data` adds original Xellent customer, address/contact, payer and
contract source rows, plus independent Settl inventories and association evidence. Existing
snapshot sections already contain some personal data. This opt-in governs additional
details, and an existing sensitive snapshot must retain the flag on refresh. Checksum and
completion validation apply to the repair sections. Failed capture is marked incomplete.
A source-only refresh invalidates and replaces only that source's repair evidence.

Repair capture copies source values unchanged. It does not validate email/address values,
restore periods, choose contracts or save validation findings. These steps run later in
`prepare-customer-payer-repair`, using an isolated in-memory copy of the saved rows with
no Xellent or Settl connections. Invalid values do not prevent a successful source copy;
failed reads, incomplete responses and failed writes still do.
The `repair-raw-settings` section identifies the new format. Older completed snapshots
with mapped `repair-xellent` data remain readable, but do not preserve rejected raw values.

Settl customer/payer inventories are scanned globally to detect unexpected owners and
standalone payers, but scoped captures persist only dependencies and target associations.
Association reads run with bounded concurrency and count-only progress. If an association
endpoint returns its 1,000-row cap, capture refuses to claim complete conflict evidence.
This scan can be substantial even for a short target list. SQL contract reads use bounded
scope batches. Offline preparation reuses the source mapper's contract-to-payer rules,
with representative merging disabled for this repair. Ambiguous routes are not replaced
with guessed default payers.

For the 35-point investigation, run this only on an authorized host with source access:

```powershell
DataMigration.OpsCli.exe pull-state-sources --env production --only contracts `
  --reference-date 2026-08-31 --scope-file missing-35.txt --include-sensitive-data `
  --output-dir C:\repair\customer-payer-snapshot
```

The normal capture does not use restore-window flags. Supply
`--restore-parts-changed-from` and `--restore-parts-changed-before` only when evidence
shows that historical contract reconstruction is necessary; never add them by default.
These options affect mapping only, never the source database. Copy the resulting
snapshot and use the five unmodified supplied Customers V4, Payers V6,
CustomerMeteringPoints V4, PayerMeteringPoints V3 and ContractMeteringPoints V2 templates locally:

```powershell
DataMigration.OpsCli.exe build-migration-model `
  --report-dir C:\repair\all-active-runs --output-dir C:\repair\migration-model

DataMigration.OpsCli.exe prepare-customer-payer-repair --env production `
  --input-dir C:\repair\customer-payer-snapshot --scope-file missing-35.txt `
  --migration-model C:\repair\migration-model\migration-model.json `
  --template-dir C:\repair\templates --output-dir C:\repair\imports
```

The version-2 model determines expected accounts, customer/payer records, relationships,
contract names and dates. Customer and payer version dates come from the report's
`AddressStart` (both migration translators use the customer's earliest contract start).
Customer/payer MP-link dates come from `IdentificationStart`. Contract dates come from
each reported contract window. Supplier-change dates never replace these dates.
The model preserves per-MP observations, report filenames, run dates, rules and charger evidence. Only reports with
explicit `Partial: false`, `Result.IsDryRun: false` and `Report.DryRun: false` are included.
A completed run may contain failed actions: success flags and action counts remain in
the model and do not prove individual links exist. Dry/unfinished reports are excluded;
missing or contradictory flags are listed in the manifest. Unreadable reports stop the build.
Original reports are streamed and checksummed in the same read. The model and completion
manifest are published only after all reports have been read. Output folders must be empty.
The `run-details` folder retains compressed complete copies of eligible reports with
checksums. This preserves all source fields and failed-action details for future consumers.
Keep the entire model folder together; the index alone is not the complete model.
Version-1 models do not retain enough relationship data and require a one-time rebuild.

Later steps require `--migration-model` and never reopen the report folder. Identical
observations share supporting reports; different accounts, customer/payer records, dates or periods are held
for review without choosing the newest run. Xellent's offline mapping checks the migration
result; it does not replace it. Missing model data and differences remain unresolved.
The model version and checksum are saved with the preparation result. Rebuild explicitly
into a new output folder when source reports change. No production connections are needed.

Preparation requires no database credentials or network and writes a report accounting for
every selected point and a separate `verification.csv` with source evidence, actions, periods and released workbook cell references. Only a successful report with `Ready: true` releases the five import
workbooks. Output directories must be empty. Existing entities are reused; only confirmed
missing entities/associations receive `Create` rows. All import dates are the migration
report dates. Settl is checked for existing coverage; Xellent is supporting comparison data.
Delivery history does not select accounts or replace migration dates.
Unknown dates, incomplete evidence, missing required fields, unsupported template billing
channels, conflicting or partially covered associations and legacy unpadded
identifier conflicts require review and produce no import files. End dates are never guessed.
Adjacent existing periods with the same owner/contract can jointly cover a report period.
Partial coverage is held for review: the command neither shifts the start to a gap nor
creates a duplicate over the existing period. Updating existing associations is not part
of this Create-only preparation.
The workbook metadata, comments, column order and template format are preserved; identifiers
are text and dates are native Excel dates displayed DD-MM-YYYY. Empty workbooks retain their
headers. No Settl API mutations or drain-ledger updates occur.

Repair capture disables representative merging. Customer and payer must both equal the original
account, with a leading `9` for five-digit accounts; divergent source routing stays unresolved.
Contract names and periods come from the completed migration model, checked using the
production migration resolver and base mappings against the offline Xellent copy. Historical electricity
and closed charger subscription periods reuse the existing AboRabat reconstruction rules and
ordered source logs. Missing or ambiguous evidence blocks release rather than opening dates.
Existing correct associations are kept; this command only creates missing associations.

When `--migration-model` is supplied, every scoped MP is included in the import files.
The latest completed report for each MP supplies the account, customer/payer details,
relationship dates and original contract periods. Ties use report filename order.
Customer and payer rows are deduplicated by account; when shared-account details differ,
the latest selected report supplies the record and the difference is a warning.
Xellent and Settl comparisons are warnings only. Existing records, existing links and
partially overlapping periods do not exclude model rows or change dates. The files use
the templates' `Create` action, including for records already present in Settl. Preparing
the files does not execute an import or assert that Settl will accept every row.
Missing required migration records still fail preparation; snapshot values are never substituted.
Import dates are native Excel dates displayed as `yyyy-mm-dd`. The importer rejected
the supplied templates' documented `dd-mm-yyyy` display. Verification's Excel-value
column uses the same ISO date display; descriptive evidence keeps Danish dates.
Confirmed HomeCharge products `O HCEjeUL3`, `O HCEjeUL4` and `O HCEjeUL5` resolve
unmapped `V LadVariP`/`V LadVarUA` report names to `HomeChargeLadeEjeMel(O HCEjeMEL)`.
Accounts and report periods are preserved. Already resolved names are left as recorded.
UA periods carry `Manuel opfølgning efter import: UA-periode` and their original dates
in verification summary rows and contract evidence. Each row identifies the relevant MP.
Unresolved product names also use the shared product mappings, including payment terms.
Archived report lookups must explicitly record no charger before an unresolved `V LadVariP`
can use the pending-installation contract. `V AboRabat` is a discount marker: the existing
AboRabat rules select the companion's UA contract during the model's discount period and
the ordinary contract outside it. These boundaries come from the recorded discount dates;
the raw discount product is never exported as a contract.

The evidence package follows the existing Danish AboRabat reporting convention:

- `verification.csv` uses the same 12 Danish columns and semicolon delimiter. It contains
  one summary per MP and evidence for key import values only: customer/payer numbers,
  contract names and dates. Other populated fields are omitted. Customer/payer existence
  and proposed actions stay visible in the summary. Released cell references are checked
  against the saved workbooks; held proposals do not claim released references.
- `contract-evidence.csv` reviews effective electricity/discount/charger parts, source keys,
  exact historical end-date log records, charger/address evidence and proposed contract periods.
  The displayed end date is the effective restored date; superseded raw ends are evidence only.
  Parts from another owner or chargers without a documented MP association do not count.
Customer/payer evidence is included in verification; no third CSV or Excel evidence copy is generated.
Both CSVs use UTF-8 with BOM and quote/escape semicolons, quotes and line breaks. Supported
model rows with comparison findings say `Med advarsler`; warnings remain visible in both CSVs.
For the legacy workflow without a model, supported
rows held by the all-or-nothing release gate say `Afklaret – afventer frigivelse`;
blockers say `Uafklaret`. By default, a held package contains no import workbooks or invented cell references.
`report.json` remains the machine-readable decision and release gate. Existing output directories
are never overwritten; rerun into a new empty directory.

Import manually in numbered order: customers, payers, customer links, payer links, contract links. Check each
result before importing the next workbook. Refresh evidence before repeating a repair;
this preparation does not itself verify what Settl accepted during a manual import.


### Capture progress, failures and resuming the Settl phase

Capture logs name the current phase, start timestamp and elapsed seconds. Inventory logs
separate customers from payers and show page number, offset, actual rows returned and the
reported count. Settl may omit `CountResult` even when `DoCount=true`; the SDK represents
this as zero. Such counts are treated as unknown, with pagination continuing by the actual
page length until an explicit empty page. Short pages are not considered the end. Reported
positive totals must remain consistent with the returned rows; repeated identifiers,
premature empty pages, failed requests and inconsistent totals still leave capture incomplete.

Association progress is printed every 100 finished entities or 10 seconds while work
completes. It includes association/history/detail/historical-owner call counts and cumulative
API seconds. These seconds sum concurrent calls and can exceed elapsed wall time. A request
still pending after 30 seconds prints a heartbeat with its operation and entity GUID. Errors
name the phase and operation; pagination errors include offset/count metadata, and API errors
include a safe HTTP status when available. Names, addresses and billing values are not logged.

If Xellent repair capture finished and the subsequent Settl phase failed, update the executable
and resume only Settl in the same snapshot directory:

```powershell
DataMigration.OpsCli.exe pull-state-sources --env production --only settl `
  --scope-file missing-35.txt --include-sensitive-data `
  --output-dir C:\repair\customer-payer-snapshot 2>&1 | Tee-Object C:\repair\settl-capture.log
```

The saved Xellent repair section is retained and checksum-verified by offline preparation;
this resume command does not connect to Xellent. Keep the same scope file. The command's
recovery hint is shown only when saved Xellent repair evidence is available. A failed or
cancelled phase prints its elapsed time and the total elapsed time; only a completed capture
can be used for offline repair preparation.

### Scoped Settl reads

With a scope file, electricity metering points are resolved directly by identification;
the full metering-point inventory is not scanned. Delivery-history calls are limited to
those resolved points. Missing, mismatched, child or undated selected points fail explicitly.
The same Danish delivery-date rules apply to scoped and unscoped capture.

Contract discovery uses the resolved metering-point GUIDs and includes ended associations.
Shared contracts are fetched once. Only the relevant contracts have their association pages
read; their full pages are still needed to preserve original periods and association IDs.
Only selected-point links are saved. Logs show selected points, discovered contracts, link
reads and elapsed time. Unscoped capture keeps the full inventory behavior. Customer/payer
inventory scans remain necessary to detect unexpected ownership and are unchanged.

Held customer/payer repair packages also show each proposed populated field in
`verification.csv`, read from the same five templates in memory. `Excel-kolonne`
is the field name and `Excel-værdi` is its proposed value; `Importfil` and
`Excel-række` remain blank until release. The held status distinguishes these
proposals from released imports. Retained and unresolved MPs keep summary rows.
The all-or-nothing gate still writes no import workbooks for a held package.

Use `--draft-imports` to also fill the five supplied templates with supported rows
when a package is held. Copies are placed in `draft-imports/` and named
`DRAFT-NOT-FOR-IMPORT-1-Customers.xlsx` through
`DRAFT-NOT-FOR-IMPORT-5-ContractMeteringPoints.xlsx`. Unresolved MPs are excluded.
The input templates, release gate and `report.json` decisions are unchanged; the
command still exits with code 1. These copies are drafts for review, not released
imports. Verification keeps proposed field names and values without released
file or row references.


`prepare-customer-payer-repair --check-delivery-history` optionally uses the saved Settl
history to end old MP links. The default is off: migration dates are unchanged and delivery
history cannot hold an MP. When enabled, move-in ends old links the preceding day;
move-out or supplier departure ends them on the last supply day. Joining Verdo does not
change migration starts. Settl exclusive period ends are converted to inclusive import
ends. Customer/payer record dates remain unchanged. Earlier contract ends are retained;
periods starting after the cutoff are omitted. Missing or conflicting delivery evidence
holds the package; `--draft-imports` still provides only supported rows for review.
Both existing CSV reports state whether the check was enabled and show the event,
original periods and resulting periods. Only saved snapshots are read; no production
refresh or new-customer creation occurs. HomeCharge UA follow-up flags remain visible.

# Delivery-state pattern investigation

`report-delivery-start --include-history --env featuretest --output delivery-patterns.csv`
reads all non-child MPs, including true/false/missing delivery flags, with up to
eight concurrent history reads and progress/wait updates. No consumer-name
filter or Settl update is used. `--include-children` includes child MPs too.

The main CSV has one row per exact state/start-reason/stop-reason sequence and
an MP count. Dates and the current MP-level flag do not split groups. Consecutive
true periods stay separate; patterns are descriptions, not error classifications.
Periods are ordered by start; missing/invalid starts follow dated entries in
source order and are identified in the examples.

`delivery-patterns.examples.csv` contains up to three MPs per pattern, with all
their periods/states/reasons, current MP flag and current/completed list periods.
`SourceLine` refers to the corresponding line in `delivery-patterns.sources.jsonl`,
whose `ResponseJson` is the exact history response read for that example.
Unknown and missing values are retained, and failed/empty/missing histories have
separate groups. Only example responses are saved; there is no 60k-row MP export.
Without `--include-history`, the existing delivery-start CSV stays unchanged.

## Duplicate Consumer 2 cleanup rules

`fix-consumer2-duplicate` follows these rules:

- Reads Settl histories directly and uses the report's shared rules: for each delivery
  period, select its newest effective Consumer version, then assess the duplicate.
  The first delivery period is excluded. A clean newest version never falls back to
  an older duplicate. Pattern match and AnyDuplicateConsumer2 do not select fixes.
- Only the Consumer version in effect today is written, for every rule and option.
  It is judged itself when it overlaps a delivery period other than the first, so a
  future version later in the same period cannot hide it. Older or future versions
  the period rules pick are left alone and counted in the summary as "Selected but
  not current (left alone)". The report still shows the newest version per period.
- Each selected Consumer version is updated once, even when selected by several
  periods. No report file is read. Incomplete evidence is reported and the MP skipped.
- Full scans include all delivery states, including false or missing `InDelivery`,
  and exclude child MPs. `--mp-id` instead scopes the run to the supplied MP
  identifications or Settl GUIDs.
- Matches only customer type `Person`, in one of two cases:
  - Same name: names present for both consumers, and FullName, FirstName and
    LastName each match. Detection ignores surrounding whitespace and treats
    missing name fields as blank; comparison is case-sensitive.
  - National id without name: Consumer 2 has a national identification number
    but no name (no name object, or all three name fields blank). Settl's
    metering-point read never carries national ids, so the id is read from
    `GET /api/v4/meteringPoints/{id}/nationalIdentificationNumber?EffectiveDate=…`,
    only for private versions where consumer 2 has no name. A failed id read
    skips the MP.
  The dry-run line names the case. The national id value is never printed.
- After every write, the cleanup reads the same national-id endpoint and only
  counts the version as fixed when consumer 2's id is blank.
- Clears Consumer 2's name fields and national identification number. The update
  operation rechecks selection and equality before writing at the existing Consumer
  start date. MarketUpdate is omitted from both request levels; other fields are preserved.
- `--dry-run` lists matches without changing Settl, using up to eight concurrent
  reads and displays the selected Consumer dates in Danish time. Live cleanup updates
  sequentially and verifies both cleared fields on the same dated version. Read, write
  and verification failures are reported, including failures during full scans.
- `--limit` caps matching MPs, not the number scanned. The run stops once that
  number of matches has been reached.
- `--from-report <csv>` takes the points from a `report-consumer2-duplicate` CSV:
  the SettlId of every row with DuplicateConsumer2=Yes. Only the scope comes from
  the file; every point is read fresh from Settl and selected by the rules above.
  The header must match the current report exactly, and it cannot be combined
  with `--mp-id`.
- `--clear-fictitious-cpr2` also clears consumer 2's CPR where it is DataHub's
  fictitious 1111110000 and consumer 1 has a real CPR. Only the CPR is cleared;
  the name is sent back unchanged, because Settl replaces the whole second party
  and a patch without the name clears it. Same period rules as above. It needs
  `--mp-id` and cannot be combined with `--from-report`, so it only touches the
  points you list. Afterwards consumer 2 has a name and an empty CPR, which
  DataHub also rejects until Settl leaves an empty CPR out.

For a read-only preview:

```powershell
dotnet run --project src/DataMigration.OpsCli -- fix-consumer2-duplicate --env featuretest --dry-run
```

# Duplicate Consumer 2 version report

`report-consumer2-duplicate` is read-only. It checks historical, current and future
Consumer versions, using the cleanup tool's private-person rule (same name, or
national id without name).

~~~powershell
dotnet run --project src/DataMigration.OpsCli -- report-consumer2-duplicate --env featuretest --output consumer2.csv
dotnet run --project src/DataMigration.OpsCli -- report-consumer2-duplicate --env featuretest --mp-id 571313100000000002 --limit 10 --output consumer2-scoped.csv
~~~

## Report rules

- Check all MPs regardless of delivery pattern. DeliveryPatternMatch is Yes when
  the oldest two periods, ordered by start time, are false (Na -> Na), then
  true (ChangeOfSupplier -> Na). Later periods are unrestricted. Other histories
  show No; failed or ambiguous histories show Unknown and a warning. Consumer
  checks continue in every case. Incomplete evidence returns exit code 1.
- Full scans check non-child MPs in every delivery state. The CSV includes MPs
  with at least one duplicate Consumer version or incomplete Consumer/delivery
  evidence. Clean MPs are omitted regardless of their pattern flag.
- ConsumerDates supplies distinct version starts. The current Consumer is read
  once; each other dated version is checked separately using the existing rule.
- Each CSV row is one delivery period from /deliveryStatuses. DeliveryStatusDates
  and dated delivery-status versions are not used. All periods of included MPs
  appear, ordered by start time, including periods before/after Consumer dates.
- ConsumerVersionCount counts every Consumer version effective during the period,
  including one that started earlier. Periods include their start and exclude
  their end: a Consumer ending at delivery start or starting at delivery end does
  not count. The newest effective version is the one with the latest start.
- Consumer1Name, Consumer2Name, ConsumerStartDk, ConsumerEndDk and
  DuplicateConsumer2 describe that newest version only, except the first chronological delivery period always
  shows DuplicateConsumer2=No. This exception does not affect AnyDuplicateConsumer2,
  MP inclusion or duplicate totals. An earlier version can
  have a duplicate even when the newest shows No. MP/version totals and --limit
  still use duplicate detection across all Consumer versions.
- AnyDuplicateConsumer2 is Yes when any Consumer version effective during the
  delivery period has a confirmed duplicate, even if the newest shows No. It is
  No when the complete evidence has no duplicate; incomplete evidence without a
  confirmed duplicate shows Unknown. A confirmed Yes survives other read errors.
- A complete history with no effective Consumer shows count 0 and blank Consumer
  names/dates. Incomplete Consumer history or invalid delivery dates show count
  Unknown, blank Consumer values and DuplicateConsumer2=Unknown (No on the
  first period), with the warning.
- DeliveryWarning and ConsumerWarning are blank when valid. A missing delivery
  history retains one diagnostic row with blank delivery values and a warning.
- The CSV has 23 columns. RowType, environment, current delivery summaries, LastUpdated,
  change signals and calculated overlaps have been removed. Include the
  environment in the output filename, for example consumer2-production.csv.
- MarketUpdate contains the selected Consumer version's MarketUpdate value as
  true/false, or blank when no Consumer version can be selected.
- Consumer2Reason says why DuplicateConsumer2 is Yes: `Same name` or
  `National id without name`. It is blank on every other row. The national id
  value itself is never exported.
- Consumer2NameWithEmptyNationalId is Yes when the row's private Consumer
  version has a consumer 2 name and Settl holds consumer 2's national id as an
  empty string, not null. DataHub rejects a customer update that carries a name
  with a blank CPR (BRS-015). Report only.
- CompanyWithConsumer2 is Yes when a company has a named consumer 2. DataHub
  allows one customer on a company; Settl reports it as "Organization ID / CVR 2
  is not filled in". Report only.
- Consumer2FictitiousCpr is Yes when consumer 2's CPR is 1111110000 and consumer
  1 has a real CPR: what `--clear-fictitious-cpr2` clears.
- CprInvalidFormat is Yes when a filled-in CPR 1 or CPR 2 is not 10 digits.
- These four columns describe the row's newest version. A point with no
  duplicate is listed when any of its versions has a Yes in one of them. CPR
  values are never exported; every private version gets a national-id read.
- All four date columns use Danish time with daylight-saving offsets (+01:00 or
  +02:00), and names ending Dk. Null and year-9999 ends show (open).
  Consumer dates without an offset are interpreted as UTC. Missing or inconsistent
  evidence returns exit code 1 while retaining successful findings. An unread
  Consumer has DuplicateConsumer2=Unknown, except the first period shows No.
  If missing or tied starts prevent identifying the first period, the newest-version
  duplicate flags show Unknown with a delivery warning.
- Counts distinguish affected MPs from duplicate versions. --limit caps affected
  MPs, checks all their Consumer versions and exports all their delivery periods. --mp-id accepts
  identifications or Settl GUIDs, space/comma separated.
- The CSV contains names but no CPR values. No Settl writes are made. Cleanup and
  its dry-run reuse the same per-period selection rules, reading fresh Settl data.

Progress goes to stderr and shows checked MPs, version reads, affected MPs,
duplicate versions, errors and phase elapsed time. Up to eight requests run
concurrently. Waiting for Settl is shown only once a pending batch has waited five
seconds; fast batches no longer repeatedly display a zero-second wait.
