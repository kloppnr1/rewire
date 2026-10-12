using DataMigration.SettlApi.Clients;
using Microsoft.Extensions.Logging;

namespace DataMigration.SettlApi.Aggregates;

public sealed class SettlContractLinesAggregator(
    ISettlContractClient contractClient,
    ISettlPriceListClient priceListClient,
    ILogger<SettlContractLinesAggregator> logger) : ISettlContractLinesAggregator
{
    public async Task<(List<ContractLineView> Lines, string? Error)> AggregateAsync(
        Guid contractId, CancellationToken ct = default)
    {
        var (lines, err) = await contractClient.GetContractLinesAsync(contractId, ct);
        if (err != null)
        {
            logger.LogWarning("GetContractLinesAsync for {ContractId} failed: {Error}", contractId, err);
            return ([], $"Fejl fra Settl: {err}");
        }
        if (lines == null || lines.Count == 0)
            return ([], null);

        var (pls, _) = await priceListClient.ListPriceListsAsync("DK", ct);
        var priceListMap = (pls ?? []).Where(p => p.Name != null).ToDictionary(p => p.Id, p => p.Name!);

        var (pes, _) = await priceListClient.ListProductElementsAsync("DK", ct);
        var productElementByCode = (pes ?? [])
            .Where(p => p.ProductElementCode != null)
            .GroupBy(p => p.ProductElementCode!)
            .ToDictionary(g => g.Key, g => g.Last());

        var result = lines
            .OrderBy(l => l.Start)
            .Select(l =>
            {
                ContractLineStandard? standard = null;
                if (l.ProductElementCode != null && productElementByCode.TryGetValue(l.ProductElementCode, out var pe))
                {
                    standard = new ContractLineStandard(
                        pe.Description,
                        pe.AverageUnitPriceType,
                        pe.ExternalIdentifier,
                        pe.BillingModel?.PriceModelType,
                        pe.BillingModel?.QuantityModelType,
                        pe.BillingModel?.CalculationModelType,
                        pe.BillingModel?.RoundingModelType,
                        pe.BillingModel?.VatModelType);
                }
                return new ContractLineView(
                    l.ProductName,
                    l.ProductElementCode,
                    l.PriceListId != Guid.Empty && priceListMap.TryGetValue(l.PriceListId, out var plName) ? plName : null,
                    l.Category,
                    l.Start.ToString("yyyy-MM-dd"),
                    l.End.ToString("yyyy-MM-dd"),
                    l.LastUpdated.ToString("yyyy-MM-dd HH:mm"),
                    l.Text,
                    l.UnitPrice?.Value,
                    l.UnitPrice?.CurrencyUnit,
                    l.UnitPrice?.QuantityUnit,
                    l.UnitPrice?.QuantityKind,
                    l.FixedNumber?.Value,
                    l.FixedNumber?.Unit,
                    l.FixedNumber?.Kind,
                    l.Coverage,
                    l.MinimumPrice,
                    l.MaximumPrice,
                    l.ProfitSplit,
                    l.VolumeFlexibility,
                    l.ReportingType,
                    standard);
            })
            .ToList();

        return (result, null);
    }
}
