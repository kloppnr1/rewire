# Nightly Settl export to network share (SonWin migration)

## Goal
- Nightly read-only export of Settl `production` data to a network share, as input to the SonWin migration
- Scope: the data the Xellent migration wrote to Settl, for electricity only
- Runs until cutover to SonWin

## Decisions
- Source: Settl only, no Xellent reads
- Selection: customers linked today to an electricity metering point with `InDelivery=true`
- Two active delivery statuses on one metering point cannot occur; if Settl returns it anyway, the run fails
- Current state only, no history
- Environment: `production`, own folder per environment
- Schedule: nightly
- Personal data on the share: approved
- Out of scope: time series, externally invoiced periods, invoices, activity notes, water

## Content (= what `create` and the price-list import wrote)
- Customer (v3 + v2, incl. CPR/CVR)
- Payers incl. billing and payment configuration
- Metering points: market master data, consumer national ids, delivery statuses, billing configuration, settlement groups
- Links: customer, payer and contract to metering point (active only)
- Contracts, customer price lists, extra invoice lines
- Reference examples: private `Customer_405013.xlsx` (= current projection), business `Customer_397697_Verdo_Varme.xlsx` (06.11.2026)

## Gaps from the business example (not in the projection today)
- Market price lists per metering point: type, period, charge owner GLN, occurrences, invoicing flags; Market `GET /api/v3/meteringPoints/{id}/marketPriceLists`, no client yet
- Customer price lists and product (spot price, surcharge, product elements): read with `--raw`, add to projection
- Full contract: order number, category, payment terms, frequencies, contract lines, product setup
- Metering point: ignore in billing, VAT exempt, settlement schedule, price area, grid area, balance supplier, meter type code, consumer category, electrical heating
- Customer: segment, settlement schedules, manual invoice approval
- Payment agreements as in the business example: metering point or joint invoice, external customer no., payment method, last updated
  - Source: Settl 'Payment Agreement' CSV from a drop folder, read with existing `SettlCsvParser` (add last updated)
  - Attach to the exported customer by metering point id, else by joint invoice number
  - CSV missing or older than the run's day: no `_READY`, non-zero exit

## Approach
- Extend existing `export-active-customers` (OpsCli); it already selects the right scope and reads the sections above
- Output: JSON Lines, one customer per line, raw Settl JSON minus `Uri`/`Links` (`--raw`), or the approved projection; pick one with SonWin
- Keep identifiers apart: Settl GUID, 18-digit metering point identification, EAN on payer
- GET only; never call Settl export endpoints (`POST .../export`, `exportApprovedSettlement`, `POST /SettlementExports`)

## Delivery to share
- `--out <unc>`; run folder `<env>/<yyyy-MM-ddTHHmmZ>/`
- Write to `.tmp` folder, rename when complete, write `_READY` last
- `manifest.json`: schema version, env, run start/end, counts, SHA-256 per file
- Delta file against previous run: added, changed, removed (left delivery or gone from Settl)
- State (hashes per customer) in `<env>/state/`
- Lock file against overlapping runs; retention of N runs
- Settl unreachable or any section error: no `_READY`, non-zero exit
- Logs: ids, counts, timings only

## Hosting
- Windows Task Scheduler on a host that reaches Settl production and the share
- Service account with write access to the share only
- Alert on non-zero exit or stale `_READY`

## Open
- Payment agreement source: no Settl API endpoint documented; ask the vendor for one. Until then someone must drop the manual Settl CSV before each run; who and how often is open- Host and UNC path
- Future-dated links (e.g. a contract or price change starting next month): today's rule drops them; "current state only" may need to mean "open today or later"
- SonWin: raw or projected JSON, schema, how they pick up runs
- Settl vendor: modified-since filter or change feed (would replace full scan)

## Phases
1. Spike (done): `ExportActiveCustomersFlowTests` runs the export over a fake tenant filled by real `create`
   - Exported: customer, in-delivery metering points with market, billing and delivery status, payers and links, open-today contracts, price lists, extra invoice lines
   - Left out: metering points not in delivery, customers without one, contract links starting after today
2. Build: JSON Lines, share delivery, manifest, delta, lock; tests for diff, hashing, atomic write against the fake
3. Featuretest: nightly schedule to a test share; SonWin trial import
4. Production: after host, share and SonWin format are settled
