---
last-reviewed: 2026-09-01
owner: DataMigration maintainer
status: implemented for the combinations confirmed as of 2026-08-27; exact Settl names and per-metering-point scope since 2026-09-01 — see "Code status" below
---

# Charge account classification (subscription fee + owned/rented)

Goal: classify every account with an EV-charger contract into one of four
subscription-fee buckets, plus a separate owned/rented axis. Everything is
expressed via `PRODUCTNUM` on `EXU_CONTRACTPARTTABLE`.

## Population

5,919 distinct Xellent accounts (AXDB50 production) have at least one contract
part with `DELIVERYCATEGORY` in (`Charge`, `Charge_Abo`).

**Important:** `Charge`/`Charge_Abo` is a `DELIVERYCATEGORY` value on
`EXU_CONTRACTPARTTABLE` (the contract *part*), not on the parent
`EXU_CONTRACTTABLE`. There is no `Charge` delivery category at the contract
level.

## The four buckets (subscription fee)

The `Charge` vs `Charge_Abo` split on the contract part **is** the
subscription-fee signal for the charger side:

- `Charge` — usage/consumption lines only (kWh charged, refunds, guest/employee
  usage). No subscription fee.
- `Charge_Abo` — subscription line present ("Abo" = abonnement). Subscription
  fee charged.

For El-ekstern, the same axis reads off the productnum suffix:

- `...UA` suffix (`V LadVarUA`, `V Kvt UA`, `V Vari UA`) = uden abonnement,
  no subscription fee.
- No `UA` suffix (`V LadVariP`, `V Kvartal`, `V Variabel`) = subscription fee
  charged.

Combine the two axes into the four buckets:

| Bucket | Charge (charger) | El-ekstern |
|---|---|---|
| 1 | no fee (`Charge` only) | no fee (`...UA`) |
| 2 | no fee (`Charge` only) | fee (no `UA`) |
| 3 | fee (`Charge_Abo`) | no fee (`...UA`) |
| 4 | fee (`Charge_Abo`) | fee (no `UA`) |

**Exclusion before bucketing:** refund/tax-adjustment lines under `Charge`
(`OReElafgUm`, `ORefuElafg`, `Refu.elafg`, `Refusion` — el-afgift refusion,
~5,000 contract-part rows) do **not** count as charger presence. An account
whose only `Charge`-category lines are these codes is treated as having no
charge line at all for bucketing purposes. Confirmed with domain owner
2026-08-24.

## Owned vs. rented

Read off the `Charge_Abo` productnum, which encodes two flags:
`[owner-flag][el-flag]` — confirmed pattern, domain owner 2026-08-24:

- `Eje` = owned (kunden ejer laderen)
- `Lej`/`Leje` = rented (kunden lejer laderen)
- `MEL` = med el (electricity cost bundled into the subscription)
- `UEL` = uden el (electricity billed separately, i.e. via the El-ekstern
  contract)

Confirmed codes:

| Productnum | Description | Owned/Rented | El bundled? |
|---|---|---|---|
| `OAboEjeMEL` | Eje Ladeabo med el | Owned | Med el |
| `OAboLejMEL` | Leje Ladeabo med el | Rented | Med el |

By the same convention (not yet individually confirmed for contract creation —
inferred from the pattern, treat as provisional there):

| Productnum | Owned/Rented | El bundled? |
|---|---|---|
| `OAboEjeUEL` | Owned | Uden el |
| `OAboLejUEL` | Rented | Uden el |
| `Gl.CLejMEL` / `NY.CLejMEL` | Rented | Med el |
| `Gl.CLejUEL` | Rented | Uden el |

For settlement-config customer segments, the business confirmed on 2026-09-22
that `OAboEjeUEL`, `O HCEjeUL3`, `O HCEjeUL4`, and `O HCEjeUL5` mean **Eje**.
The segment export now accepts those exact `Charge_Abo` codes at COS-1. This
does not establish which Settl charger contract to create for them.

