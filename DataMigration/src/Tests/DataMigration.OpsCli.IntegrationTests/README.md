# DataMigration.OpsCli.IntegrationTests

End-to-end tests of OpsCli's runners and readers. They run anywhere: no VPN, no `testsettings.json`, no user-secrets, no Xellent and no Settl.

```bash
dotnet test DataMigration/src/Tests/DataMigration.OpsCli.IntegrationTests/
```

## What stands in for the real systems

- **Settl** — `Fakes/FakeSettlApi.cs`, an in-memory tenant behind the real typed clients (`HttpMessageHandler`). It keeps what was written and returns it on the next read, so create → reconcile scan → mutation → reconcile-apply all see one consistent Settl. It mimics the asymmetries the code depends on, e.g. payer billing is written as `VatIdentificationNumber` and read back as `OrganizationIdentifier`.
- **Xellent** — `Fakes/FakeXellent.cs`, an SQLite in-memory database. Code that only takes a connection string reaches it through `XellentDbConnection.OverrideForTests` (in XellentData); in production that call is plain `UseSqlServer`.
- **Data** — the reconcile flows migrate account 448928 from its anonymised snapshot in `DataMigration.Migration.Tests/Fixtures/SeedData`. Everything else is synthetic rows built in the tests (`Fakes/XellentRows.cs`). No names, addresses or national ids.

## What these tests no longer prove

The Settl contract tests (`SettlApiContractTests`, `SettlPaginationContractTests`, `SettlLinkContractTests`, `SettlMpDetailContractTests`, `PayerBillingContractTests`, `BillingPaymentHistoryTests`) pin the response shapes our code relies on. They no longer check that live Settl still answers that way.

The Xellent tests run the real queries, but against SQLite, which compares strings case-sensitively and has no SQL Server collation.

## Goldens

`Goldens/report-meter-info/fixture-accounts.csv` was recorded from a live run. The fake data in `ReportMeterInfoRunnerTests` is built to the same facts, so the golden still applies. A drift writes a proposed baseline to `Goldens/**/new/` (gitignored); review it before promoting.
