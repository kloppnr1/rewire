# Settl API Integration Mapping

This document maps DataMigration.Api endpoints and CLI operations to their corresponding Settl API endpoints.

---

## Already Implemented (DataMigration.SettlImport)

The `DataMigration.SettlImport` project already has working Settl API integration for time series and price list imports.

### Implemented Endpoints

| Endpoint | Purpose | Status |
|----------|---------|--------|
| `POST /connect/token` | OAuth2 authentication | ✅ Implemented |
| `GET /api/v3/meteringPoints?MeteringPointIdentification={id}` | Lookup metering point GUID | ✅ Implemented |
| `GET /api/v3/meteringPoints/{guid}/timeSeries` | Get time series for metering point | ✅ Implemented |
| `POST /api/v1/timeSeries/{guid}/points` | Import time series data points | ✅ Implemented |
| `GET /api/v1/marketPriceLists?Type={type}&Identifier={id}` | Lookup price list GUID | ✅ Implemented |
| `POST /api/v3/meteringPoints/{guid}/marketPriceLists` | Create price list associations | ✅ Implemented |

### Existing Services (reusable)

| Service | Location | Purpose |
|---------|----------|---------|
| `SettlAuthenticationService` | `SettlImport/Services/` | OAuth2 token management with caching |
| `SettlApiClient` | `SettlImport/Services/` | HTTP client with retry/backoff |

### Configuration

```yaml
SettlApi:
  BaseUrl: "http://vm-verdo-ft1-01"
  Ports:
    Market: 4002
    Connect: 4003
  Credentials:
    ClientId: "..."
    ClientSecret: "..."
    Username: "..."
    Password: "..."
    Scope: "BIO_Client BIO_Api"
```

---

## DataMigration.Api → Settl Endpoints (not implemented)

### Customer Search

| DataMigration.Api | Settl Endpoint | Notes |
|-------------------|----------------|-------|
| `GET /api/customers/search` | `GET /tenant-api/v1/customers` | Search customers |
| | `GET /tenant-api/v1/payers` | Search payers (search both) |

### Contracts

| DataMigration.Api | Settl Endpoint | Notes |
|-------------------|----------------|-------|
| `GET /api/customers/{id}/contracts` | `GET /api/v1/contracts/customer/{customerId}/customer-contracts` | List customer contracts |

### Invoices

| DataMigration.Api | Settl Endpoint | Notes |
|-------------------|----------------|-------|
| `GET /api/customers/{id}/invoices` | ❓ **Not found** | No payer/customer filter - ask vendor |
| `GET /api/customers/{id}/invoices/{invoiceId}/pdf` | ❓ **Not found** | Ask vendor |
| `POST /api/customers/{id}/invoices/{invoiceId}/send-email` | N/A | Keep local (Microsoft Graph API) |

### Documents/Notes

| DataMigration.Api | Settl Endpoint | Notes |
|-------------------|----------------|-------|
| `GET /api/customers/{id}/documents` | ❓ **Not found** | Ask vendor |
| `GET /api/customers/{id}/notes` | ❓ **Not found** | Only PUT exists |
| `POST /api/customers/{id}/notes` | `PUT /api/v2/customers/{customerId}/notes` | Update only (no create) |
| `GET /api/customers/{id}/attachments` | ❓ **Not found** | Ask vendor |

### Timeseries

| DataMigration.Api | Settl Endpoint | Notes |
|-------------------|----------------|-------|
| `GET /api/customers/{id}/timeseries` | `GET /api/v1/timeSeries/{timeSeriesId}/points` | Via metering point lookup |

### Sales Journal (Customer Creation)

| DataMigration.Api | Settl Endpoint | Notes |
|-------------------|----------------|-------|
| `POST /api/salesjournal` | `POST /api/v1/Payers` | Create payer |
| | `POST /api/v1/Contracts` | Create contract |
| | `POST /api/v1/businessProcesses/ChangeOfSupplier/...` | Supplier switch |
| `GET /api/salesjournal/options` | `GET /api/v1/products` | List available products |

---

## CLI Timeseries → Settl Endpoints

### Metering Points

