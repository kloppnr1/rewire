# Supply and readings

Use for who was supplied when, equipment changes, consumption, and query construction.
Labels are defined in [Start here](start.md).

## Joins and identifiers

**Code:** Begin with customer → contract → part as described in
[Customers and contracts](customers-contracts.md#follow-the-relationship-being-asked-about).
Then select the path from the **part's** category:

| Path | Required relationship |
|---|---|
| Water/heat installation | Part `(DATAAREAID, INSTAGREENUM)` → installation `(DATAAREAID, INSTNUM)`; retain part company/category |
| Electricity agreement/delpoint | Part `(DATAAREAID, COMPANYID, INSTAGREENUM)` → agreement `(DATAAREAID, COMPANYID, AGREEMENTNUM)` → delpoint `ATTACHMENTNUM`; retain category |
| Delpoint periods/meter trades | Full point scope: data area, category, company, attachment and local delpoint number. On meter trades, attachment is named `AGREEMENTNUM` and local point is `DELPOINTNUM`. |

The mapper synthesizes `V{INSTNUM}01` for installation-based points. Keep the
original scoped installation identity; the string is not a national point ID.
For other categories, verify the branch before reusing these joins. All categories
remain in scope; these are the paths established by the reviewed code.

## Supply on a date

**Rule:** Preserve product rows and check both agreement and part validity for
the requested day. More than one eligible contract is a discrepancy to examine;
several products within one contract can be intentional.

**Code:** Contract/part end `1900-01-01` means open-ended. A dated investigation
uses start ≤ day and (open end or end ≥ day). Some CustomerSupport “active” readers
check only end, and some use strict `>`; use their precise predicate only when
reproducing that screen. The migration loader is a selected snapshot: water/heat
assignments exclude future starts and history depends on configured date windows.

[Exercise D1](../development-query-exercises.md#d1-who-was-supplied-with-water-or-heat-at-this-installation-on-a-date)
provides the dated EF example. For missing historical parts, inspect
[part history](../xellent-field-semantics.md#contract-part-log-is-a-change-history);
the log contains previous state for date changes as well as deletions.

## Meter replacement and quantities

**Code:** Installation journals retain data area, company, category, installation,
meter, event date, posting state, and event code. The current mapper chooses the
latest posted journal within installation/category without a company filter in
that final lookup, and supplies default meter characteristics. Those defaults
are not measured equipment properties.

Actual installation readings use CustomerSupport's `ExuReadingTables`; the shared
context does not map that table. Keep data area, category, company, installation,
meter, counter and reading date. Register value, recorded consumption, and annual
estimate are different quantities. Preserve `QUANTITYCODEVALUE` and reading reason.
The support reader interprets reason 1 as replacement; this does not label the
journal's separate event codes. Do not subtract registers across replacement
without establishing counter/unit/reset semantics.

[Exercise D3](../development-query-exercises.md#d3-what-meter-evidence-exists-around-a-suspected-water-meter-replacement)
shows the two-context approach. **Open:** The reviewed models do not establish
every reading-status, method, or journal-code meaning.

## Time series and Danish daylight saving

**Code:** `EMS_TIMESERIESVALUES.TIMEOFVALUE` is treated as UTC. Convert each Danish
day boundary to UTC and query a half-open interval. Spring/autumn transition days
have 23/25 hours: 92/100 quarter-hour slots if complete. Preserve offsets when
distinguishing the repeated autumn hour. Current-version sentinel is exactly
`2154-12-31 23:59:59` in `VERSIONTOEXCL`.

Select series headers by point, data area, category, company, resolution and quantity
before selecting values. These UTC rules do not establish the time zone of
`EXU_READINGTABLE.READINGDATE`. Use [D4 and its DST examples](../development-query-exercises.md#d4-which-stored-observations-exist-for-a-datahub-electricity-point-on-a-danish-day).
For pricing, read only [Prices and tariff versions](../xellent-field-semantics.md#prices-and-tariff-versions).
