> **✅ IMPLEMENTERET** — `b4344b8d`
>

# Plan: TG4/kundegruppe case-insensitive sammenligning

To-repos-fix. Xellent `ExuTechnicgroup4` (kundegruppe) blev sammenlignet
case-sensitive flere steder. Profilens `IgnoredGroups` og legacy CVR+TG4
merge fejler hvis casing afviger fra DB-værdien.

## Verifikation (start her)

### Del A — DataMigration (dette repo, kode FÆRDIG, mangler commit)
- Kør: `dotnet build src/DataMigration.Migration.Library/DataMigration.Migration.Library.csproj`
  - Forventet: `0 Error(s)` ✓ (kørt)
- Kør: `dotnet test src/Tests/DataMigration.Migration.Tests/DataMigration.Migration.Tests.csproj --filter "FullyQualifiedName~OwnerCluster"`
  - Forventet: `Passed: 5, Failed: 0` ✓ (kørt — inkl. 2 nye case-tests)

### Del B — UtilityData (pakke, SEPARAT session/repo)
- Kør i UtilityData: `dotnet test src/Tests/DataMigration.Domain.Xellent.Tests/`
  - Forventet: alle grønne (tilføj nye tests for legacy-merge + owner-cluster casing)
- Empirisk tjek (afgør om fixen ændrer faktisk adfærd): findes der TG4-værdier
  i AXDB50 der kun afviger på casing?
  - Kør (xellent-investigator): `SELECT ExuTechnicgroup4, COUNT(*) FROM CustTable
    WHERE Dataareaid='hol' AND ExuTechnicgroup4<>'' GROUP BY UPPER(ExuTechnicgroup4)
    HAVING COUNT(DISTINCT ExuTechnicgroup4) > 1`
  - Forventet: 0 rækker → fixen er ren hærdning (ingen golden-drift). >0 rækker
    → golden-baselines i DataMigration vil skifte; kræver re-baseline (spørg først).

### Del C — cross-repo integration (efter B er pushet)
- Vent på UtilityData CI-preview (`0.8.7-preview.{BuildId}` til feed).
- I DataMigration: `dotnet nuget locals http-cache --clear; dotnet restore --force`
- Kør Xellent golden-suite (skill `xellent-integration-tests`).
  - Forventet: golden uændret HVIS Del B-query gav 0 rækker; ellers re-baseline.

## Kontekst
TG4/kundegruppe-sammenligning skal være case-insensitive — bruger fandt at
casing-afvigelse forhindrer ignore + merge.

## Ændringer

### Del A — dette repo (FÆRDIG, pending commit)
- `MigrationDomainLoad.cs:32` — `IgnoredGroups` HashSet → `OrdinalIgnoreCase`
  (sættet sendes til pakkens mapper; styrer dens `.Contains(Tg4)`).
- `SettlMappingService.cs:50` — ignore-sæt i `BuildOwnerClusters` → `OrdinalIgnoreCase`.
- `SettlMappingService.cs:56` — `GroupBy(GroupNum)` → `OrdinalIgnoreCase` (create's
  owner-clustering, CreateRunner.cs:524).
- 2 nye tests + CHANGELOG-række.

### Del B — UtilityData / DataMigration.Domain.Xellent (TODO, andet repo)
- `XellentDomainMapper.cs:468` — `GroupBy(r => r.Tg4, StringComparer.Ordinal)` →
  `OrdinalIgnoreCase` (mirror af SettlMappingService; feeder `XellentLoadResult.OwnerClusters`).
- `AccountRepresentativeResolver.cs:283-287` (ResolveLegacyAsync `KeyOf`) —
  normalisér TG4 i merge-nøglen, fx `groupNum.ToUpperInvariant() + "|" + cvr`.
  Dette er den vigtigste: bestemmer hvilke konti der bliver én Settl-kunde i
  legacy-mode (PPA, Spar Nord).
- (Valgfri hærdning) Defensivt rebuild af `IgnoredGroupNumbers` som
  `OrdinalIgnoreCase` ved pakke-entry, så consumeren ikke kan sende et Ordinal-sæt
  (gælder fingerprint linje 106 + legacy linje 285). Uden dette afhænger
  case-insensitivity af kalderens sæt-comparer.
- Bump `Directory.Build.props` VersionPrefix `0.8.6` → `0.8.7` (tjek feed:
  vælg højere end nyeste eksisterende preview). Commit develop → CI publisher preview.
- Tilføj tests i `DataMigration.Domain.Xellent.Tests`.

## Sekvens
1. Commit Del A i dette repo (develop).
2. Åbn UtilityData-session → Del B → push develop → vent på CI-preview.
3. Tilbage i dette repo → restore --force → Del C golden-verifikation.

## Åbne beslutninger
- Skal valgfri hærdning (pakke-entry rebuild af ignored-set) med? Anbefales —
  billig, fjerner skrøbelig afhængighed af consumer-comparer.
- Golden re-baseline kræver eksplicit ja (jf. golden-promotion-regel).
