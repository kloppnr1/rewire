# MP-list payment match: rules

Rules for `payment-match-mp`, ported from the MigrationDashboard MP-list report (US 46004). The engine is in `DataMigration.OpsCli.Library/Reporting/PaymentMatchMp`.

```powershell
dotnet run --project DataMigration/src/DataMigration.OpsCli -- payment-match-mp points.txt payment-agreements.csv --type pbs --cutover 2026-09-01 --env production
```

Run summary, source-file names and progress go to the console, never into the report. Names are report data only and are never written to the console.

These rules apply only to an explicit list of metering-point IDs. The profile-driven `payment-match` keeps its own rules. PBS and MobilePay reports use the same selection policy; their payment identifiers and existing match algorithms differ.

### Input and ownership

- Supply an MP list (TXT/CSV), the Settl payment-agreement CSV, `--type pbs|mb` and `--cutover`. `--env` picks the Xellent database and labels the files. No Settl API lookup or write is performed.
- MP IDs must be 18 digits. Invalid/empty lists are rejected; duplicates are counted and each unique MP receives one coverage result.
- Find the Xellent contract valid for each MP the day before cutover: start on/before that day, and end on/after it or the `1900-01-01` open-end sentinel.
- Missing identifiers, missing owners or multiple customer accounts are unresolved. Overlapping contracts on one account retain the existing latest-start/contract-number tie-break. A changed or missing owning contract during the subsequent read fails the run. Missing customer records are unresolved rather than assumed private.
- The first disponent's CVR follows the existing Xellent mapper convention: eight digits means business; otherwise private. CVR does not participate in matching; paired customer names determine the primary report status after agreement selection and CSV lookup. Customer-account matching uses the source Xellent account, not a merged-account expansion.

### Agreement selection: both payment types

- For both private and business customers, use the explicitly linked main contract's eligible agreement for the selected report type. If the electricity contract has no main-contract reference (absent or blank), use its own eligible agreement. Never search unrelated customer contracts.
- The linked main contract must be a different contract on the same Xellent account and valid the day before cutover. When a main-contract reference exists, missing contracts, wrong-account links, future/expired main contracts and missing/ineligible agreements produce `NoAgreement` (`Ingen relevant aftale`), even if electricity has an active agreement or the CSV contains that MP. An unusable explicit main link never falls back to the own agreement. Without a main link, an ineligible own agreement also produces `NoAgreement`. There is no customer-wide or recursive main-contract search.
- If an electricity-to-water/heat main-contract link was removed from 1 September, the electricity contract uses its own agreement; the water/heat agreement cannot be inherited without an explicit link. There is no date-dependent reconstruction of removed links.
- PBS and MobilePay selection is independent: each report reads its selected payment type from the main contract, or from the own contract when no main link exists.
- Existing payment-status definitions are preserved. PBS eligibility uses the shared report rule: positive transaction codes except 4 and 5, including code 3 (FI-card), code 6 and other positive codes. Selecting a main PBS agreement also requires a nonzero agreement number; own-contract selection retains the original status-only eligibility, including number zero. MobilePay requires a populated ID and cancellation date exactly `1900-01-01`; SQL NULL does not satisfy the existing default reader rule.
- MP-list mode has no cancelled-agreement option. The command has no `--include-terminated` option; `payment-match` keeps it.

### Business error check

Check only the selected report's payment type. In a PBS report, flag `BusinessPaymentOnlyOnWaterHeat` when the business electricity contract has no eligible PBS payment but a valid water/heat contract on the same account has one. In MobilePay, make the same check using MobilePay only. The other payment type neither triggers nor suppresses this error. The check uses existing eligibility definitions, including PBS codes 3 and 6.

The check does not select an agreement or change matching. A valid linked main agreement can therefore be matched while this separate error is also shown. Supporting water/heat rows show only the selected type, with contract, commodity, agreement ID and PBS transaction code when applicable; unrelated evidence is never used as the electricity agreement. Count each affected MP once, even with several supporting agreements. Contracts and their commodity parts must be valid the day before cutover. Commodity names come from dated contract parts, including mixed `Vand/Varme` contracts; when no parts are available, retain the contract header's commodity.

### Matching: existing algorithms

- **PBS:** match the selected owner's MP first, then joint invoice by the owning contract number and its main-contract reference. Main-contract selection changes agreement provenance, not these lookup keys. Multiple CSV rows on one MP remain separate; duplicate joint keys use the first row. Relevant unmatched CSV rows remain visible.
- **MobilePay:** try the supplied MP on the agreement's source contract, then other supplied MPs on the same account, then joint invoice by the agreement's source contract and its main-contract reference. The existing same-account MP fallback is unchanged. Coverage links an agreement only to MPs that selected it; shared agreement detail rows are not duplicated per covered MP.
- Joint-invoice lookup uses CSV rows without an MP ID. Neither matcher compares names, agreement identifiers or payment methods to decide the connection.
- Different E-numbers under one MP/joint key remain separate primary report rows, retaining each actual E-number, payment method and customer name. Each row is evaluated independently by the name rule below; only the unique highest numeric E-number among equal-name eligible rows on that MP is `Match`; lower rows are `Ikke matchet`. Internal compatibility agreement results remain cleared for these keys. The CSV payment-agreement number appears in `Bemærkning`, never in the Xellent agreement-ID column. Shared MobilePay associations retain their corresponding CSV rows. The primary report retains repeated raw CSV entries even when their E-number is identical, so duplicate highest rows cannot be hidden. Compatibility results retain the underlying matcher behaviour: MobilePay and joint lookups use the first row; PBS MP matches retain separate rows.
- CSV rows require an E-number. Payment-agreement number may be absent/blank. The report uses CSV **customer name**, never payer name, for the payment-agreement customer name.

