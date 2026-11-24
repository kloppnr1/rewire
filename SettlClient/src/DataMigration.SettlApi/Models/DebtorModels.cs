namespace DataMigration.SettlApi.Models;

public class CustomerAccountInfoDto
{
    public Guid CustomerId { get; set; }
    public List<DebtorBalanceDto> Balances { get; set; } = [];
    public Guid DebtorId { get; set; }
    public string DebtorIdentifier { get; set; } = string.Empty;
    public Guid DebtorBelongsTo { get; set; }
}

public class DebtorBalanceDto
{
    public string Unit { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

public class DebtorAccountEntryDto
{
    public Guid DebtorId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset DueDate { get; set; }
    public decimal Balance { get; set; }
    public string Currency { get; set; } = string.Empty;
    public Guid ReferenceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public Guid PayerId { get; set; }
    public List<AccountEntryLineDto> AccountEntryLines { get; set; } = [];
    public decimal OriginalAmount { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public Guid Id { get; set; }
}

public class AccountEntryLineDto
{
    public int LineNumber { get; set; }
    public Guid ReferenceId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>
    /// Min-value (0001-01-01) = not yet paid. Check before displaying.
    /// </summary>
    public DateTimeOffset PaymentDate { get; set; }
    public DateTimeOffset TransactionDate { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ProcessingMode { get; set; } = string.Empty;
    public Guid TransactionId { get; set; }
    public bool IsCanceled { get; set; }
}

public class FindDebtorEntriesResponse
{
    public List<DebtorAccountEntryDto> Items { get; set; } = [];
    public DebtorCountResult? CountResult { get; set; }
}

public class DebtorCountResult
{
    public int Matches { get; set; }
    public bool IsComplete { get; set; }
    public bool IsBasedOnCountQuery { get; set; }
    public bool IsApproximated { get; set; }
}