On 2026-10-01, the business confirmed `O HCEjeUL3`, `O HCEjeUL4`, and
`O HCEjeUL5` map to `HomeChargeLadeEjeMel(O HCEjeMEL)` for both
`V LadVariP` and `V LadVarUA`. UA periods require manual follow-up after import;
the 35-MP evidence identifies the relevant MPs and original periods. This does
not change the separate multi-installation rules. Other HomeCharge UL codes
still require an explicit confirmed lookup; ownership alone is insufficient.

## Settl contract resolution table — for review by Janni Grundvad et al.

Cross-references every known El-ekstern productnum × charger `Charge_Abo`
productnum combination against the actual Settl contract name, sourced from
`Contract _ Settl.csv` (exported 2026-08-24), and **verified against live
Xellent data** (AXDB50 production, 2026-08-24 — row counts and example
accounts below). This table is the spec the `create` pipeline will implement
against — please confirm or correct every row, including the `Needs
confirmation` ones, before implementation starts.

**Correction from live-data verification:** `O HCEjeMEL`/`O HCLejMEL` (with an
`E` before the final `L`) do not exist in Xellent at all — 0 contract-part
rows. Only `O HCEjeML`/`O HCLejML` are real. `MigratableChargeProducts` in
Dashboard.Api already has this right. The Settl contract names
(`HomeChargeLadeEjeMel(O HCEjeMEL)`/`HomeChargeLadeLejeMel(O HCLejMEL)`) use
"Mel" as a readable word and spell the productnum with the extra `E` in
their parenthesised suffix — that suffix is Settl's own spelling, part of the
exact `ContractName`, not the Xellent productnum. Not a genuine ambiguity,
corrected below.

**Table structure note:** one row per Charge_Abo productnum, not per
El-ekstern pairing — "Live rows" is the total count of live contract parts
for that productnum in Xellent (both El-ekstern pairings combined), and the
two example-account columns show what was actually found paired with each
El-ekstern side. An earlier version of this table split the total count
across the two El-ekstern rows, which wrongly implied some pairings had zero
live accounts when real examples existed — corrected here.

| Charge_Abo productnum | Owned/Rented | Live rows (productnum total) | Resolved contract — `V LadVariP` (fee) | Examples (fee side) | Resolved contract — `V LadVarUA` (no fee) | Examples (no-fee side) | Status |
|---|---|---|---|---|---|---|---|
| `OAboEjeMEL` | Owned | 2,656 | `LadeAbo_Eje_Variabel` | `423275` | `LadeAbo_Eje_VariabelUA` | `505610`, `462976`, `464213` | Confirmed |
| `OAboLejMEL` | Rented | 372 | `LadeAbo_Leje_Variabel` | `473766`, `493129`, `489711` | `LadeAbo_Leje_VariabelUA` | `475341`, `475343`, `474690` | Confirmed |
| `OAboEje0` | Owned, 0-kr | 243 | `LadeAbo_Eje0kr_Variabel` | `419726`, `478917`, `483341` | `LadeAbo_Eje0kr_VariabelUA` | `393918`, `501490`, `521767` | Confirmed |
| `Gl.CLejMEL` | Rented | 64 | `Gl.LadeChargeLejeMel(Gl.ClejMEL)` | `420108` | `Gl.LadeChargeLejeMel_UA(Gl.ClejMEL)` | `434355`, `363830` (only 2 of 64 live rows pair with the no-fee side) | **Needs confirmation** — does the contract vary by El-ekstern fee status, or is `Gl.LadeChargeLejeMel` used regardless? Both sides have real accounts, so this isn't hypothetical |
| `NY.CLejMEL` | Rented | 8 | `Ny.LadeChargeLejeMel(NY.ClejMEL)` | `223061`, `104480`, `417994` | `Ny.LadeChargeLejeMel(NY.ClejMEL)` | `331593` | Needs confirmation — same question as `Gl.CLejMEL` |
| `O HCEjeML` | Owned | 9 | `HomeChargeLadeEjeMel(O HCEjeMEL)` | `501531`, `499602`, `491923` | `HomeChargeLadeEjeMel(O HCEjeMEL)` | **none — 0 of 9 live rows pair with `V LadVarUA`** | Needs confirmation — is the no-fee pairing simply absent from current data, or structurally impossible? Ask Janni |
| `O HCLejML` | Rented | 1 | `HomeChargeLadeLejeMel(O HCLejMEL)` | `499593` | — | *(n/a — only 1 live row total, already counted on the fee side)* | Needs confirmation — single data point |
| `OAboEjeUEL` | Owned | 346 | — | `430387`, `478133`, `521193` | — | `505610`, `488878` | Needs confirmation — no matching Settl contract found in the export, despite real accounts on both sides |
| `OAboLejUEL` | Rented | 6 | — | **Zero real examples** | — | **Zero real examples** | **No real pairing exists at all** — all 6 live-row accounts (`426737`,`477798`,`484353`,`472930`,`454938`,`512888`) have no live El-ekstern contract part on the same account, fee or no-fee side. Possibly moot for this feature |
| `Gl.CLejUEL` | Rented | 36 | — | **Zero real examples** | — | **Zero real examples** | **No real pairing exists at all** — none of the 36 live-row accounts has a live El-ekstern contract part, either side. Same situation as `OAboLejUEL` |
| `OAbLejBero`, `ChaBeroAbo`, `ChaXtraA`, `ChaEjSeAfg`, `ChaLejeSer` and other low-volume codes | Unknown | *(not counted)* | — | *(none pulled)* | — | *(none pulled)* | Needs confirmation — not investigated, low volume |
| *(no `Charge_Abo` part at all — consumption only, or no charger)* | n/a | — | *(existing resolution, unchanged)* | — | *(existing resolution, unchanged)* | — | Confirmed — out of scope, no new rule needed |

