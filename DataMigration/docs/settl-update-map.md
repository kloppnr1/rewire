---
last-reviewed: 2026-04-15
owner: OpsCli maintainer
synonyms:
  EAN: ElectronicNumber, ElectronicNo, InvoiceCcElectronicNumber
  GLN: ElectronicNumber
  CVR: VatIdentificationNumber, NationalIdentificationNumber
  CPR: NationalIdentificationNumber
  valutaen: Currency
  fakturaadresse: InvoiceCcEmail, InvoiceCcElectronicNumber
---

# Settl-felter — Xellent↔Settl↔Repo lineage (MP-scope)

Genereret fra `docs/billing_internal_api.json` + `docs/market_internal_api.json`.
Kun master-data update-endpoints for MeteringPoint:

- `PUT /api/v1/meteringPoints/{id}/meteringPoint` (billing)
- `PUT /api/v3/meteringPoints/{id}` (market)

Cross-refereret mod NuGet `SettlClient.Api` (Models/), `src/DataMigration.Migration/`
og `src/DataMigration.OpsCli/`. Bruges af `.claude/skills/post-migration-fix/` som
primær lookup.

**Legende:**
- `Our DTO` — første hit af feltnavn i Models/; `(gap)` = umodelleret.
- `Create sets it?` — `no` hvis intet hit i Migration-kode; fil:line = lokation.
- `Fix runner` — navn på eksisterende Fix*Runner der rammer feltet (tom = SCAFFOLD).
- `Xellent source` — manuelt udfyldt; typisk en `Exu*Table` kolonne.
- `Fix action` — RUN fix-<name> / SCAFFOLD / STOP.

**Bemærk:** rækker markeret `(noisy name)` har generiske feltnavne (Start, End, Id, ...);
DTO/Create-kolonner er undladt for at undgå falske matches.


## (top-level)

| Field | Write endpoint | Our DTO | Create sets it? | Fix runner | Fix action |
|---|---|---|---|---|---|
| `TransactionId` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |

## SettlementInfo

| Field | Write endpoint | Our DTO | Create sets it? | Fix runner | Fix action |
|---|---|---|---|---|---|
| `SettlementInfo.Start` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `SettlementInfo.End` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `SettlementInfo.IgnoreInBilling` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (gap) | no |  | SCAFFOLD (DTO gap) |
| `SettlementInfo.ExemptFromVat` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (gap) | no |  | SCAFFOLD (DTO gap) |

## InvoiceConfiguration

| Field | Write endpoint | Our DTO | Create sets it? | Fix runner | Fix action |
|---|---|---|---|---|---|
| `InvoiceConfiguration.Start` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `InvoiceConfiguration.End` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |

## PaymentConfiguration

| Field | Write endpoint | Our DTO | Create sets it? | Fix runner | Fix action |
|---|---|---|---|---|---|
| `PaymentConfiguration.Start` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `PaymentConfiguration.End` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `PaymentConfiguration.InvoiceCcEmail` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (gap) | no |  | SCAFFOLD (DTO gap) |
| `PaymentConfiguration.InvoiceCcElectronicNumber` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (gap) | no |  | SCAFFOLD (DTO gap) |
| `PaymentConfiguration.BillingType` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (gap) | no |  | SCAFFOLD (DTO gap) |
| `PaymentConfiguration.OrderNumber` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | ContractModels.cs:12 | no |  | SCAFFOLD |
| `PaymentConfiguration.ElectronicNumber` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | MeteringPointModels.cs | no (server-side) |  |  |
| `PaymentConfiguration.AgreementIdentifier` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | PayerModels.cs:47 | SettlExtractionService.Payers.cs:241 |  | SCAFFOLD |
| `PaymentConfiguration.OrderIdentifier` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | `MeteringPointModels.cs:284` | `SettlClient` (commit `029fb4ef`); `fix-order-identifier` (backfill) | `fix-order-identifier` | ✓ done |
| `PaymentConfiguration.BookkeepingReference` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | PayerModels.cs:49 | no |  | SCAFFOLD |

## BillingConfiguration

| Field | Write endpoint | Our DTO | Create sets it? | Fix runner | Fix action |
|---|---|---|---|---|---|
| `BillingConfiguration.Start` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `BillingConfiguration.End` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `BillingConfiguration.JointInvoice` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | MeteringPointModels.cs:191 | no |  |  |
| `BillingConfiguration.JointInvoiceNumber` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | MeteringPointModels.cs:31 | SettlExtractionService.MeteringPoints.cs:322 |  |  |
| `BillingConfiguration.NextInvoiceManual` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | MeteringPointModels.cs:217 | no |  | SCAFFOLD |
| `BillingConfiguration.AutomaticApprovalOfDraft` | PUT /api/v1/meteringPoints/{mpId}/meteringPoint | MeteringPointModels.cs:218 | no |  | SCAFFOLD |

## MeteringPoint

