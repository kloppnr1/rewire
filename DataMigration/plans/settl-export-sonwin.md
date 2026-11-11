# Nightly Settl export to network share (SonWin migration)

## Goal
- Nightly read-only export of Settl `production` data to a network share, as input to the SonWin migration
- Scope: the data the Xellent migration wrote to Settl, for electricity only
- Runs until cutover to SonWin

## Decisions
- Source: Settl only, no Xellent reads
- Selection: customers linked today to an electricity metering point with `InDelivery=true`
- Two active delivery statuses on one metering point cannot occur; if Settl returns it anyway, the run fails
- Current state only, no history: links open today or starting later; ended links left out (2026-11-11)
- Environment: `production`, own folder per environment
- Schedule: nightly
- Personal data on the share: approved
- Out of scope: time series, externally invoiced periods, invoices, activity notes, water
- Reference examples: private `Customer_405013.xlsx`, business `Customer_397697_Verdo_Varme.xlsx` (06.11.2026)

## Command
- Operated through `settl-export.ps1` (Trial, Run, Check, Schedule, Unschedule), deployed next to the exe: [runbook](../docs/settl-export-runbook.md)
- `export-active-customers --env production --deliver-to <unc> --payment-agreements <csv> [--keep 14] [--raw]`
- `check-share-delivery --env production --deliver-to <unc>`: read-only check of the newest run
- `--limit`/`--customer` with `--deliver-to`: trial run in `<env>-trial`, never touches the real delta
- GET only; never Settl's export endpoints (`POST .../export`, `exportApprovedSettlement`, `POST /SettlementExports`)
- Identifiers kept apart: Settl GUID, 18-digit metering point identification, EAN on payer

## Content (done)
- Customer: master data, CPR/CVR, segment, manual approval
- Payers: billing and payment configuration, automatic approval, active metering point links
- Metering points in delivery: master data, consumer, delivery status, billing configuration, ignore in billing, VAT exempt, exemptions, settlement groups, price area, grid area, remark, meters
- Market price lists per metering point (id, type, charge owner GLN, period, occurrences, name, description)
- Contracts: full contract, settlement terms, lines not ended
- Customer price lists (price list, product element, contract, price area, period)
- Extra invoice lines
- Payment agreements from the Settl 'Payment Agreement' CSV, by metering point or joint invoice number
- Settl ids resolved to names once per run (price area, grid area, segment, price list, product element)
- All dates are Danish calendar dates of the Settl instant; a value without zone keeps its date (2026-11-11)

## Delivery (done)
- Run folder `<root>/<env>/<yyyy-MM-ddTHHmmssZ>/`: `customers.jsonl`, `delta.jsonl`, `manifest.json`, `_READY` last
- Written to `.tmp-<run>`, renamed when complete
- Delta against the last published run: added, changed (full line), removed (number only); key = customer number
- State (line hashes) in `<env>/state/`, saved after publishing
- Failed export or read errors: `<run>.failed`, no `_READY`, state unchanged, non-zero exit
- Payment CSV missing or older than 24 hours: run refused
- `.lock` stops overlapping runs; a crash leaves it, removed by hand
- Keeps the newest 14 published and 14 failed runs

## Open
- Host and UNC path; account for the scheduled task; who watches `Check` (alerting not built)
- Payment CSV: who exports it from Settl before each run; ask the vendor for an API endpoint
- SonWin: projected or `--raw`, schema, pick-up by `_READY`
- Settl vendor: modified-since filter or change feed (would replace the full scan)
- Not resolved to names: balance supplier (no lookup endpoint known)
- Not in the export: market price list mandatory and direct invoicing (route and id mapping unverified), "contract shared with other customers", product description template
- Live check: the new routes (market price lists, contracts, contract lines, price lists, product elements, reference lists) come from the Settl API description, not a live call

## Phases
1. Spike (done): `ExportActiveCustomersFlowTests` over a fake tenant filled by real `create`
2. Build (done): content above, JSON Lines, share delivery; tests against the fake
3. Real-system testing, by the user, following the runbook: featuretest trial, full run, payment CSV, SonWin trial import
4. Production: runbook steps 4 and 5, after host, share and SonWin format are settled
