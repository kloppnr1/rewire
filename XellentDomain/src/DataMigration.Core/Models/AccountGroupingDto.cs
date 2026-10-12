namespace DataMigration.Core.Models;

public class AccountGroupingDto
{
    public string XellentAccountNumber { get; set; } = string.Empty;
    public string MergedAccountNumber { get; set; } = string.Empty;
    public string? GroupKey { get; set; }
}