| Field | Write endpoint | Our DTO | Create sets it? | Fix runner | Fix action |
|---|---|---|---|---|---|
| `MeteringPoint.Id` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.ExternalIdentifier` | PUT /api/v3/meteringPoints/{mpId} | ContractModels.cs:52 | CustomerBundleMigrator.cs:302 |  | SCAFFOLD |
| `MeteringPoint.SystemService.IsSystemService` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SystemService.PlantName` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.ElectricalHeating` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.ElectricalHeatingDate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.WebAccessCode` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.ConsumerCategory` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.HasBalanceSupplier` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.SupplyStart` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.UseExtendedStorageOfMeteringValues` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Consumer.ProtectedName` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Identification.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Identification.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Identification.MeteringPointIdentification` | PUT /api/v3/meteringPoints/{mpId} | ContractModels.cs:35 | CustomerBundleMigrator.cs:74 |  |  |
| `MeteringPoint.Identification.ValidateMeteringPointIdentifier` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Identification.InstallationNumber` | PUT /api/v3/meteringPoints/{mpId} | ContractModels.cs:34 | SettlExtractionService.MeteringPoints.cs:258 |  | SCAFFOLD |
| `MeteringPoint.InstallationAddress.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.InstallationAddress.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.InstallationAddress.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.InstallationAddress.InstallationAddressNote` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DistributionSystemOperator.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DistributionSystemOperator.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DistributionSystemOperator.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DistributionSystemOperator.ContractNumber` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DistributionSystemOperator.NextNetworkUsageBillingYear` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.Zone` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.UtmX` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.UtmY` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.Capacity` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.ProductionAddress` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.ProductionPostalCode` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.Gsrn` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Renewable.ConsumptionMeteringPoint` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeterCharacteristics.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeterCharacteristics.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeterCharacteristics.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeterCharacteristics.EstimatedAnnualMaxPower` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeterCharacteristics.MeterReadingIntervalForTheMeterOperator` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeterCharacteristics.HeatingDependingOnElectricity` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SettlementMethod.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SettlementMethod.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SettlementMethod.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.MarketBalanceAreaId` | PUT /api/v3/meteringPoints/{mpId} | MeteringPointModels.cs:110 | CreateRunner.cs:284 |  |  |
| `MeteringPoint.Grid.MeteringGridAreaId` | PUT /api/v3/meteringPoints/{mpId} | MeteringPointModels.cs:111 | CreateRunner.cs:286 |  |  |
| `MeteringPoint.Grid.ResidualProfile` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.ProfileTypeTimeSeriesId` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.ProfileType` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Grid.BalanceSupplierId` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.ConnectionInfo.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.ConnectionInfo.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.ConnectionInfo.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.ConnectionInfo.TransmissionSystemOperatorId` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DeliveryStatus.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DeliveryStatus.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DeliveryStatus.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.DeliveryStatus.InDelivery` | PUT /api/v3/meteringPoints/{mpId} | MeteringPointModels.cs:134 | SettlExtractionService.MeteringPoints.cs:320 |  | SCAFFOLD |
| `MeteringPoint.Miscellaneous.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.IndustryCode` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.ProductCode` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.MarketCommunicationDisabled` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.VisibleInSalesSystem` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.VisibleInWebPortal` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Miscellaneous.RelatesToProduction` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Notes.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Notes.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Notes.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Notes.AdditionalInfo1` | PUT /api/v3/meteringPoints/{mpId} | MeteringPointModels.cs:35 | SettlExtractionService.MeteringPoints.cs:325 |  | SCAFFOLD |
| `MeteringPoint.Notes.AdditionalInfo2` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Notes.Remark` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Notes.InvoiceNote` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Relation.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Relation.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Relation.ParentMeteringPointId` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Relation.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.EnovaFee` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.ElFee` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.ElCertificateShare` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.ConsumptionCode` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.MunicipalityDiscountPercentage` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Taxation.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Equipment.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Equipment.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Equipment.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.BalanceResponsible.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.BalanceResponsible.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.BalanceResponsible.IsResponsible` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.BalanceResponsible.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Balance.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Balance.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Balance.RegulationZone` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Balance.BalancingGroup` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Balance.BalancingArea` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Balance.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.RegisterConfiguration.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.RegisterConfiguration.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.RegisterConfiguration.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.AgreementIdentification` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.MarketCustomerIds` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.Canceled` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.Approved` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.SalesAgreement.InformationUpdatedInMarket` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.EnergyCommunity.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.EnergyCommunity.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.EnergyCommunity.Identification` | PUT /api/v3/meteringPoints/{mpId} | CustomerModels.cs:76 | CustomerBundleMigrator.cs:74 |  |  |
| `MeteringPoint.EnergyCommunity.CommunityName` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.EnergyCommunity.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Netting.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Netting.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Netting.IsNettingApplied` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.Netting.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.NetSettlementGroup.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.NetSettlementGroup.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.NetSettlementGroup.MarketUpdate` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.PowerPlantIdentification.Start` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.PowerPlantIdentification.End` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.PowerPlantIdentification.Code` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.ProfileTimeSeriesId` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.MeasuredTimeSeriesId` | PUT /api/v3/meteringPoints/{mpId} | (gap) | no |  | SCAFFOLD (DTO gap) |
| `MeteringPoint.LastUpdated` | PUT /api/v3/meteringPoints/{mpId} | (noisy name) | (noisy name) |  | SCAFFOLD (DTO gap) |

## Fields with no write path (STOP rows)

Primary-key fields and read-only computed fields. To be filled in during review.

## Other entities

Customer / Contract not yet mapped — add rows when a concrete need arises.
