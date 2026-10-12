namespace DataMigration.Domain.Common;

/// <summary>
/// Contact channels. All fields are optional.
/// </summary>
public sealed record ContactInfo(
    string? Email,
    string? Phone,
    string? Cellphone,
    string? Fax)
{
    public static ContactInfo Empty { get; } = new(null, null, null, null);
}