**Note on `OAboLejUEL`/`Gl.CLejUEL`:** both charger products are live and in
use in Xellent, but on accounts with no live electricity delivery in `hol`
at all — not a query gap, confirmed twice with different filters. If that
holds, these two rows may not need a resolution rule at all; worth
confirming with Janni whether that's expected (e.g. these customers left
electricity supply but kept the charger) before deciding.

## Open questions (blocking a full mapping — do not build code against this doc until resolved)

1. **`OAboEje0`** (401 contracts) = "Ladeabonnement 0 kr." — **resolved
   2026-08-24** via the actual Settl contract list (`Contract _ Settl.csv`):
   it is neither bucket 1/2 nor a plain bucket-3/4 fee — it gets its own
   dedicated Settl contracts, `LadeAbo_Eje0kr_Variabel` /
   `LadeAbo_Eje0kr_VariabelUA` (owned, 0-kr, split by the El-ekstern fee
   side same as everything else).
2. **`O HC*` explicit mapping table** — not yet supplied by domain owner.
3. The remaining `OAboEje*`/`OAboLej*`/`O HC*` productnums beyond the two
   confirmed above are provisional (pattern-inferred), not individually
   verified. One naming claim was already retracted once during this
   investigation (see chat 2026-08-24) — do not treat the pattern as
   trustworthy until more codes are confirmed or a canonical source is found.
4. **Scope exclusion:** `V_Con*` (Verdo Connect) and `V_Empl*` (Verdo
   Employee charging) productnums are explicitly **out of scope** — separate
   service lines, not customer owned/rented chargers. Confirmed 2026-08-24.
   Filter these out by prefix *before* classification so they don't show up
   as unrecognized/error rows.

## Code status (2026-09-01)

`ElectricityChargerContractMap.ConfirmedChargeAboProducts`
(`src/DataMigration.Migration.Library/Configuration/ElectricityChargerContractMap.cs`) now allows
these Charge_Abo productnums (plus `__NoCharge__`) through the charger-derived naming rule, matching
`appsettings.json`'s `ElectricityChargerContractMap`:

