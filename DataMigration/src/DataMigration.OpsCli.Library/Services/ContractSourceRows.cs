namespace DataMigration.OpsCli.Services;

// Independent sections of the state snapshot. Invoice-state replay does not load these files.
internal sealed record XellentContractPartRow(string MeteringPoint, string Account,
    string ContractNumber, string AgreementNumber, string ProductNumber,
    DateTime Start, DateTime End, string Source, DateTime? LoggedAt,
    string? ChangeType = null)
{
    public string? CompanyId { get; init; }
    public string? AmrCode { get; init; }
    public string? LogEmployeeId { get; init; }
    public long? LogRecordId { get; init; }
}

internal sealed record SettlContractLinkRow(string MeteringPoint, Guid ContractId,
    string ContractName, DateTimeOffset Start, DateTimeOffset End, Guid? AssociationId = null);

internal sealed record XellentChargeSubscriptionRow(string Account, string ContractNumber,
    string ProductNumber, DateTime Start, DateTime End, string Source, DateTime? LoggedAt,
    string? ChangeType = null)
{
    public string? AgreementNumber { get; init; }
    public string? CompanyId { get; init; }
    public string? AmrCode { get; init; }
    public string? LogEmployeeId { get; init; }
    public long? LogRecordId { get; init; }
}
