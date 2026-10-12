---
name: post-migration-fix
description: Assists authoring or running post-migration fix-* commands against Settl. Use when the user reports a field is missing or wrong in Settl after migration ("feltet X kom ikke over", "X er forkert i Settl", "fix X in Settl", "fix the currency on account Y", "blank the EAN on metering points"). Consults docs/settl-update-map.md as the primary lookup (Xellent↔Settl↔repo lineage) and routes to RUN / SCAFFOLD / STOP.
---

# Post-migration fix skill

Assists — does not autopilot. Every mutation confirmed with the user first.

## Invariant: Accountnum ≠ Settl CustomerNumber

Before any Settl lookup, **translate Xellent Accountnum to its group
representative**. Customers sharing CVR + TechnicGroup4 merge in `create` —
only the lowest Accountnum reaches Settl as `CustomerNumber`. Searching Settl
with a member Accountnum returns null silently (looks like "not migrated",
is actually "merged"). See `docs/representation-principle.md` and the top
of `CLAUDE.md`.

Every scaffolded fix-* runner that accepts `--accounts` or a profile must
resolve representatives via `GetEffectiveGroupKey` before the Settl call.
If the runner currently does not, flag it as a bug — not a feature gap.

## Trigger phrases

Danish: *"X mangler i Settl efter migrering"*, *"feltet X kom ikke over"*,
*"X er forkert på konto Y"*, *"fix X på Y"*, *"X skal blankes"*.

English: *"field X missing in Settl"*, *"fix X for account Y"*, *"clear X"*.

## Workflow

### 0. Scope-lock (before any lookup)

Identify the entity from the user's prompt and **lock lookup to that entity's
section of the map**. Never widen.

| User says | Locked section in `docs/settl-update-map.md` |
|---|---|
| *"målepunkt"*, *"MP"*, *"metering point"* | `## MeteringPoint`, `## BillingConfiguration`, `## PaymentConfiguration`, `## InvoiceConfiguration`, `## SettlementInfo` |
| *"kunde"*, *"customer"* | `## Customer` (when added) |
| *"betaler"*, *"payer"* | `## Payer` (when added) |
| *"kontrakt"*, *"contract"* | `## Contract` (when added) |

Hard rule: when user says *"målepunkt"*, do not propose a fix that touches
Customer/Payer/Contract — even if the same field name (e.g. `ElectronicNumber`)
appears there too. If the field isn't in the locked section, STOP and tell the
user plainly; do not widen without their OK.

### 1. Look up in the map

Primary source: `docs/settl-update-map.md`. Check:

1. **Direct hit.** The field appears under the locked entity's section.
2. **Synonym expand.** If user used slang, consult the frontmatter `synonyms:`
   block (EAN → ElectronicNumber, CVR → VatIdentificationNumber, etc.) and
   retry the lookup.
3. **Swagger fallback.** Only if the synonym expansion still misses. Grep
   `docs/billing_internal_api.json` + `docs/market_internal_api.json` for the
   canonical name, **restricted to the locked-entity schemas**. A miss here
   means the field isn't writable anywhere → STOP.

If the map's `last-reviewed:` date is >90 days old, tell the user up front
the map may be stale and the swagger should be verified.

### 2. Diagnose Cause A vs Cause B from the map

The map's `Create sets it?` column makes this a lookup, not a conversation.

- **`Create sets it?` = fil:line** + user reports value is wrong → **Cause A**.
  DM's create-path wrote a wrong value. Fix the extraction/create logic so the
  next migration doesn't repeat. fix-\* on existing data is only a patch.
- **`Create sets it?` = no** + user reports a value is present → **Cause B**.
  Settl/external process set it post-create. fix-\* is the right path.
- **`Create sets it?` = fil:line** + user wants to *blank* it → **Cause A**.
  Change the extraction gate so create stops populating the field, then
  fix-\* on already-migrated accounts.

Surface the diagnosis to the user before asking for action confirmation.

### 3. Route to action (from map's `Fix action` column)

