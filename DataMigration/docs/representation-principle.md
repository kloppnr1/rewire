# Representation principle

**The single most important concept in this repo.** Read this before touching
anything that takes a Xellent Accountnum and expects to find it in Settl.

## The rule

In `create`, customers with the same **CVR + Fingerprint** are merged into
one Settl customer. The member with the **lowest Accountnum** becomes the
*representative* — its Accountnum is the only one written to Settl as
`CustomerNumber`. All other group members are absorbed: their payers and MPs
hang under the representative's customer; their Accountnum exists nowhere
in Settl.

Source of truth: `FingerprintCalculator.cs` + `AccountRepresentativeResolver.cs`
in NuGet package `NewSettlement.XellentDomain.Domain.Xellent` (repo
`NewSettlement.XellentDomain`) — moved out of this repo. The resolver builds a
union-find over two edges: a street-fingerprint edge (`S|Cvr|fp`) and a
legal-contact DAR-id edge (`D|Cvr|nameKey|darId`). Same-CVR accounts merge
when they share *either* edge. Email is folded into the fingerprint on both
edges, so a differing contact email keeps accounts apart on both.

```
fp  = normalise(Name | AdditionalName | Street | Zip | City | Email)
key = (Cvr not empty) ? Cvr + "|" + fp : Accountnum
representative = group.OrderBy(Accountnum).First()
```

`normalise` = `string.Normalize(NFD)` (accent-fold) → drop non-spacing
marks → lowercase → strip the chars: space, tab, CR, LF, NBSP (U+00A0),
'/', '.', ',', '-', '&', '\'', '"', '(', ')'. Concrete consequence:
`"Verdo A/S"` and `"Verdo  A / S"` produce the same fingerprint and merge.

**Email/EAN history.** The fingerprint originally included Email and EAN.
Both were removed 2026-05-20; Email was re-introduced 2026-06-04. Today the
fingerprint includes Email (a differing contact email splits same-CVR
accounts into separate Settl customers) but **not** EAN (contract-level EAN
variation legitimately occurs under one legal entity). The simplified
`key = Cvr + "|" + fp` above is the historical single-key model; the live
merge is now a union of two edges (street-fingerprint OR legal-contact DAR
id) — Email participates in both. See source note below.

Fallback: if CVR is missing/empty, the account represents itself
(`key = Accountnum`). Person customers without CVR are always solo.

### Address fields come from the primary CustTable-address

The fingerprint pulls Street/Zip/City from
`DirPartyAddressRelationship.Isprimary=1` — i.e. the customer's primary
postal address. This is **not** the legal contact, even though the
create-pipeline otherwise uses legal contact as truth for the customer and
payer addresses written to Settl. Two accounts with the same legal contact
but different primary addresses split into separate Settl-customers.

### Legacy TG4-rule

Profiles that set `Extraction.LegacyTg4Merge=true` (PPA, Spar Nord, etc.)
fall back to the old `(CVR + TechnicGroup4)` rule via
`BuildLegacyTg4MergeKeyMapAsync`. Used where many branches under one TG4
must merge into a single Settl-customer despite varying addresses.

The `ValidateCvrGroupings` validator (gated post commit `deef4e6b`) runs
**only** in legacy-mode. In fingerprint-mode there is no separate
validator — fingerprint divergence is surfaced via the `FingerprintSplits`
diagnostic instead (see below).

## Why it matters for any Accountnum-driven code

A Xellent Accountnum is **not** a Settl CustomerNumber. For group members,
Settl has never heard of them. Calling
`SearchByCustomerNumberAsync(memberAccountnum)` returns null with no error —
looks like "not migrated yet", is actually "merged into the representative".

This has already produced a false-negative in a former
`FixElectronicNumberRunner` (production run 2026-04-15: 3 of 268 accounts
reported "not found in Settl" with empty error — all three were group
members, their MPs were silently skipped).

## Required pattern for Accountnum-driven runners

Use `Services/Extraction/AccountRepresentativeResolver.cs`. It is the
**canonical** translator: same fingerprint logic as
`BuildMergeKeyMapAsync`, exposes a `legacyTg4Merge` flag for profiles that
need it, and returns `(Accounts, MergedCount, NameByAccount)` — including
accounts that are not in Xellent (passed through untouched).

```csharp
var resolution = await AccountRepresentativeResolver.ResolveAsync(
    context, accounts, dataAreaId,
    legacyTg4Merge: profile.Extraction.LegacyTg4Merge,
    cancellationToken);
// resolution.Accounts → look these up in Settl
// resolution.MergedCount → log how many were absorbed into representatives
```

Do **not** call `SettlExtractionService.GetEffectiveGroupKey` from external
code. It is now a runtime lookup over the private `_mergeKeyByAccount`
dictionary that `BuildMergeKeyMapAsync` populates — calling it without
that setup returns the input accountnum unchanged (silent
mis-translation).

