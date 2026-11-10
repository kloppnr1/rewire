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
- Out of scope: time series, externally invoiced periods, invoices, water

## Content (= what `create` and the price-list import wrote)
- Customer (v3 + v2, incl. CPR/CVR)
- Payers incl. billing and payment configuration
- Metering points: market master data, consumer national ids, delivery statuses, billing configuration, settlement groups
- Links: customer, payer and contract to metering point (active only)
- Contracts, customer price lists, extra invoice lines
- Verify on a real record: billing exemptions and estimated annual volume are present

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
- Payment agreements (PBS/MobilePay): channel + agreement number from payer billing is all the export has; full agreements have no API client (today a manual Settl CSV). Needs SonWin's requirement, then possibly a vendor question
- Host and UNC path
- SonWin: raw or projected JSON, schema, how they pick up runs
- Settl vendor: modified-since filter or change feed (would replace full scan)

## Phases
1. Spike: run `export-active-customers --raw --limit 100 --env featuretest` locally; measure time and request count; check exemptions and annual volume
2. Build: JSON Lines, share delivery, manifest, delta, lock; unit tests for diff, hashing, atomic write
3. Featuretest: nightly schedule to a test share; SonWin trial import
4. Production: after host, share and SonWin format are settled
