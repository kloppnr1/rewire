# Time-series knowledge entry point

This guide carries useful Claude project knowledge into shared documentation. Statements below were checked against current source; they do not prove current database contents or live Settl behaviour. Read only the topics needed for the task.

| Topic | Start here | Current source evidence |
|---|---|---|
| Running commands, batch scope, skip reports, snapshot and done marks | [Workflow](../../README.md) | [CLI options and environment selection](../../src/TimeSeries.Cli/Program.cs), [runner](../../src/TimeSeries.Migration/Runners/TimeSeriesRunner.cs), [snapshot storage](../../src/TimeSeries.Migration/Services/RunSnapshot.cs) |
| Delivery window, ownership, migration closure, child fallback | Rules below and README `post --all` | Runner: `RunPostAllCoreAsync`, `ResolveRevisionAsync`, `CheckMigrationClose`, `AccountHeldSince` |
| Agreements, stored resolution and reading-period mismatches | Rules below and README skip-file descriptions | [Extraction](../../src/TimeSeries.Migration/Services/TimeSeriesExtractionService.cs), [resolution tests](../../src/Tests/TimeSeries.Cli.Tests/Runners/ResolutionSegmentTests.cs) |
| UTC points and Danish date boundaries | [Time-zone guide](../dst-handling.md), with the caveat below | Extraction: `ToDanishUtc`, `ToAgreementPeriod`; runner: `ToDanishDate`, `ComputeEipPeriods`; [DST payload tests](../../src/Tests/TimeSeries.Xellent.IntegrationTests/DstPostPayloadTests.cs) |
| Settl writes and package ownership | Rules below; [historical API guide](../settl-api-integration.md) for background only | Runner: `RunPostCoreAsync`; [package references](../../src/TimeSeries.Migration/TimeSeries.Migration.csproj) |

## Delivery and source ownership

- Batch scope comes from Settl electricity metering points; delivery category is `El-ekstern`. Parents are the default. `--include-children` adds children from Settl relations or the Xellent relation table.
- The revision normally comes from the single `InDelivery=true` status. Multiple active statuses cause a safety skip. With none, the runner can use the current v4 delivery status; a list entry shown as not in delivery must still be examined.
- The Xellent owner is the account whose contract covers the Danish day before the revision. A clean migration requires that contract and its agreement to end on that day, with agreement status 5 (delivery ceased). An unclean in-delivery point fails; a point not marked in delivery is skipped as not migrated.
- The batch window ends on the Danish delivery-start date. It starts at the latest of three years before that date, the resolved delivery boundary, and the account's continuous contract history. The boundary is the start of the `InDelivery=false` period ending at the first delivery's start; with no `InDelivery=false` period starting before it (none, or only later ones) there is no boundary. The end must match the current Settl delivery-status start. A library-only window override exists; do not treat it as a CLI option or use it to bypass production verification.
- A child uses its own delivery and ownership data where available. Missing delivery/holder information can fall back to its parent. Its own points still go to its own Settl time-series GUIDs.

## Resolution and time boundaries

- Each Xellent series belongs to a company. Its eligible points are limited by that company's agreements and reading periods. Series with values but no eligible agreement/period are reported as invalid.
- Extraction keeps stored values and resolution, with dated reading resolution on each segment. For story 45902, outgoing hourly values in quarter-hour reading periods are split equally into four 15-minute points; earlier hourly periods remain hourly. Merge only periods with the same reading resolution. Source-series selection still prefers an existing series matching the reading period. Preserve mismatch evidence in `known-issue-xellent-resolution.csv`. Validate all included splits within five decimal places and all required Settl series before any write for the metering point.
- The runner matches Settl series by outgoing resolution length/unit, not the first series returned. Missing matching series prevents posting. Readiness and resolution-change reports describe blockers; they create no Settl series.
- `TimeSeriesRunner.ScanMissingReadingsAsync` is a read-only library scan for the dashboard. It uses the batch migration customer/window rules, reads Settl live across all series, and includes previously marked done MPs without changing done marks or snapshots. `Missing` means zero historical readings, `Present` means at least one (not full coverage), and `Unknown` means the window or a required read could not be verified. It does not read Xellent consumption values or post data. Publishing the producer package is required before dashboard integration can be verified.
- `TIMEOFVALUE` is interpreted as UTC and sent as UTC. Do not apply a second CET/CEST conversion to point timestamps. Agreement/reading dates are Danish calendar dates: inclusive Xellent ends become exclusive next-day bounds, and Danish midnights are converted to UTC. DST therefore matters for window bounds even though stored points need no DST repair.
- The time-zone guide contains historical verification and obsolete CLI file paths. The integration README also describes the removed normalization service. Use current extraction and test source when those descriptions disagree.

## Environments, batching and writes

