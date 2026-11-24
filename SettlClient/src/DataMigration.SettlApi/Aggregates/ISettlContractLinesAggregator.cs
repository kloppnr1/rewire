namespace DataMigration.SettlApi.Aggregates;

public interface ISettlContractLinesAggregator
{
    /// <summary>
    /// Fetches contract lines for <paramref name="contractId"/> and enriches each line with
    /// the resolved price-list name and product-element standard definition.
    /// </summary>
    Task<(List<ContractLineView> Lines, string? Error)> AggregateAsync(
        Guid contractId, CancellationToken ct = default);
}