- `OAboEjeMEL`, `OAboLejMEL` (owned/rented, with el bundled)
- `OAboEje0` (owned, 0-kr) → `LadeAbo_Eje0kr_Variabel` / `LadeAbo_Eje0kr_VariabelUA`
- `Gl.CLejMEL` → `Gl.LadeChargeLejeMel(Gl.ClejMEL)` / `Gl.LadeChargeLejeMel_UA(Gl.ClejMEL)`
- `NY.CLejMEL` → `Ny.LadeChargeLejeMel(NY.ClejMEL)` (same contract on both el-ekstern sides — only 1
  real no-fee data point, per Janni: a discount is applied afterwards for that account rather than
  standing up a separate contract for it)
- `O HCEjeML` → `HomeChargeLadeEjeMel(O HCEjeMEL)`, `O HCLejML` → `HomeChargeLadeLejeMel(O HCLejMEL)`
  (fee side only — the no-fee pairing doesn't exist in Xellent and isn't expected to)
- `__NoCharge__` → `LadeAbo_Afventer_Installation`

**Exact Settl names (2026-09-01):** the five contracts added 2026-08-27 carry a parenthesised
productnum suffix in Settl production's `ContractName` — the names listed above are the exact ones.
The short forms (`Gl.LadeChargeLejeMel`, `Ny.LadeChargeLejeMel`, `HomeChargeLadeEjeMel`, ...) do not
exist in Settl and made `create` refuse those accounts. The seven `LadeAbo_*` names were exact already.

**Scope is per metering point (2026-09-01, business decisions confirmed on accounts `105212` and
`105909`).** The charger rule used to require exactly one El-ekstern MP on the account; an account
with a charger MP plus ordinary El-ekstern MPs was refused the rule as a whole and every MP fell to
the template/product rules. Now `ContractNameResolver` finds the MPs on the account a charger is
unambiguously linked to:

- none → every MP asks the charger rule (the `__NoCharge__` sentinel when the account has no charger
  data at all; fall-through when the charger could only be tied to the account, not to one MP);
- one or more → each linked MP resolves through the charger table with its own charger(s) (and each
  gets its own charger-driven future extension). Every unlinked MP on the account skips the charger
  step — no sentinel — and takes the template/product rules like any ordinary MP.

What makes "one or more" safe is the postal-code tie-break in XellentDomain's `BuildEvChargerLookups`
(2026-09-01): with several candidate MPs, each charger installation is linked to the one MP whose
delpoint postal code equals the installation's, and only the installations it cannot settle stay on
the account with a "left unlinked" warning. So a link in `evChargersByMeteringPoint` is always unique,
and an account with two charger MPs — `105909`: delpoints in 8930 and 7840, installations `1090220`
in 8930 and `1090295` in 7840 — migrates with each MP on its own contract.

**Several installations on one metering point (2026-09-01, user decisions on accounts `491704`,
`109909`, `471352`, `498288`).**
One household with several charging boxes is a known arrangement, and Settl has exactly one contract
for it: `LadeAbo_Eje_2inst_1mp_Variabel` — owned and Variabel only; there is no rented, mixed or UA
variant. An MP with two or more installations linked to it resolves to that contract when **both**
of these hold:

- every Charge_Abo product live at the tuple's effective date across those chargers is owned —
  `OAboEjeMEL` or `OAboEje0` (`ElectricityChargerContractRules.OwnedChargeAboProducts`);
- the el product has an entry under the reserved key `__MultipleInstallations__` in
  `ElectricityChargerContractMap` — shipped for `V LadVariP` only.

Where the installations are registered is **not** a condition: an installation at another address
than the metering point still counts (decided 2026-09-01 on `109909`, `471352`, `498288`). The
`EvCharger.AddressMatchesMeteringPoint` flag (XellentDomain 0.8.53) is not read by this rule — it
only drives the tie-break inside XellentDomain that linked the charger to the MP in the first place.

The MP then gets ONE link spanning the whole electricity tuple, no per-product split, and the
charger-driven future extension does not apply to it. The rule hit reads
`V LadVariP + __MultipleInstallations__ → LadeAbo_Eje_2inst_1mp_Variabel`.

Any other multi-installation MP stays paused — that MP alone falls through, its siblings resolve —
with one of two reasons, printed as-is by the dashboard's dry-run summary:

- `several EV chargers on metering point, product mix has no multi-installation contract: <products> (paused for now)`
  — rented (`OAboLejMEL`), excluded (`OAboEjeUEL`), unconfirmed, or nothing live
- `several EV chargers on metering point, no multi-installation contract configured for '<el product>' (paused for now)`
  — the `V LadVarUA` case

Seven production accounts had a multi-installation MP at cutover: `109909`, `213457`, `360535`,
`415905`, `421416`, `430387`, `475632`; which of them now resolve depends only on the product mix
and the el product.

**Deliberately still excluded** (confirmed by the business to have **no** Settl contract at all —
these accounts have no live electricity delivery and stay in Xellent, not a "pending" pause):
`OAboEjeUEL`, `OAboLejUEL`, `Gl.CLejUEL`.

**Still unconfirmed / not investigated:** `OAbLejBero`, `ChaBeroAbo`, `ChaXtraA`, `ChaEjSeAfg`,
`ChaLejeSer` and any other low-volume code not listed above — these still fall through to the
product-name fallback.

**Two Charge_Abo products live at once (2026-09-01, business decision).** Some accounts have a
stale, unclosed old line (typically `OAboEje0`) running alongside the new `OAboEjeMEL`/`OAboEjeUEL`
line — the old row was left with a future end date instead of being closed the day the new one
started. 14 production accounts were in this state at cutover. The resolver used to refuse to guess
and fell through to the product rule, which landed the MP on its raw `V LadVariP`/`V LadVarUA`
product name and blocked the account. Now it neither guesses nor refuses:

- every live Charge_Abo product with a configured entry resolves to its own contract through the
  charger table, and the MP gets one contract link per contract;
- each link spans its own product's live window clipped to the electricity tuple, so the overlap
  Settl shows is exactly the overlap Xellent has — no wider. A single live product is unchanged
  (the link spans the tuple as before);
- one warning per account names the products and the contracts sent, so the operator sees the
  overlap was passed on. The stale end date is then fixed in Xellent by hand;
- a live product with no configured entry is named in the warning and left out; it does not cost the
  products that do resolve. Only when none of them resolves does the MP fall through as before;
- the charger-driven future extension applies the same rule per product: a stale row that starts or
  ends in the future opens or closes its own link without touching the other one.

Downstream the links flow unchanged: `CreateRunner` and `SettlClient` key contract links on
(metering point, contract), so two contracts on one MP are two `AddContractMeteringPoints` calls,
and the dashboard report groups links per MP per contract. `MpContractNames` (one name per MP, for
display) shows the open-ended product's contract. **Not verified against Settl:** whether Settl
accepts one MP on two contracts for the same period is untested; if it refuses, the account fails on
the existing per-account error path.

**Open, not yet acted on in code** (see the 2026-08-27 confirmation round for detail):
- `461197`, `473075`, `493178` and the address-based pairing gap (`491068`) are manual/charger-link
  cases in `XellentDomainMapper.BuildEvChargerLookups`, not a Charge_Abo naming-table question —
  out of scope for this resolver.
- `LadeAbo_Afventer_Installation` for the 18 accounts whose charger contract previously existed and
  has since ended (vs. the 98 that never had one) is unresolved with the business — both groups
  currently get the same contract.

**Settl contract existence:** as of 2026-08-27, `LadeAbo_Eje0kr_Variabel`/`LadeAbo_Eje0kr_VariabelUA`
were not found in the **featuretest** contract list (checked via the Settl API, Electricity
commodity). Confirm these contracts exist wherever a `create` run will actually target — including
featuretest, if that's the dry-run environment — before relying on this mapping there; otherwise the
account resolves the name locally but `LoadContractsAsync` reports it missing and blocks the account.

## Source

Derived via `xellent-investigator` query against AXDB50 (production),
2026-08-24, cross-checked against domain owner corrections in chat the same
day. `EXU_CONTRACTPARTTABLE.PRODUCTNUM` is `nvarchar(10)` and truncates
`INVENTTABLE.ITEMID` for longer codes — the join used to resolve descriptions
in the initial survey is lossy for the `OAbo*`/`O HC*` families, which is why
several codes above are still provisional.