| CLI Operation | Xellent Source | Settl Endpoint | Status |
|---------------|----------------|----------------|--------|
| Lookup by ID | Profile/param | `GET /api/v3/meteringPoints?MeteringPointIdentification={id}` | ✅ SettlImport |
| Get children | `EXU_DELPOINT2DELPOINTRELA` | `GET /api/v3/meteringPoints/{meteringPointId}/children` | ❌ Not implemented |

### Time Series

| CLI Operation | Xellent Source | Settl Endpoint | Status |
|---------------|----------------|----------------|--------|
| Get TS GUID | N/A | `GET /api/v3/meteringPoints/{mpGuid}/timeSeries` | ✅ SettlImport |
| Import points | `EMS_TIMESERIESVALUES` | `POST /api/v1/timeSeries/{timeSeriesId}/points` | ✅ SettlImport |
| Fetch points | N/A | `GET /api/v1/timeSeries/{timeSeriesId}/points?Start={date}&End={date}` | ❌ Not implemented |

### Price Lists

| CLI Operation | Settl Endpoint | Status |
|---------------|----------------|--------|
| Lookup by identifier | `GET /api/v1/marketPriceLists?Type={type}&Identifier={id}` | ✅ SettlImport |
| Create association | `POST /api/v3/meteringPoints/{guid}/marketPriceLists` | ✅ SettlImport |

### ExternallyInvoiced Periods (not implemented)

| Operation | Settl Endpoint |
|-----------|----------------|
| List | `GET /api/v1/meteringPoints/{meteringPointId}/externallyInvoicedPeriods` |
| Add | `POST /api/v1/meteringPoints/{meteringPointId}/externallyInvoicedPeriods` |
| Delete all | `DELETE /api/v1/meteringPoints/{meteringPointId}/externallyInvoicedPeriods` |
| Get single | `GET /api/v1/meteringPoints/{meteringPointId}/externallyInvoicedPeriods/{periodId}` |
| Delete single | `DELETE /api/v1/meteringPoints/{meteringPointId}/externallyInvoicedPeriods/{periodId}` |

---

## Request Schemas

### AddExternallyInvoicedPeriodsRequest

From `swagger-billing-internal.json`:

```json
{
  "TransactionId": "uuid",
  "Periods": [
    {
      "Period": {
        "Start": "2024-01-01T00:00:00Z",
        "End": "2024-12-31T23:59:59Z"
      },
      "BillableTypes": ["Energy", "Grid"],
      "Revision": "2024-01-01T00:00:00Z",
      "TimeSeriesId": "uuid"
    }
  ]
}
```

### BillableTypes Enum

Valid values for the `BillableTypes` array:
- `Energy`
- `Grid`
- (additional values TBD from swagger)

---

## Missing Endpoints (Vendor Request Required)

The following functionality is not available in the current Settl API documentation:

| Feature | Current Xellent Implementation | Settl Status |
|---------|-------------------------------|--------------|
| Invoices by payer/customer | Direct DB query | SettlementExports only filters by SettlementId/InvoiceId |
| Invoice PDF download | Local file storage | Not found in swagger |
| Customer documents | Local file storage | Not found |
| Customer attachments | Local file storage | Not found |
| Customer notes (GET) | Direct DB query | Only PUT endpoint exists |

See [settl-vendor-request.md](settl-vendor-request.md) for the vendor communication template.

---

## Implementation Notes

### Authentication (already implemented in SettlImport)

- OAuth2 client credentials + password grant
- Token endpoint: `POST {BaseUrl}:{ConnectPort}/connect/token`
- Scope: `BIO_Client BIO_Api`
- Token caching with 5-minute expiry buffer
- See `SettlAuthenticationService` for implementation

### Base URLs

| Service | Port | Endpoints |
|---------|------|-----------|
| Connect (Auth) | 4003 | `/connect/token` |
| Market | 4002 | `/api/v1/`, `/api/v3/`, `/tenant-api/v1/` |
| Billing Internal | TBD | `/api/v1/` (separate swagger) |

### Versioning

- Multiple API versions coexist (v1, v2, v3)
- Use the specific version documented for each endpoint
