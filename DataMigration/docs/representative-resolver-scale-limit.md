# The representative resolver's query-plan ceiling

> **The query processor ran out of internal resources and could not produce a query plan.
> This is a rare event and only expected for extremely complex queries or queries that
> reference a very large number of tables or partitions. Please simplify the query.**

If a run died with that message, this is the page. The cause is almost always one call
that handed a whole population's account list to `AccountRepresentativeResolver`.

**It is plan compilation, not a timeout.** It fails in seconds, long before any command
timeout, and no amount of waiting or `CommandTimeout` helps. A run that makes steady
progress for fifteen minutes and then throws this in under twenty seconds is showing you
exactly this failure.

## Why it happens

`AccountRepresentativeResolver.ResolveAsync` (NuGet package
`XellentDomain.Domain.Xellent`) runs two statements per call:

1. `CustTable` plus three left joins through the party-address relationship tables to the
   address table, for the street fingerprint.
2. `FetchLegalContactAddressIdsAsync` — a five-table inner join
   contract → contract part → agreement → delpoint → delpoint contact info, for the
   legal-contact DAR id.

Both filter on `accounts.Contains(...)`. Xellent (`xel2012`/`AXDB50`) is SQL Server 2012
at **compatibility level 110**, and every DbContext in this repo sets
`UseCompatibilityLevel(110)` to match. Below compatibility level 130 EF Core cannot use
`OPENJSON`, so it falls back to writing every account as a **literal in the `IN` list**.
The statement text therefore grows with the account count until the optimiser gives up.

Two consequences worth holding on to:

- It is **not** the 2100-parameter limit. The accounts are constants, not parameters, so
  the familiar "too many parameters" error never appears — you get the planner error
  instead, at a much higher count.
- Dropping `UseCompatibilityLevel(110)` is **not** a fix. The server really is SQL Server
  2012 (11.0), so `OPENJSON` does not exist there. The setting is correct.

## The measured ceiling

Measured 2026-09-02 against production `AXDB50`, running the resolver's two real
statements with a literal `IN` list of that many accounts:

| accounts | CustTable + 3 left joins | legal-contact 5-table join |
|---|---|---|
| 15.000 | ok (2,3 s) | ok (4,2 s) |
| 20.000 | ok (3,8 s) | ok (5,6 s) |
| 30.000 | ok (5,6 s) | ok (8,4 s) |
| 35.000 | ok (6,9 s) | **fails** |
| 45.000 | **fails** | **fails** |

The five-table legal-contact join gives up first. **Treat ~30.000 accounts as the last
count known to plan, and anything above it as unsafe.** The exact ceiling is
data-dependent — it moves with the join fan-out — so do not tune close to it.

Two independent runs bracket the same wall from the other side:

- `generate-state-from-settl`, 2026-08-31: a 13.343-account call planned; the call that
  died held at least 41.600.
- `export-settlement-config --env production`, 2026-09-02: the full El-ekstern population
  needed **44.946** accounts and failed. The same command with `--limit 25000` needed only
  **19.891** and passed — which is why the failure looked intermittent, and why it looked
  at first as though it belonged to the large 2026-09-01 cohort. It belongs to no cohort.
  It belongs to the account count.

`PortionSize` is **15000** in the remaining guard: above the 13.343 proven
to plan, and roughly half the lowest count observed to fail. The COS-driven
`generate-state-from-settl` path resolves direct source accounts through metering-point
IDs. It also calls `AccountRepresentativeResolver` in bounded CVR portions to find
older merge-group members whose contracts ended before COS−1; ambiguous historical
ownership is reported and never assigned by first-claim order.

## Who is guarded and who is not

The unplannable query lives in **XellentDomain**. Every guard against it is a private
helper in a **downstream caller**. There is no protection in the package itself, so a new
caller rediscovers the limit in production.

**Guarded:**

| Caller | Guard |
|---|---|
| `XellentDomainLoader.ResolveRepresentativesAsync` — covers `export-settlement-config` and `report-meter-info` | Splits a list above 15000 into CVR-class portions via `RepresentativePortioning` and `CosStatePlanner.BuildCvrPortions`, then merges the per-portion resolutions. A list at or below 15000 takes exactly the call it always took. With `disableMerge: true` the portioning is skipped altogether — nothing merges, so no group can be split, and the resolver's no-merge path is a single `CustTable` read that chunks its own account filter. `export-settlement-config --disable-representative-merge` takes that route. |

