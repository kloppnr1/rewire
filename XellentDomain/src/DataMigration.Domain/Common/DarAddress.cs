namespace DataMigration.Domain.Common;

/// <summary>
/// Reference to an address registered in Danmarks Adresseregister (DAR), the Danish national
/// address registry. The identifier is the stable UUID assigned by DAR.
/// </summary>
public sealed record DarAddress
{
    public Guid Identifier { get; }

    public DarAddress(Guid identifier)
    {
        if (identifier == Guid.Empty)
            throw new ArgumentException("DAR identifier must not be Guid.Empty.", nameof(identifier));
        Identifier = identifier;
    }
}
