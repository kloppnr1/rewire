> **✅ IMPLEMENTERET** — `5f528b4e`
>

## Verifikation (start her)

### Hvad der beviser at det virker

1. **Build** — `dotnet build DataMigration.sln` → 0 errors.
2. **Unit tests** — `dotnet test src/Tests/DataMigration.Migration.Tests/DataMigration.Migration.Tests.csproj` → alle grønne.
3. **Xellent integration tests** (kør lokalt, kræver xel2012):
   `dotnet test src/Tests/DataMigration.Xellent.IntegrationTests/DataMigration.Xellent.IntegrationTests.csproj`
   Forventet: alle profiler grønne. Goldens for `match`, `ppa-customers`, `vand-sample` må kun ændre sig på owner-cluster-rækker (ikke på representative/customer-merging).
4. **Spot-check de 4 accountnums** — kør `match`-profil dry-run mod featuretest:
   `dotnet run --project src/DataMigration.Migration -- create --profile src/profiles/match.json --dry-run`
   Verificér at 489377, 489400, 489428, 489744 alle har en Owner i output (samme `OwnerId` hvis de deler CVR+TG4).

### Forventet output / succeskriterium

- De 4 accountnums får tildelt en Owner (forudsat de har samme CVR+TG4 og ≥2 distinkte representative accounts under den nøgle).
- Profiler hvor `LegacyTg4Merge=true` har ingen ændring i representative-resolution / customer-merging — KUN owner-cluster-rækker kan ændre sig.

## Kontekst

Owner-clusters skal udelukkende afgøres af `CVR + TG4` (ikke længere via fingerprint-split eller TG4-only legacy-mode). Representative-resolution forbliver uændret — `LegacyTg4Merge` beholdes for merge-key-pathen.

## Ændringer

- `SettlExtractionService.BuildOwnerClusters`: erstat begge eksisterende grene med ét rent CVR+TG4-cluster:
  - Group `accountGroupings` på `(Cvr, GroupNum)` — skip hvis enten er null/empty.
  - Behold kun grupper med ≥2 distinkte `RepresentativeAccount`.
  - `ClusterKey = $"{Cvr}|{GroupNum}"`, `OwnerName = GroupName` (første ikke-tomme i gruppen).
- Fjern `_legacyTg4Merge`-betingelsen i `BuildOwnerClusters` (men behold `_legacyTg4Merge`-feltet og legacy-merge-key-pathen `BuildLegacyTg4MergeKeyMapAsync` urørt — bruges stadig af representative-resolution).
- `OwnerCluster.cs` doc-comment: opdatér til at beskrive CVR+TG4-modellen (drop legacy/fingerprint-mode-beskrivelsen).
- `CreateRunner.cs` log-line: erstat `options.LegacyTg4Merge ? "--- Cross-CVR TechnicGroup4 owners ---" : "--- Fingerprint-split owners ---"` med statisk `"--- CVR+TG4 owners ---"`.
- Ingen ændringer til: `ProfileOptions`, `CreateOptions`, `MigrationDtos`, `MigrationDefinitionService`, `AccountRepresentativeResolver`, `BuildLegacyTg4MergeKeyMapAsync`, profil-JSON-filer.
- Goldens: opdater Xellent integration test goldens hvor owner-cluster-output ændrer sig (efter user-review af diff).