**Not guarded:**

| Caller | Exposure |
|---|---|
| `GenerateStateFromSettlRunner.EnrichWithInvoicesAsync` — the `--flat-reference-date` path | Loads **every** hol account from `CustTable` (415.683 as of 2026-09-02) and passes the array straight to `AccountRepresentativeResolver.ResolveAsync`, bypassing `XellentDomainLoader`. Ten times past the ceiling. This path will fail on production. |

On that unguarded path, portioning is probably the wrong fix. The resolver already
documents `accounts: null` as the idiom for "every account in the data area", and null
keeps the filter out of the SQL entirely — no `IN` list, no plan problem. But it is not a
free swap: with `accounts == null` the resolver reports an empty `notInXellent`, so
`Accounts` no longer carries the requested-but-missing accounts, and any caller leaning on
that changes behaviour. Verify before switching.

## The invariant for whoever writes the next guard

**Portion on whole CVR classes. Never on raw accounts.**

Both of the resolver's union edge keys are CVR-prefixed (`S|<cvr>|<fingerprint>` and
`D|<cvr>|<nameKey>|<darId>`) and union is only ever applied inside a single edge bucket.
A merge group therefore can never span two CVR classes — which is what makes cutting on
class boundaries answer-preserving, and what makes cutting anywhere else wrong.

Chunk on raw accounts and a merge group gets split across two calls. That turns **one
Settl customer into two**, and if the split takes the lowest-numbered member away, the
rest silently get a **different representative** — and a Settl lookup on a
non-representative account number returns null rather than an error. See
`docs/representation-principle.md`.

Three rules follow from it:

- **A CVR class larger than the portion size is resolved whole**, over the cap, and said
  out loud in the log. Correctness beats the budget.
- **Blank CVR is `IsNullOrEmpty`, not `IsNullOrWhiteSpace`.** A spaces-only CVR still
  forms edges in the resolver, so two such accounts can merge with each other and must
  travel in one portion. `CosStatePlanner.PortionClassKey` mirrors this deliberately.
- **Do not widen the account set unless the caller's population is a subset.**
  `BuildCvrPortions` can pull in every sibling account carrying a seed's CVR
  (`accountsByCvr`), which `generate-state-from-settl` needs because its COS slices are
  subsets of the population. A caller that already passes one flat list must pass an
  **empty** sibling map — widening the set would let an account merge with a sibling
  outside the population and hand it a lower representative than a single call would have.

Merging the per-portion resolutions is exact for the same reason: the portions partition
the account list, no group spans two of them, so every per-account map has disjoint keys
and every count is additive (`RepresentativePortioning.Merge`).

## Water (Vand) and Heating (Varme)

**This will come back.** Any run whose account count passes ~30.000 hits it, whatever the
delivery category. El-ekstern was simply the first population large enough.

When a Vand or Varme run dies with the planner error:

1. Look for **the call that hands a whole population to the representative resolver**.
   That is the failure, essentially every time.
2. **Do NOT lower `MeteringPointAccountResolver.BatchSize`.** It is 1500, it was blamed
   first when this was diagnosed, and it is fine — 1500 constants plan comfortably. Its
   batching has nothing to do with this ceiling, and shrinking it only makes the run
   slower while the real query still fails.
3. Check whether the failing caller goes through `XellentDomainLoader`. If it does, it is
   already guarded and something else is wrong. If it builds its own Xellent layer — as
   `generate-state`, `generate-state-from-settl`, `report-drift`, `report-payer-diff` and
   `report-meter-history` all do — it needs its own guard, built on the invariant above.

The water/installation path (`ResolveWaterAsync`, and the installation-based categories in
`MeteringPointStrategyFactory`) reaches accounts through `Exu_ContractPartTable.Instagreenum`
rather than through delpoints, but the **representative resolution is shared**:
`AccountRepresentativeResolver` is category-agnostic and always runs the same two
statements. A water population of the same size fails identically.
