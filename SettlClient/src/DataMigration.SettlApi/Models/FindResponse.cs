namespace DataMigration.SettlApi.Models;

public class FindResponse<T>
{
    public List<T> Items { get; set; } = [];
    public CountResult? CountResult { get; set; }
    public string? NextUri { get; set; }
    public string? PreviousUri { get; set; }
}

public class CountResult
{
    public int Count { get; set; }
}
