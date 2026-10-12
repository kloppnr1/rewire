using System.Text.Json.Serialization;

namespace DataMigration.SettlApi.Models;

// === Wire-format DTOs for Settl /v1/time-series/{id}/points POST + GET ===

public class SettlTimeSeriesRequestDto
{
    public Guid TransactionId { get; set; }
    public DateTimeOffset RegistrationTime { get; set; }
    public List<SettlTimeSeriesPointDto> Points { get; set; } = new();
    public DateTimeOffset Revision { get; set; }
    public bool? InvoiceRelevant { get; set; }
    public bool AccountForQuantityQuality { get; set; } = true;
}

public class SettlTimeSeriesPointDto
{
    public DateTime Time { get; set; }
    public decimal Quantity { get; set; }
    public string Quality { get; set; } = "Measured";
}

// GET /v1/time-series/{id}/points returnerer et wrapper-objekt — ikke et rå array.
// Bemærk: POST'ens request-DTO (SettlTimeSeriesRequestDto) har anden struktur.
public class SettlTimeSeriesPointsResponseDto
{
    public string? QuantityUnit { get; set; }
    public List<SettlTimeSeriesPointDto> Points { get; set; } = new();
}

// === Settl /internal-market/api/v3/meteringPoints/{id}/timeSeries GET response ===

public class TimeSeriesInfoDto
{
    public Guid Id { get; set; }
    public TimeResolutionInfoDto? TimeResolution { get; set; }
}

public class TimeResolutionInfoDto
{
    public int Length { get; set; }
    public string Unit { get; set; } = string.Empty;
}

// === Settl /internal-billing/api/v1/meteringPoints/{id}/externallyInvoicedPeriods POST ===

public class AddExternallyInvoicedPeriodsRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public List<ExternallyInvoicedPeriodDto> Periods { get; set; } = [];
}

public class ExternallyInvoicedPeriodDto
{
    public BillingTimePeriodDto? Period { get; set; }
    public List<string> BillableTypes { get; set; } = [];
    public DateTimeOffset? Revision { get; set; }
    public Guid? TimeSeriesId { get; set; }
}

// Billing-domæne period (EIP) — Start/End sendes som DateOnly konverteret til
// Danish-midnight-UTC via DateOnlyUtcConverter (Z-suffix). Settl's billing-API
// afviser "+00:00"-format med Kind=Local — og rå offset-0 vil desuden være 1-2
// timer forskudt ift. dansk midnat afhængigt af sommer-/vintertid.
//
// Forskellig fra <see cref="TimePeriodDto"/> i MeteringPointModels.cs som bruger
// DateTimeOffset til contract/MP-perioder hvor Settl er mere lempelig.
public class BillingTimePeriodDto
{
    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly Start { get; set; }

    [JsonConverter(typeof(DateOnlyUtcConverter))]
    public DateOnly End { get; set; }
}

// === Aggregeret summary fra GetPointsSummaryAsync ===

/// <summary>
/// Aggregeret oversigt over Settl-points for et TS i et givet vindue. Aggregering
/// foregår client-side (Settl returnerer rå points-array), så for store TS'er kan
/// kaldet tage flere sekunder.
/// </summary>
public class SettlPointsSummaryDto
{
    public int Count { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
}