When logging, distinguish "non-representative member" from "not in Settl".
The MPs/payers you want are under the representative; chasing the member
Accountnum is a dead end.

### There is a size limit on one resolver call

Above roughly 30.000 accounts in a single `ResolveAsync` call, SQL Server
stops being able to produce a query plan at all and the run dies in seconds
with *"the query processor ran out of internal resources"*. The account list
is inlined as literals (Xellent is at compatibility level 110, so EF cannot
use `OPENJSON`), so the statement grows with the count.

A caller above that size must split the work into **whole CVR classes** —
never raw account chunks, which would split a merge group and silently change
the representative for its members. `XellentDomainLoader.ResolveRepresentativesAsync`
and `GenerateStateFromSettlRunner` already do this;
`--flat-reference-date` does not. Measured ceiling, the full list of guarded
and unguarded callers, and the rules for writing a new guard:
**`docs/representative-resolver-scale-limit.md`**.

## Diagnostics: FingerprintSplits

`SettlExtractionService.FingerprintSplits` exposes the inverse signal:
CVRs that **split** into ≥2 Settl-customers because their accounts
disagree on the fingerprint fields. Each entry lists the variations
(name/address/email clusters) and which accountnums fall under each.
Used by report-runners to flag data-quality issues — same legal entity,
inconsistent customer master data in Xellent, ends up as multiple Settl
customers.

## OwnerCluster (separate concept)

Introduced 2026-04-29 (`e49c5780`). When ≥2 representatives share the
same `ExuTechnicgroup4`, a synthetic owner-customer is created and the
representatives are linked under it. Cluster key is the TG4 value; owner
name is the TG4's `GROUPNAME` (fallback: TG4 itself). CVR can vary across
cluster members — the owner-customer's CVR is copied from the lowest
member.

This is **not** the merge rule. It is a layer above representatives that
gives Settl a parent customer for groups of related companies. Reps
without a TG4 are not eligible. After a brief fingerprint-split-default
phase (`de863570`), `OwnerCluster` was reverted to TG4-only (`5f528b4e`,
`4464baf7`) — current state.

Source: `Services/Extraction/OwnerCluster.cs` +
`SettlExtractionService.BuildOwnerClusters`.

## Ignored groups

Some TechnicGroup4 values must **neither merge nor form an OwnerCluster**.
Accounts in an ignored TG4 are treated as ungrouped — each migrates
standalone (its own Settl customer), regardless of CVR+Fingerprint match.
They are still migrated; nothing is dropped from the run.

**The list is a fact about the data area, not about a profile.** `Medarbej`
is Verdo's own CVR: without the list every employee account merges into one
Settl customer, no matter which profile is running. So the shared list lives
in the appsettings section `Extraction:IgnoredGroups` (today: `Medarbej`,
`Hybel`, `Velk strøm`) and reaches every run — including `create --all-active`
without a profile, and env-wide `reconcile`, which has no profile at all.

A profile's own `Extraction.IgnoredGroups` (and a reconcile mapping's) is
**added on top of** the base, never replacing it. Replacement would let a
profile silently reopen merging for employee accounts, and that only shows up
as wrong customers in Settl; the union can only fail the other way, where
accounts stand alone and are visible in the report.

Wiring: `IgnoredGroupsLoader.Load(config)` → `CreateOptionsFactory` (union
with the profile / reconcile mapping) → `CreateOptions.IgnoredGroups` →
`MapperOptions.IgnoredGroupNumbers` (consumed by
`AccountRepresentativeResolver` in both fingerprint and legacy paths, and by
`XellentDomainMapper.BuildOwnerClustersAsync`). Owner-cluster suppression on
the create path also runs explicitly in this repo via
`SettlMappingService.BuildOwnerClusters(groupings, ignoredGroups)` — merge
exclusion alone is not enough, because an ignored account still carries its
TG4 and would otherwise re-cluster.

Read-only OpsCli commands get the list from `XellentDomainLoader`, which
applies it to every `MapperOptions` it is handed (and to its representative
lookup). That covers `report`, `compare` and the rest in one place, so a
report cannot show merge groups that `create` would not produce. Five runners
build their own Xellent layer outside the loader and are wired individually:
`generate-state`, `generate-state-from-settl`, `report-drift`,
`report-payer-diff`, `report-meter-history`.

Before 2026-06-08 this was dead config: the JSON key had no binding here and
`IgnoredGroupNumbers` was declared-but-unconsumed in the package. Until
2026-08-11 the list existed only in the `pilot-production` profile, so every
other run merged employee accounts.

## What `Create sets it?` does *not* guarantee

`docs/settl-update-map.md` tells you whether the *field* is create-owned.
It does not tell you the account-to-customer mapping. A runner that needs
to reach an MP field still needs to reach the MP via the representative —
regardless of what column the field lives under.
