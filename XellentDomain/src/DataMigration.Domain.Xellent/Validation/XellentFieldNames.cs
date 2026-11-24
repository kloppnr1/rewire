namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// Maps Settl/domain field names to the Xellent column path they originate from.
/// Validators use this so error messages and <c>ValidationEntry.XellentField</c>
/// stay consistent with the rest of the codebase (<c>SettlExtractionService.Validation.cs</c>).
///
/// <para>Add entries here when new validators reference fields not yet listed —
/// keeping the lookup central avoids drift between validators that target the same
/// Settl field.</para>
/// </summary>
public static class XellentFieldNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["Name"] = "CustTable.Name / Namealias",
        ["StreetName"] = "Address.Street / ExuStreet.Streetname",
        ["PostalCode"] = "Address.Zipcode / ExuDelpoint.Zipcodeid",
        ["City"] = "Address.City / AddressZipCode.City",
        ["NationalIdentificationNumber"] = "CustTable.ExuCprDisponent1 / Exualternativecustomernumber"
    };

    /// <summary>Returns the Xellent column path for a Settl field, or the field name itself if not registered.</summary>
    public static string For(string settlField) =>
        Map.TryGetValue(settlField, out var xel) ? xel : settlField;

    /// <summary>The set of Settl fields with a registered Xellent mapping. Useful for unit-testing coverage.</summary>
    public static IReadOnlyCollection<string> RegisteredFields => Map.Keys;
}