### Results

`payment-match-mp` writes three CSVs: the report (the primary result), coverage (one row per MP) and agreements (one row per agreement found). The report columns are: MobilePay retains the original 16 payment-report columns; PBS retains those plus `Kandidater på målepunkt`. Exactly three customer columns follow them.

| Position | Column |
|---|---|
| 1 | Kundenummer |
| 2 | Betalingsaftale fundet på kontrakt |
| 3 | Kontrakttype |
| 4 | Kontraktslutdato |
| 5 | Målepunkter med egen betalingsaftale |
| 6 | Status |
| 7 | Bemærkning |
| 8 | Målepunkt |
| 9 | Nøgle der matchede i Settl |
| 10 | Enummer Settl |
| 11 | E-nummer hentet fra betalingsmetode |
| 12 | Migreret kontrakt |
| 13 | Betalingsaftale på migreret kontrakt |
| 14 | COS |
| 15 | Betalingsaftale-ID |
| 16 | Betalingsaftale-status (Xellent) |
| 17, PBS only | Kandidater på målepunkt |
| Appended | Kundenavn på betalingsaftalen i Settl |
| Appended | Kundenavn i Xellent |
| Appended | Kundenummer i Xellent |

`Kundenummer` retains its original Xellent account meaning; the explicitly requested `Kundenummer i Xellent` repeats that source identifier. The Settl name is the corresponding export row's `customer name`, never its payer name. Names are not combined across independent agreement/E-number pairs.

Every supplied unique MP appears. Multiple agreement/export pairs produce separate rows repeating the MP. A shared MobilePay agreement appears for every MP associated with it, retaining the same actual match key and E-number; its internal handover grouping is unchanged. MPs without an agreement or identifiable owner receive explicit rows explaining the result. Business error evidence remains separate rows with no inferred E-number. Without an eligible agreement on the required contract, the primary report status is `Ingen relevant aftale`, including when a corresponding Settl-only row remains visible, unless the separate business error requires `Kræver undersøgelse`. Both payment types use `Match`, `Ikke matchet`, `Ingen relevant aftale`, and `Kræver undersøgelse`. With an eligible selected Xellent agreement and a corresponding nonblank E-number, compare that row's CSV customer name with the Xellent name: both must be nonblank and equal after trimming ends, ignoring case with ordinal comparison. There is no fuzzy comparison or account-wide joined-name comparison. Equal names qualify a row for selection. On each MP independently, compare the arbitrary-length numeric digits after uppercase `E`; the unique highest row is `Match`, and lower rows are `Ikke matchet`. Unequal/missing names or no CSV row give `Ikke matchet`. Duplicate highest numeric values, including leading-zero ties, produce `DuplicateHighestENumber`: tied rows require investigation with an explicit error and no winner. An unrecognizable qualifying E-number produces `InvalidENumber` and no winner. Both errors mark the MP unresolved without counting as business errors. No eligible agreement remains `Ingen relevant aftale`. True ownership problems and business errors retain `Kræver undersøgelse` with their diagnostics. An MP counts as matched only after selecting its unique highest qualifying row; otherwise it is unmatched or unresolved for the explicit selection errors. Shared MobilePay rows are evaluated separately for each associated MP. Review summary counts deduplicate actual primary review rows by source agreement. Internal matcher statuses and compatibility agreement results are unchanged. The original `Status` and `Bemærkning` carry errors, ambiguity and commodity evidence; no extra customer-type, error-code, metadata or coverage columns are added to the report.

MP-list remarks and coverage explanations describe only rules that affect the result. Highest-E-number selection is mentioned only when multiple eligible equal-name rows compete on that MP. A single qualifying row has only the name-match explanation. Missing export/E-number rows are explained as missing export data, without claiming a name mismatch. No-agreement and business/ownership error rows retain their applicable diagnostics without adding name-match or highest-E-number explanations.
Settl agreement-number text is omitted when the export number is blank. Joint-invoice candidates that fail the final checks say 'Fundet via fællesfaktura.'; only final matched rows say 'Matchet via fællesfaktura.'.

`Målepunkter med egen betalingsaftale` is restored from the original source definitions, not the number of output rows or linked agreements. For PBS it counts distinct eligible agreement numbers on consumption-linked account contracts not ended before cutover−1, using the original `E17/Forb`/`FORB` joins and shared eligibility rules. Umbrella contracts without consumption MPs do not add to that count. MobilePay counts distinct active agreement IDs across the source account, with the existing cancellation rule and no source-contract end-date filter. `Betalingsaftale-status (Xellent)` uses the PBS source transaction-code label or MobilePay active/cancellation label, including the correct payment type for business evidence. PBS candidate counts come from distinct source owner candidates returned for that MP on cutover−1, not from the number of report rows.

Source contract type and end date describe the actual contract carrying the agreement. Selected agreements identify their explicitly linked main contract or the own contract when no main link exists; diagnostic rows retain their actual source type. `Migreret kontrakt` names the owning contract for the MP on that report row. The lookup-key column identifies the actual CSV lookup, including separately evaluated competing rows and prefixes joint keys with `Fællesfaktura`. Names stay blank when the paired CSV row is absent; separately evaluated rows retain their own CSV name while internal safe matches remain cleared.


Existing MobilePay review flags remain, including subcontract placement and multiple active agreements; the global scan's exact-COS-minus-one end-date warning is suppressed because ownership here is based on dated validity. Review flags may accompany a successful match. Source and CSV lookups remain batched/indexed for large uploads. CSV escaping protects formulas and 18-digit MP IDs. Names are report data only and must not be written to logs.

