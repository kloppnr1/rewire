# Settl API Integration — TimeSeriesMigration

This document maps the `post` command's flow to Settl API endpoints. Ported and adapted from DataMigration's integration guide; focuses on what this repo's CLI actually calls.

---

## The post-command flow

Per metering point (fail-fast on any error):

```
1. Search MP by identification
   → GET /market/tenant-api/v1/meteringPoints?MeteringPointIdentification={ean}
   → returns MeteringPointInfoDto { Id }

2. Look up time-series Guid for the MP
   → GET /internal-market/api/v3/meteringPoints/{mpGuid}/timeSeries
   → returns List<TimeSeriesInfoDto> { Id, TimeResolution }; use [0].Id

3. POST time-series points
   → POST /api/v1/timeSeries/{tsGuid}/points
   → body: SettlTimeSeriesRequestDto
     { TransactionId, RegistrationTime, Points[], Revision,
       InvoiceRelevant=true, AccountForQuantityQuality=true }

4. Register externallyInvoicedPeriod
   → POST /internal-billing/api/v1/meteringPoints/{mpId}/externallyInvoicedPeriods
   → body: AddExternallyInvoicedPeriodsRequest
     { TransactionId, Periods: [
         { Period: { Start, End },
           BillableTypes: ["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"],
           Revision, TimeSeriesId: tsGuid } ] }
```

Client code: `src/TimeSeries.SettlApi/Clients/SettlMeteringPointClient.cs` and `SettlTimeSeriesClient.cs`. Runner: `src/TimeSeries.Cli/Runners/TimeSeriesRunner.cs:RunPostAsync`.

---

## Authentication

OAuth2 password grant. Implementation in `src/TimeSeries.SettlApi/Services/SettlAuthService.cs`.

- Token endpoint: `POST {BaseUrl}/security/connect/token`
- Scope: `BIO_Client BIO_Api`
- Token caching with 5-minute expiry buffer
- Basic Auth header (ClientId:ClientSecret) + form body (username, password) when all four credentials are present; else client_id in form body

All requests also require `Ocp-Apim-Subscription-Key` header.

---

## ExternallyInvoicedPeriods — details

```json
{
  "TransactionId": "uuid",
  "Periods": [
    {
      "Period": {
        "Start": "2024-01-01T00:00:00Z",
        "End": "2024-12-31T23:59:59Z"
      },
      "BillableTypes": ["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"],
      "Revision": "2024-01-01T00:00:00Z",
      "TimeSeriesId": "uuid"
    }
  ]
}
```

### BillableTypes enum (from `billing_internal_api.json`)

| Value | Notes |
|---|---|
| `Contracts` | Used in this repo |
| `ExtraInvoiceLines` | Used in this repo |
| `MarketPriceLists` | Used in this repo |
| `NationalContracts` | Used in this repo |
| `PaymentFee` | Used in this repo |
| `MovingFee` | Used in this repo |
| `CustomerContracts` | Not used — rejected by Systemate (not by the Settl API) |
| `VirtualContracts` | Not used |
| `SystemServiceContracts` | Not used |

**This repo uses `["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"]`** per afklaring med Systemate (2026-08-27) — Systemate skriver alle disse skal på for målepunkter (El metering points). Do not change without explicit business confirmation.

### Operations

| Operation | Endpoint |
|-----------|----------|
| List | `GET /internal-billing/api/v1/meteringPoints/{mpId}/externallyInvoicedPeriods` |
| Add | `POST /internal-billing/api/v1/meteringPoints/{mpId}/externallyInvoicedPeriods` |
| Delete all | `DELETE /internal-billing/api/v1/meteringPoints/{mpId}/externallyInvoicedPeriods` |
| Get single | `GET /internal-billing/api/v1/meteringPoints/{mpId}/externallyInvoicedPeriods/{periodId}` |
| Delete single | `DELETE /internal-billing/api/v1/meteringPoints/{mpId}/externallyInvoicedPeriods/{periodId}` |

---

## Environments

| DataAreaId | Environment | BaseUrl |
|------------|-------------|---------|
| hol | production | https://settl-production.example.invalid |
| han | production-vhel | https://settl-production-vhel.example.invalid |
| HJH | production-hev | https://settl-production-hev.example.invalid |
| (dev) | featuretest | https://settl-featuretest.example.invalid |

Base URLs are suffixed internally:
- `/security/connect/token` — auth
- `/market/tenant-api/v1/*` — tenant-facing market (MP search)
- `/internal-market/api/v3/*` — internal market (MP detail, timeSeries list)
- `/internal-billing/api/v1/*` — internal billing (externallyInvoicedPeriods, contracts, customers, payers)
- `/api/v1/timeSeries/*` — top-level time-series endpoint

Options class: `src/TimeSeries.SettlApi/Configuration/SettlApiOptions.cs`.

---

## Fetch full swagger (if needed)

The public Settl specs are in `DataMigration/docs/billing_public_api.json` and `DataMigration/docs/market_public_api.json`. Settl's internal API descriptions are not published in this repository; get them from Settl.

Or query Settl's live swagger endpoint if available.