- Configured environments are `featuretest`, `production`, `production-hev`, and `production-vhel`. Selection is explicit `--env`, then `DOTNET_ENVIRONMENT`, then `featuretest`. Unknown environment sections fail during configuration loading; there is no `staging` alias. Pass `--env` explicitly for verification.
- `post --dry-run` performs reads and validation but skips both time-series and externally invoiced-period POSTs. Batch dry runs count points per dated segment and check conversion precision; targeted dry runs may read points. Local CSVs and snapshot/cache files can still be written.
- `post --all --offline` uses the environment snapshot but fetches missing entries live and caches them. Live posts read points fresh and write to Settl. Offline is not a safety substitute for dry-run. `coverage` is strictly offline and fails on missing snapshot evidence.
- Done marks and payers matching `^6\d{5}$` are excluded before batch pagination. `--skip` counts the remaining points with delivery starts in range in identification order, before ownership and later per-point checks. `--limit` counts only metering points sent (ready, in a dry run); skipped, blocked and failed ones do not use it up. The missing-readings scan sends nothing, so there it still counts every point in range.
- Snapshot seeding marks a candidate done when any relevant Settl series already has points in its window; this is not proof of complete coverage. Successful live batch posts with points also mark it done. Done marks are `meteringPoint` lines in the ledger ([PostLedger](../../src/TimeSeries.Migration/Services/PostLedger.cs)); an old `done.jsonl` is copied in once and renamed to `done.jsonl.moved`. `snapshot --refresh` retains done marks, and cached summaries can be stale. Check them before interpreting progress or reposting.
- Every live write to Settl (points per series, externally invoiced period) is recorded in `output/snapshot/<env>/ledger.jsonl` before and after the call ([ledger](../../src/TimeSeries.Migration/Services/PostLedger.cs)). A write already done is not sent again; one never finished, or overlapping an earlier one on the same series, blocks the metering point. Dry runs never write the ledger; `snapshot --refresh` keeps it. Posts before 2026-10-22 are not in it.
- Failures stop the run unless `--force` is used; with `--force`, failures still cause a nonzero exit. Successful earlier writes are not rolled back if a later segment or externally invoiced-period write fails.
- Externally invoiced periods include `Contracts`, `ExtraInvoiceLines`, `MarketPriceLists`, `NationalContracts`, `PaymentFee`, and `MovingFee`. Do not change this business rule without explicit confirmation. The API guide's deleted local client paths and first-series example are historical; clients now come from `SettlClient.Api`. Xellent entities come from `XellentDomain.*` packages, not the old UtilityData package or local forks.
- Preserve the runner's plain `Run completed:` totals line: the dashboard parses it. Parent/child breakdown belongs on its separate line.
- `OUTGOING MP <id>: resolutions=Hour+Quarter, convertedHours=24, convertedQuarters=96` describes the outgoing plan after delivery filtering, before skips and validation. The dashboard uses this explicit evidence for conversion markers and filtering; zero counts mean no conversion. This line does not prove a successful post. Source extraction resolution stays separate.
- Opt-in `ExtractOptions.IncludeOutgoingPreview` writes `OutgoingPreview_<mp>.json` alongside unchanged source exports. It pairs each original value with the values from `OutgoingTimeSeries.Prepare`, applying the captured `DeliveryBoundary`. Excluded source values remain visible without outgoing points. Whole-meter precision failures retain source values but suppress all outgoing values. Previews cannot include simulated values and do not prove Settl has accepted data.

- Conversion-aware snapshots use `xellent-point-counts-v3` and the `dated-quarter-conversion-v1` key suffix. Refresh missing evidence; never reuse old source-only counts. Done marks remain unchanged. `resolution-changes` lists one row per reading-period switch inside the post window (either direction) when Settl lacks the Hour or Quarter series; no switch means no row, and more than one switch puts a warning in the log. The file holds only `MeteringPointId;ChangeDate;MissingSeries`, unquoted, with a BOM. `post --all` blocks a metering point with such a switch, whatever series Settl has (`resolution switch – waiting for Settl to confirm series`); `--allow-switches` sends it. No meter installed (no values, status Oprettet) is checked first and stays skipped. `post --all` reads the subtype (`EXU_METERTABLETRADE.METERINGPOINTDHSUBTYPE` on the meter row that starts last, on the delivery point whose agreement starts last; [extraction](../../src/TimeSeries.Migration/Services/TimeSeriesExtractionService.cs), `GetMeteringPointSubTypesAsync`). Virtual (2, E18 production) is migrated like physical and calculated. No meter row or a value outside 1–3 is blocked just before the switch block (`subtype blank in Xellent`) unless no meter is installed. The rule is shared ([runner](../../src/TimeSeries.Migration/Runners/TimeSeriesRunner.cs), `ReadingPeriodSwitches`). Dashboard run totals count outgoing points; extraction and delivery-filter counts remain source counts.

## Verification for code changes

Run from the repo root unless stated otherwise, in workspace verification order:

```powershell
dotnet build TimeSeriesMigration.slnx
dotnet test src/Tests/TimeSeries.Core.Tests/TimeSeries.Core.Tests.csproj
dotnet test src/Tests/TimeSeries.Cli.Tests/TimeSeries.Cli.Tests.csproj
```

For extraction or Settl mapping changes, also run `dotnet test TimeSeries.Xellent.IntegrationTests.csproj` from `src/Tests/TimeSeries.Xellent.IntegrationTests/`. It is in `rewire.slnx` and runs on a seeded SQLite copy of Xellent, so it needs no VPN or settings. Inspect [integration setup](../../src/Tests/TimeSeries.Xellent.IntegrationTests/IntegrationTestBase.cs) and the [payload assertions](../../src/Tests/TimeSeries.Xellent.IntegrationTests/ResolutionSegmentPayloadTests.cs). Do not promote changed golden baselines without reviewing the behaviour change.

Then use the actual host for a parsing/configuration smoke check:

```powershell
dotnet run --project src/TimeSeries.Cli -- post --help --env featuretest
```

Configuration loads before help parsing, so even this requires a valid environment configuration. For a changed mutation path, follow workspace dry-run, source spot-check, then featuretest hot-run verification using the concrete authorized scope. Production requires `--dry-run`. Documentation-only edits require link and diff checks, not application runs.
