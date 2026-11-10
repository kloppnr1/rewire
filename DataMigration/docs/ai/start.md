# DataMigration project knowledge

Use this page with the [workspace rules](../../../AGENTS.md). It records useful
Claude-era knowledge checked against current source on 2026-10-02. Follow current
source when older documentation disagrees; this page does not install tools or
guarantee that a hook runs.

## Structure and package ownership

The [migration CLI](../../src/DataMigration.Migration/Program.cs) and
[OpsCli](../../src/DataMigration.OpsCli/Program.cs) are hosts. Reusable runners live
in [Migration.Library](../../src/DataMigration.Migration.Library/DataMigration.Migration.Library.csproj)
and [OpsCli.Library](../../src/DataMigration.OpsCli.Library/DataMigration.OpsCli.Library.csproj),
which publish `NewSettlement.DataMigration.Migration` and
`NewSettlement.DataMigration.OpsCli`. Xellent domain/data and Settl clients come
from NuGet packages, not local copies. Dashboard.Api lives in MigrationDashboard;
MigrationOps and its tracking database are retired. Follow the workspace's CI
producer-to-consumer flow; do not create local package feeds for verification.

## Account identity and merging

A Xellent account is not necessarily a Settl customer number. Resolve it through
`AccountRepresentativeResolver` before customer lookup; searching a merged
member directly can falsely report that the customer has not migrated.

Current [resolver source](../../../NewSettlement.XellentDomain/src/DataMigration.Domain.Xellent/AccountRepresentativeResolver.cs)
merges transitively within a CVR when accounts share either a street fingerprint
or a legal-contact DAR address ID plus matching normalised names/email. Lowest
Accountnum becomes the representative. The
[fingerprint](../../../NewSettlement.XellentDomain/src/DataMigration.Domain.Xellent/FingerprintCalculator.cs)
uses name, additional name, primary customer street/postcode/city and email,
with accents, case, whitespace and selected punctuation normalised. Email affects
both merge paths; EAN does not. A shared DAR ID alone is insufficient.

`Extraction.LegacyTg4Merge=true` selects CVR + TechnicGroup4 instead. Missing CVR
and explicitly ignored groups remain separate; `disableMerge` makes every
account represent itself. Owner clusters are a separate layer above
representatives. See [representation history](../representation-principle.md),
whose earlier single-key and address-only explanations are historical.

### Resolver scale

Large SQL account filters previously exhausted the query planner. Keep
[caller portioning](../../src/DataMigration.OpsCli.Library/Services/RepresentativePortioning.cs)
on whole CVR classes, with a soft target of 15,000 accounts; never independently
resolve raw chunks that can split a merge group. An oversized CVR class stays
whole. The
[domain loader](../../src/DataMigration.OpsCli.Library/Services/XellentDomainLoader.cs)
and historical ownership enrichment in the
[state runner](../../src/DataMigration.OpsCli.Library/Runners/GenerateStateFromSettlRunner.cs)
use this approach.

Current XellentDomain source also reads account filters in
[2,000-row query portions](../../../NewSettlement.XellentDomain/src/DataMigration.Domain.Xellent/Internal/XellentQueryLimits.cs)
and groups after collecting all rows. These internal query chunks preserve
cross-chunk merges. The [old scale investigation](../representative-resolver-scale-limit.md)
still explains the failure, but its claim that the package has no protection is
outdated. The flat-reference-date caller still passes the whole account set;
do not assume its historical failure is fixed in a deployed package without
checking the actual restored/published version and a representative run.

## Migration state contract

`migration-state.json` is the migration-state truth; do not use
`migration-tracking.db`. Canonical DTOs live in
[OpsCli.Library/Data/MigrationState.cs](../../src/DataMigration.OpsCli.Library/Data/MigrationState.cs):
`MigrationStateDocument.Accounts` contains `MigratedAccount` entries with Settl
account number, source accounts, delivery categories, group key, earliest
change-of-supplier date and invoices. Each invoice carries its source account;
`PdfSourcePath` is excluded from JSON. Producers write camelCase JSON.

`generate-state` is deprecated. `generate-state-from-settl` uses Settl metering
points, first supplier changes and Xellent ownership evidence, including an
offline replay path. It is not a Settl-only command in every mode. Preserve the
wire contract and coordinate changes with PDF upload and CustomerSupport
consumers. See [invoice ownership comparison](../invoice-state-comparison.md).
The established production location is
`C:\Services\DataMigration\OpsCli\data\migration-state.json`; confirm the target
path before replacing a file.

## Write commands and previews

The current host registrations are the command inventory. OpsCli is not entirely
read-only. These commands can change Settl:

| Command | Non-writing preview | Important behaviour |
|---|---|---|
| `create` | `--dry-run` | Still reads Settl; `--offline` skips Settl calls but reads Xellent. `--mp-only` cannot combine with either preview flag. |
| `fix-settlement-groups` | `--dry-run` | Adds customers to existing settlement groups; exporting a CSV alone does not prevent writes. |
| `fix-payment-config` | `--dry-run` | Updates payer and metering-point payment configuration. |
| `fix-consumer2-duplicate` | `--dry-run` | Clears duplicate second-consumer name/identification fields. |
| `reconcile-apply` | `--dry-run` | Selected repairs can create, update or delete; PriceList output prepares a re-import file. Production is forced dry-run unless `--force-production-hot` is set; hot runs require a selector. |
| `apply-aborabat-repair` | Omit `--execute` | Production-only plan display by default. Execution requires `--reviewed-xlsx` and a new `--report` journal; adds and confirms target links before removing the old link. No `--dry-run` option. |

Start with featuretest previews where supported, then production previews and
source spot-checks. Hot verification uses featuretest. The production-only
AboRabat apply cannot be used for a featuretest hot smoke test. Every production
write requires explicit user escalation; a code flag or reviewed workbook alone
is not user permission. Do not assume older fix commands still exist. Offline
preparation/model/compare commands write local artifacts without changing Settl.
Consult the [OpsCli README](../../src/DataMigration.OpsCli/README.md), checking
examples against current Program.cs before running them.

## Verification and behaviour history

For code changes, run `dotnet build DataMigration.sln`, the unit projects, relevant
integration projects and a CLI smoke test in that order. The
[Migration unit project](../../src/Tests/DataMigration.Migration.Tests/DataMigration.Migration.Tests.csproj)
and [OpsCli unit project](../../src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj)
are in the solution. These integration projects are deliberately outside it:

- [Xellent integration tests](../../src/Tests/DataMigration.Xellent.IntegrationTests/DataMigration.Xellent.IntegrationTests.csproj)
  cover live extraction; see their [setup](../../src/Tests/DataMigration.Xellent.IntegrationTests/README.md).
- [OpsCli integration tests](../../src/Tests/DataMigration.OpsCli.IntegrationTests/DataMigration.OpsCli.IntegrationTests.csproj)
  cover live API/report/reconcile flows. Some provision or mutate featuretest;
  inspect current fixtures and configuration rather than relying on the old
  README's removed fix-runner inventory.

Live tests need corporate connectivity and local credentials. Never point
mutating tests at production. On a first connection failure suggesting VPN
loss, stop and report the likely cause; do not loop retries. Use `--help` or a
scoped preview for CLI checks, respecting each command's configuration needs.

For runtime behaviour changes in migration or OpsCli runners, add a newest-first
row to [CHANGELOG.md](../../CHANGELOG.md) with date, command, short behaviour change
and commit reference in the same user-authorised commit. Refactors without a
behaviour change, test-only and docs-only changes need no row. Do not commit or
amend automatically to fill in the reference.
