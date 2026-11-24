# Time zone handling

How time-series timestamps flow from Xellent to Settl.

## Xellent storage

`EMS_TIMESERIESVALUES.TIMEOFVALUE` is stored as **UTC** in the Xellent SQL database (verified 2026-05-20 against AXDB50_TEST for MP 571313115242231955: the fall-back day 2024-10-27 has 25 unbroken UTC hours 21:00→04:00 without a repeated 02:00 — had it been Danish local time, the fall-back hour would appear twice).

The Xellent UI displays both `Tidspunkt (UTC)` and `Tidspunkt (CET)` columns side-by-side; the CET column is computed for display only.

## Pipeline

1. SQL read: `TIMEOFVALUE` (UTC) → .NET `DateTime` with `Kind=Unspecified`
2. `TimeSeriesExtractionService` labels it `Kind=Utc` and wraps as `DateTimeOffset(value, TimeSpan.Zero)`
3. `JsonExportService` / `TimeSeriesRunner.BuildTimeSeriesRequest` writes the value to `SettlTimeSeriesPointDto.Time` (`DateTime Kind=Utc`)
4. `System.Text.Json` serialises with `Z` suffix → Settl receives ISO 8601 UTC

## No DST handling

Because storage is UTC, the spring-forward "invalid" hour and fall-back "ambiguous" hour are not concepts that apply — every UTC timestamp is unique and monotonic across the DST transitions.

Regression coverage: `DstPostPayloadTests` runs the full Xellent→Settl payload pipeline against AXDB50_TEST for both DST days (2024-03-31, 2024-10-27) and snapshot-compares the wire JSON against committed goldens. If a future change reintroduces a tz-conversion bug, those goldens will drift visibly.

## Historical note

Prior to 2026-05-20 this pipeline incorrectly assumed `TIMEOFVALUE` was Danish local time and ran a CET/CEST → UTC conversion (`TimeZoneNormalizationService`). That conversion shifted every POSTed timestamp 1–2 hours too early. Removed in commit fixing this issue.
