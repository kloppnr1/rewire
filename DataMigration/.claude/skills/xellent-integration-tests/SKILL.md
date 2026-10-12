---
name: xellent-integration-tests
description: Runs the local Xellent live-DB regression suite in src/Tests/DataMigration.Xellent.IntegrationTests/ to lock extraction output bit-identical via golden JSON files. USE PROACTIVELY after any change to SettlExtractionService, CreateRunner, CustomerBundleMigrator, helpers under Services/Extraction/ or Services/MeteringPointStrategy/. NEVER run in CI — xel2012 is unreachable from build agents.
---

# Xellent Integration Tests (local only)

This suite diffs live-DB extraction output against committed JSON goldens. It is the
only regression gate that catches subtle drift against the real Xellent schema (types,
null-håndtering, join-order effects, date-tidszoner) — SQLite seed tests cannot.

## When to run proactively

Run this test whenever you change ANY of these paths:

- `src/DataMigration.Migration/Services/SettlExtractionService.cs` (and its partials
  `.Customers.cs`, `.Payers.cs`, `.MeteringPoints.cs`, `.Validation.cs` once the
  maintainability-refactor merges)
- `src/DataMigration.Migration/Services/Extraction/*` (helpers, parser, resolver, mappings)
- `src/DataMigration.Migration/Services/MeteringPointStrategy/*`
- `src/DataMigration.Migration/Runners/CreateRunner.cs`
- `src/DataMigration.Migration/Services/CustomerBundleMigrator.cs`,
  `ContractNameResolver.cs`, `CustomerOwnerResolver.cs`, `MigrationSummaryWriter.cs`
- Xellent entity types under `src/DataMigration.XellentData/Entities/` (schema-shape
  changes propagate through extraction)

Run **before** committing such changes so drift is caught early, not after a push.

## Run command

```bash
dotnet test src/Tests/DataMigration.Xellent.IntegrationTests/ --nologo --verbosity minimal
```

Expected output when goldens are in sync: `Passed: 3, Failed: 0` (3 profil-tests:
`match`, `ppa-customers`, `vand-sample` — suite omskrevet i commit `473660ac`).

## Handling failures

### Missing baseline

First run on a fresh clone (or after adding a new account) writes proposed baselines to
`Goldens/<account>/new/` and the test fails with `BaselineMissingException`.

Action: review each proposed JSON, copy into place, commit.

```bash
# Inspect proposal
cat src/Tests/DataMigration.Xellent.IntegrationTests/Goldens/466339/new/customer.json

# If acceptable, promote
mv src/Tests/DataMigration.Xellent.IntegrationTests/Goldens/466339/new/*.json \
   src/Tests/DataMigration.Xellent.IntegrationTests/Goldens/466339/
```

### Baseline mismatch

**Never auto-promote.** The test detected a behavioural delta. The `new/` file contains
what the changed code would produce.

1. Show the user the diff summary (the exception message lists up to 20 JSON paths)
2. Diff the `new/` file against the committed golden for full context
3. Ask the user whether the delta is intentional:
   - **Yes** → promote the specific changed goldens, commit alongside the code change
     with a clear message (e.g. "extract new SupplementaryName field — updated goldens
     for accounts X, Y, Z")
   - **No** → the code change introduced a regression; revert or fix before continuing

## Prerequisites

- Windows machine with Kerberos/Integrated-Security access to `xel2012`
- **Connected to the corporate VPN** — `xel2012` is internal-only
- `src/Tests/DataMigration.Xellent.IntegrationTests/testsettings.json` exists
  (gitignored; see `testsettings.example.json` for format)

If `testsettings.json` is missing, tell the user — don't attempt to create it with a
made-up connection string.

## Detect missing VPN

`xel2012` is on the corporate network. If the VPN is disconnected, every test fails
with a `SqlException` like *"Could not open a connection"*, *"named pipe instance
matching '' returned from host"*, or *"Netværksstien blev ikke fundet"*.

The `IntegrationTestBase` constructor probes the connection up-front and throws a
single clear error suggesting VPN — but Claude must also recognise this pattern
when running ad-hoc `sqlcmd` against `xel2012`. **Do not retry on a loop.** If the
first attempt fails with a network/named-pipe error, immediately tell the user:

> Kan ikke nå xel2012 — VPN er sandsynligvis ikke forbundet. Connect VPN og giv
> mig besked når du er klar, så fortsætter jeg.

Then stop and wait for the user.

## Not in CI

This test project is intentionally excluded from `DataMigration.sln`. Do NOT add it.
The Azure pipeline build agent has no route to `xel2012`; adding it to the sln would
break every build.
