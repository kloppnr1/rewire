# Settl API - Endpoint-oversigt

## Baggrund

Vi er i gang med at migrere fra Xellent til Settl. Dette dokument viser de Settl API endpoints vi har identificeret og planlægger at bruge.

---

## Endpoints vi skal bruge

### Kunder

| `GET /tenant-api/v1/customers` | Søg kunder |
| `GET /api/v2/customers/{id}` | Hent enkelt kunde |
| `POST /api/v2/customers` | Opret kunde |
| `GET /api/v2/customers/{id}/meteringPoints` | Hent målepunkter for kunde |
| `POST /api/v2/customers/{id}/meteringPoints` | Opret kunde-målepunkt relation |
| `GET /api/v1/customerGroups` | Hent kundegrupper |

---

### Betalere

| `GET /tenant-api/v1/payers` | Søg betalere |
| `GET /api/v1/Payers/{id}` | Hent enkelt betaler |
| `POST /api/v1/Payers` | Opret betaler |
| `GET /api/v1/Payers/{id}/MeteringPoints` | Hent målepunkter for betaler |
| `POST /api/v1/Payers/{id}/MeteringPoints` | Opret betaler-målepunkt relation |

---

### Kontrakter

| `GET /api/v1/Contracts` | Hent kontrakter |
| `GET /api/v1/contracts/customer/{customerId}/customer-contracts` | Hent kundekontrakter |
| `GET /api/v1/contracts/{id}/meteringPoints` | Hent målepunkter for kontrakt |
| `POST /api/v1/contracts/{id}/meteringPoints` | Opret kontrakt-målepunkt relation |
| `POST /api/v1/Contracts` | Opret kontrakt |

---

### Målepunkter

| `GET /api/v3/meteringPoints` | Søg målepunkter |
| `GET /api/v3/meteringPoints/{id}` | Hent enkelt målepunkt |
| `GET /api/v3/meteringPoints/{id}/full` | Hent komplet målepunkt |
| `POST /api/v3/meteringPoints` | Opret målepunkt |
| `GET /api/v3/meteringPoints/{id}/children` | Hent child målepunkter |
| `GET /api/v3/meteringPoints/{id}/registerConfigurations` | Hent målerkonfiguration |
| `GET /api/v3/meteringPoints/{id}/timeSeries` | Hent tidsserier |

---

### Tidsserier

| `GET /api/v1/timeSeries/{timeSeriesId}/points` | Hent tidsserie-punkter |
| `POST /api/v1/timeSeries/{timeSeriesId}/points` | Import af tidsserie-punkter |

---

### Ekstern afregnet perioder

| `GET /api/v1/meteringPoints/{id}/externallyInvoicedPeriods` | Hent perioder |
| `POST /api/v1/meteringPoints/{id}/externallyInvoicedPeriods` | Opret periode |
| `DELETE /api/v1/meteringPoints/{id}/externallyInvoicedPeriods` | Slet periode |

---

### Fakturaer

| `GET /api/v1/SettlementExports` | Hent fakturaer |
| `GET /api/v1/SettlementExports/{id}/invoices` | Hent fakturaer for settlement |

---

### Produkter

| `GET /api/v1/products` | Hent produktliste |

---

### Business Processes

| `POST /api/v1/businessProcesses/ChangeOfSupplier/...` | Leverandørskifte |
| `POST /api/v1/businessProcesses/MoveIn/...` | Tilflytning |
| `POST /api/v1/businessProcesses/MoveInSecondary/...` | Tilflytning sekundær |
| `POST /api/v1/businessProcesses/MoveOut/...` | Fraflytning |

