# TimeSeries.Xellent.IntegrationTests

End-to-end tests of `TimeSeriesExtractionService`: extract from Xellent, build the Settl POST payload for
`/v1/time-series/{guid}/points`, and compare the wire JSON with a committed golden.

Runs anywhere. Xellent is a SQLite in-memory copy, seeded by `XellentTimeSeriesSeeder` from
`TimeSeries.Cli.Tests`. No VPN, no `testsettings.json`.

```powershell
dotnet test TimeSeriesMigration/src/Tests/TimeSeries.Xellent.IntegrationTests
```

## What is tested

- **DST** (`DstPostPayloadTests`): metering point `571313115242231955`, El-ekstern. Spring forward
  2024-03-31 (23 hours) and fall back 2024-10-27 (25 hours). Points are sent in UTC as stored.
- **Resolution switch** (`ResolutionSegmentPayloadTests`): metering point `571313103103321037`. An Hour
  series under one company until 31 Dec 2023 and a Quarter series under another from 1 Jan 2024. One
  segment per resolution, each with its own payload; counts from `CountTimeSeriesAsync` agree with the
  extraction; the five-decimal split check agrees with the database's own remainder.

The seed is the golden's own points, which were recorded from AXDB50_TEST, plus one point just outside the
window on each side. A wrong window bound shows up as golden drift. `TransactionId`, `RegistrationTime`
and `Revision` are fixed in the tests.

Numbers compare by value: the goldens keep SQL Server's twelve decimals (`0.120000000000`), SQLite
returns `0.12`, and Settl reads both as the same number.

## When a golden drifts

The failing test writes the new output to a `new/` folder next to the golden. Diff it against the
committed file. If the change is intended, copy it over the golden and commit code and golden together.