| Column value | Skill does |
|---|---|
| **RUN fix-\<name>** | Confirm profile or `--accounts` scope. Always dry-run first. Show counters. Confirm before live-run. |
| **SCAFFOLD** | DTO exists, no runner yet. Scaffold runner + CLI block + integration test class + map-row flip. Conservative output (see "Scaffold fidelity" below). |
| **SCAFFOLD (DTO gap)** | DTO missing, endpoint writable. Three-commit plan: (1) add DTO fields + wire to request schema, verify existing tests green; (2) scaffold runner + CLI; (3) integration tests + flip map row. Default to starting with (1) immediately. |
| **STOP** | Cite reason (primary key, no write endpoint, etc.). Point the user at the specific Settl swagger schema so they can verify — don't leave them guessing. |

### 4. Ask only when truly ambiguous

- **Bulk scope** — user says *"alle / samtlige / every / all"*: refuse to run
  without explicit `--profile` or `--accounts`. Ask *"Which profile (match,
  ppa-customers, …) or explicit accountnum list?"*.
- **Profile ambiguity** when running against a specific account: *"Which
  profile produced this account's original migration?"* — expected values
  depend on profile mappings.
- **`--allow-clear` semantics** when user wants to remove a value: confirm
  whether Xellent source is also empty (honest blank) or they're intentionally
  overriding Settl regardless of Xellent (force-clear). Default to honest-only.
- **Live-run confirmation** — never mutate without explicit "yes".

### 5. After a run

- Show report summary.
- Cause A: remind user to file the DM-side fix so the next migration doesn't
  repeat.
- If integration tests cover the runner, prompt the user to run them:
  `dotnet test src/Tests/DataMigration.OpsCli.IntegrationTests/ --filter FixXRunner`.

## Business rules — never re-derive

Scaffolded runners inherit these from `SettlExtractionService` via
`Extract*` calls — **never** hand-roll:

- Representative customer resolution (`ExuTechnicgroup4 + ExuCvrDisponent1`)
- Child-MP exclusion (`EXU_DELPOINT2DELPOINTRELA25505`)
- ContractPart supersession (latest `Startdate ≤ cutoff`)
- End-date sentinels (`1900-01-01` / `2154-12-31`)
- `includeEndedCutoff = today.AddYears(-1)`
- Joint-invoice main-contract resolution

## Scaffold fidelity

Conservative defaults — include only what makes integration tests pass.
Opt-in features require the user to ask:

| Template feature | Default? | Opt-in prompt |
|---|---|---|
| Core run-loop, extraction call, report counters, dry-run branch | ✓ | — |
| `--verbose` flag + per-MP skip logging | skipped | *"Verbose logging?"* |
| `Label(acc)` customer-name lookup in logs | skipped | *"Customer names in logs?"* |
| Per-record `try/catch` | skipped | *"Per-record fault-tolerance?"* |
| `--data-area` override | skipped | *"Non-`hol` support?"* |

Template priority (read from disk at scaffold time): `FixJointRunner.cs` →
`FixGridRunner.cs` → `FixBillingRunner.cs`. Fall through if the first doesn't
exist (bootstrap case).

## What scaffold output looks like

For a SCAFFOLD row (e.g. `PaymentConfiguration.ElectronicNumber`):

1. DTO extension — new `MeteringPointPaymentConfigurationDto` + wire property
   onto `UpdateBillingMeteringPointRequest`.
2. `src/DataMigration.OpsCli/Runners/Fix<Name>Runner.cs` — FixJoint shape.
3. `src/DataMigration.OpsCli/Program.cs` — new CLI command block.
4. `src/Tests/DataMigration.OpsCli.IntegrationTests/Fix<Name>RunnerIntegrationTests.cs`
   — 3 tests: `UnknownAccount_NotInSettl_NoMutation`,
   `HappyPath_FieldMutated_FixRestoresExpected`, `SecondRun_IsIdempotent`.
5. `docs/settl-update-map.md` — flip the row from SCAFFOLD → RUN fix-\<name>.

## When the skill should NOT trigger

- Xellent-side data corrections → Dynamics AX workflow, not fix-\*.
- Create-path bugs where no migration has run → change `CreateRunner` /
  `SettlExtractionService` directly.

## Related

- `docs/settl-update-map.md` — primary lookup.
- `.claude/skills/settl-api-query/SKILL.md` — read-only Settl queries for
  investigating current state.
- `.claude/skills/xellent-integration-tests/SKILL.md` — Xel2012 regression
  gate scaffolded runners must pass.
- `docs/post-migration-fixes.md` — human-facing runner docs.
