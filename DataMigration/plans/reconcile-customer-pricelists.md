> **✅ IMPLEMENTERET** — `9b92e010`
>

# Plan: customer-pricelists i reconcile

> **STATUS 2026-06-11: IMPLEMENTERET** på develop i alle tre repos.
> SettlClient `f46e522` (klienter) · DataMigration `9b92e010` (builder-udtræk + reconcile-wiring,
> CHANGELOG `pending`) · MigrationDashboard `eb51c46` (wiring + SubType-filter). Periode bevidst
> ikke sammenlignet (sæt-medlemskab på (PriceList, ProductElement)). Udestår: bruger-verifikation
> mod production (read-only) — featuretest har næsten ingen prisliste-opsætning.


## Verifikation (start her)

### Hvad der beviser at det virker
- Kør: `dotnet build` på SettlClient-solution → 0 errors (nye GET-klienter).
- Kør: `dotnet test` SettlApi/SettlClient unit tests → 0 failed.
- Kør: `dotnet build` DataMigration-solution → 0 errors (efter NuGet-restore af ny SettlClient-preview).
- Kør: `dotnet test src/Tests/DataMigration.OpsCli.Tests` → 0 failed (ny intended-builder + categorizer-case).
- Kør (integration): `RUN_INTEGRATION=1` + OpsCli.IntegrationTests reconcile-test mod AXDB50_TEST + featuretest.
- Kør (CLI smoke, read-only): `dotnet run --project src/DataMigration.OpsCli -- reconcile --env featuretest --dataarea hol`
- Spot-check: vælg 2-3 kunder fra CSV'en; sammenlign reconcile-PriceList-rækker mod `GET /api/v2/customers/{id}/pricelists` via settl-api-query-skill.

### Forventet output / succeskriterium
- CSV har nye rækker: `Entity=Customer`, `SubType=PriceList`, kategori `Missing in Settl` eller `Anomaly`.
- Kunde uden prisliste-drift → ingen PriceList-rækker.
- Intended-association Settl mangler → præcis én `Missing in Settl`-række med `(ProductElementCode, PriceList-navn)` i Field/Intended.
- Settl-association uden intended-modpart → præcis én `Anomaly`-række.
- Ingen periode-rækker (associationer er periode-løse i Settl — bevidst udeladt).

## Kontekst
Reconcile dækker i dag customer/payer/MP/links/settlement-groups men ikke prisliste-associationer; Settl billing-internal API eksponerer både katalog og kundens associationer, så de kan reconciles read-only uden manuelle Excel-filer.

## Ændringer

### SettlClient-repo (NuGet-producer — gøres FØRST, vent på CI-preview)
- Ny `ISettlPriceListClient` + impl: `GET /api/v1/priceLists` (katalog: Id↔PriceListIdentifier↔Name), `GET /api/v1/productElements` (Id↔ProductElementCode).
- Udvid customer-klient (eller ny metode): `GET /api/v2/customers/{customerId}/pricelists` → liste af `(PriceListId, ProductElementId, AssociationPeriod{Start,End})`. ContractId/MarketBalanceAreaId er tom-GUID i praksis → ikke i diff. NB: `AssociationPeriod` mangler i `docs/billing_internal_api.json` (stale swagger) men returneres af live API — modellér efter live-respons.
- DTO'er + unit tests. Bump VersionPrefix, commit develop, vent på CI-preview-pakke.

### DataMigration-repo
- **Arkitektur (valgt):** Option A — delt OpsCli-service; extraktionen skal samtidig *forbedre* `ExportCustomerPriceListsRunner` (logik bliver testbar). IKKE i create-pipelinen (undgår laglækage).
- Ny `CustomerPriceListAssociationBuilder` (pure, OpsCli.Library/Services): input = projiceret mp-mapping (`MeteringPointId, ContractNum, ProductNum, AmrCode, ContractPartStart/End`) + lookups (`contractEndDates`, `deliveryRateCodes`, `contractPartLogEndDates`) + `Func<string,string?>` prisliste-navn→id + `ProductElementCodeResolver` + `stillActiveOnCos` + COS/refDate/env. Output = `List<CustomerPriceListAssociationRow>` + diagnostik (unmatched). Indeholder hoved-loop + go-forward-log + collapse. Unit tests (nyt test-net).
- `ExportCustomerPriceListsRunner` refaktoreres til at gather inputs + kalde builder + skrive Excel/logge (tyndere runner). `Func` = CSV-map i dag → API-katalog når reconcile deler den.
- `ReconcileRunner`: hent priceLists+productElements katalog (name/code→GUID); pr. slice projicér mp-mapping + lookups + kald builder → intended-associationer; hent faktiske via `GetCustomerPriceListsAsync` pr. rep; resolv intended→GUIDs; diff `(PriceListId, ProductElementId)` → MissingInSettl/Anomaly + blød periode-diff. SubType=PriceList.
- `ReconcileRunner`: hent priceLists- + productElements-katalog én gang pr. scan (name/code→GUID-maps; prisliste resolves Name→katalog-`Id`, IKKE PriceListIdentifier); hent faktiske associationer pr. repræsentant (én GET, som eksisterende per-rep detail-GETs); resolv intended→GUIDs; diff sæt af `(PriceListId, ProductElementId)` → MissingInSettl (intended mangler) / Anomaly (ekstra i Settl).
- Periode: matchede associationer kan derudover periode-diffes (intended Start/End vs `AssociationPeriod`). Behandl som blød date-semantics-kategori (DK-tz-konvertering som COS; intended kollapser Start til COS mens Settl-Start ofte er prissheet-start → forventet støj). Primær nøgle forbliver (PriceList, ProductElement).
- `ReconcileDriftRow`: brug eksisterende `SubType` med ny værdi `"PriceList"` (additivt). EntityKey = ProductElementCode; Field = prisliste-navn.
- `DriftCategorizer`: bekræft at PriceList-rækker falder i `Missing in Settl` / `Anomaly` (kind-baseret — sandsynligvis ingen ændring nødvendig).
- CSV: PriceList-rækker flyder med eksisterende kolonner.
- Tests: categorizer-case + intended-builder unit test. CHANGELOG-række (`reconcile`).

### MigrationDashboard-repo (efter DataMigration-preview er ude)
- Restore ny OpsCli-preview; PriceList-rækker flyder med via eksisterende ReconcileRowDto-mapping (intet nyt felt — SubType bærer det). Verificér SubType-chip/filter viser "PriceList".

## Afklaret
- **Nøgle-granularitet:** diff nøgles på `(PriceList, ProductElement)`. Contract/MBA ignoreres i diff (matcher det intended-siden producerer).
- **Katalog-kilde:** `GET /api/v1/priceLists` (drop `--pricelist-csv`); delt kilde for både export-runner og reconcile.

## Verificeret mod featuretest (2026-06-11, read-only)
- `GET /internal-billing/api/v1/priceLists` → 200. Item: `Id`(GUID), `PriceListIdentifier`, `Name` (fx "ST 0500"), `CommodityType`, `PriceSheets[Start/End/Price]`.
- `GET /internal-billing/api/v1/productElements` → 200. Item: `Id`(GUID), `ProductElementCode`.
- `GET /internal-billing/api/v2/customers/{id}/pricelists` → 200. Item: `PriceListId`, `ProductElementId`, `ContractId`(tom), `MarketBalanceAreaId`(tom), **`AssociationPeriod{Start,End}`**, `Id`.
- Featuretest har stort set ingen prisliste-associationer (import ikke kørt der — forventet); shape bekræftet på de få kunder der har. Reel verifikation af diff-tal sker mod production-katalog/-associationer (read-only).
