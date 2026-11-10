namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// The fields the email-split rule reads off a customer, and nothing else.
///
/// <para>Exists so a run that migrates in batches can carry the facts out of each batch and ask
/// the question once, over every account it touched. The rule counts accounts that share a name
/// and address, and a batch holding a slice of such a group can only ever give a slice of the
/// answer — "2 konti" where the group really has 30.</para>
///
/// <para>Taken from the mapped customer, not from raw Xellent: the address a customer migrates
/// with is the legal contact's when there is one, and only otherwise the party address. Rebuilding
/// these from a raw lookup would group different customers together than the migration does.</para>
/// </summary>
public sealed record CustomerEmailFacts(
    string AccountNumber,
    string? Cvr,
    string Name,
    string? AdditionalName,
    string? StreetName,
    string? PostalCode,
    string? City,
    string? Email);
