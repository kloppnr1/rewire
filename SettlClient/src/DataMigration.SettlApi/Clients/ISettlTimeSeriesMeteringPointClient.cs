using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlTimeSeriesMeteringPointClient : ISettlMeteringPointClient
{
    Task<(List<TimeSeriesInfoDto>? TimeSeries, string? Error)> GetTimeSeriesAsync(
        Guid meteringPointGuid, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> AddExternallyInvoicedPeriodsAsync(
        Guid meteringPointId, AddExternallyInvoicedPeriodsRequest request, CancellationToken cancellationToken = default);
}
