# Plan: IXellentAccountSearch in DataMigration.Domain.Xellent

## Verifikation (start her)

### Trin 1 — Pakke bygger og tester
- Kør: `dotnet build` i UtilityData root
- Forventet: 0 errors. Eksisterende tests grønne.
- Kør: `dotnet test`
- Forventet: alle tests passerer. Ny enhedstest for IXellentAccountSearch (mod InMemory eller SQLite) verificerer minimum: field-specific Name + Street-variant + alsoSearchCityZip + IncludeInactive + relevance scoring.

### Trin 2 — Bifrost-integration (kør først efter pakke er publiceret)
- Bump `NewSettlement.XellentDomain.*` version i Bifrost
- Refactor `CustomerSearchService` så field-specific path kalder pakken; universal-query routing forbliver i Bifrost
- Kør: `dotnet build CustomerSupport.sln` → 0 errors
- Kør: `dotnet test src/Tests/CustomerSupport.Api.Tests/` → alle tests grønne
- Smoketest i Bifrost mod AXDB50_TEST (privatperson, CVR-gruppe, CVR uden TG4, numerisk query): identiske resultater før/efter

## Kontekst

- Bifrost (`NewSettlement.CustomerSupport`) har `CustomerSearchService` (~1266 linjer) der queryer Xellent-DB direkte
- Bruger har valgt at centralisere query-laget i `NewSettlement.XellentDomain` så Bifrost kun bruger pakken — ikke direkte EF/DbContext
- `DataMigration.Domain.Xellent` har allerede `XellentDomainLoader` (account-input → domain output). Search er omvendt (criteria-input → matched accounts) og kræver nyt API
- Bifrost beholder: UX-pattern building, universal-query routing, CVR-gruppering, post-query enrichment

## Ændringer (DataMigration.Domain.Xellent)

### Nye public typer
- `XellentAccountSearchCriteria` (record) — DataAreas, PreFilteredAccounts, NameCriteria, AccountNumber, Email, PhonePattern, IdentifierPattern, InvoiceNumberPadded, StreetCriteria, StreetRelevancePattern, DarIdForRelevance, IncludeInactive, MaxResults, AsOfDate
- `NameCriteria` (record) — Patterns (List), bruges som AND mellem ord
- `StreetCriteria` (record) — Patterns (1–4 LIKE-patterns), AlsoSearchCityAndZip flag, optional CityPattern + ZipPattern
- `XellentAccountMatch` (record) — DataAreaId, AccountNumber, Name, NameAlias, Street, City, ZipCode, CountryRegionId, StreetRelevance, DarIdRelevance
- `IXellentAccountSearch` interface — `Task<IReadOnlyList<XellentAccountMatch>> SearchAsync(criteria, ct)`
- `XellentAccountSearch` impl — wrapper ala `XellentDomainLoader` der opretter `XellentDbContext` internt og kører queryen
- Static convenience: `XellentAccountSearchLoader.SearchAsync(connectionString, criteria, ct)` (matcher `XellentDomainLoader`-mønster)

### Query-logik (1:1 oversat fra Bifrost CustomerSearchService.cs:286-870)
- Address-join: `CustTable` → `DirPartyAddressRelationship` → `DirPartyAddressRelationshi1066` → `Address`, filtreret på `Addrtableid == 10` og `Validtodatetime >= today`
- Field-specific filters: Name (per-ord AND, Latin1_General_CI_AS collation), AccountNumber, Email, PhonePattern (Phone OR Cellularphone), IdentifierPattern (ExuCprDisponent1/2 OR ExuCvrDisponent1/2), InvoiceNumberPadded (CustInvoiceJour Any), Street (op til 4 patterns, alsoSearchCityZip-gren med City/Zipcode + delpoint/installation address fallback)
- Contract-aktiv filter (IncludeInactive=false): `ExuContractTable.Any()` med Contractenddate==NeverEnds OR (Enddate>today AND Startdate<=today)
- Relevance scoring: streetRelevance (Address1 LIKE pattern), darIdRelevance (Address.ExuAddressid match OR Installation.Addressid match) — kun beregnes når patterns er sat
- Take(MaxResults)

### Ikke i scope for denne iteration
- CVR-gruppering / TG4-expansion — forbliver i Bifrost (post-query)
- Migration badge enrichment, stopped categories, billing plans — forbliver i Bifrost
- Universal text query routing (numeric/UUID detection) — Bifrost konverterer til strukturerede criteria før kald

## Kritiske filer (UtilityData)
- Ny: `src/DataMigration.Domain.Xellent/XellentAccountSearchCriteria.cs`
- Ny: `src/DataMigration.Domain.Xellent/XellentAccountMatch.cs`
- Ny: `src/DataMigration.Domain.Xellent/IXellentAccountSearch.cs`
- Ny: `src/DataMigration.Domain.Xellent/XellentAccountSearch.cs` (intern impl)
- Ny: `src/DataMigration.Domain.Xellent/XellentAccountSearchLoader.cs` (static entry point)
- Ny: `src/Tests/DataMigration.Domain.Xellent.Tests/XellentAccountSearchTests.cs`
- `Directory.Build.props` — version bump (auto-publish via CI)

## Risici
- **Constants i pakke**: Bifrost bruger `MigrationConstants.AddressTableId = 10`. Verificér at samme konstant findes i `DataMigration.Core`
- **Collation-dependence**: `EF.Functions.Collate(..., "Latin1_General_CI_AS")` skal virke i pakken (samme provider). Provider er `Microsoft.EntityFrameworkCore.SqlServer` så det er fint
- **Test-coverage**: Address-joins er svære at teste mod InMemory provider (ingen LIKE). Brug SQLite med `EF.Functions.Like` emulation, eller mark som integration-test (krav: AXDB50_TEST)
- **Stort first cut**: ~600 linjer query-kode i én iteration. Spliit i to PRs hvis review-feedback peger på det (1: API + field-specific filters; 2: street variants + relevance scoring)
