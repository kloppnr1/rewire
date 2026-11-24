using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlTimeSeriesClient
{
    Task<(bool Success, string? Error)> PostPointsAsync(
        Guid timeSeriesGuid, SettlTimeSeriesRequestDto payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter de rå Settl-points for en TS i et tidsvindue — inkl. tid, mængde, kvalitet
    /// og enhed. Bruges når kalderen skal vise eller behandle de faktiske forbrugsværdier
    /// (fx forbrugsvisning i Bifrost), ikke kun et antals-summary.
    /// </summary>
    Task<(SettlTimeSeriesPointsResponseDto? Points, string? Error)> GetPointsAsync(
        Guid timeSeriesGuid,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter eksisterende Settl-points for en TS i et tidsvindue. Bruges fx af
    /// dashboard til at sammenligne hvad der ville blive postet med hvad Settl
    /// allerede har — afslører overlap/dubletter.
    /// </summary>
    Task<(SettlPointsSummaryDto? Summary, string? Error)> GetPointsSummaryAsync(
        Guid timeSeriesGuid,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default);
}
