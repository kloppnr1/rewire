namespace DataMigration.Domain.Xellent;

/// <summary>
/// Parses Xellent's single-field street addresses into street name + building number.
/// Xellent stores the full address as one string (e.g. "Fiktivgadel 16 A, 3 th"); Settl requires
/// separate StreetName and BuildingNumber fields.
/// </summary>
public static class AddressParser
{
    /// <summary>
    /// Returns just the street name portion, stripping a trailing numeric token.
    /// "Fiktivgadel 16" → "Fiktivgadel". "Fiktivgadel" → "Fiktivgadel".
    /// </summary>
    public static string ParseStreetName(string? street)
    {
        if (string.IsNullOrWhiteSpace(street))
            return string.Empty;

        var parts = street.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && int.TryParse(parts[^1], out _))
            return string.Join(" ", parts[..^1]);

        return street.Trim();
    }

    /// <summary>
    /// Splits a full Xellent street string into (streetName, buildingNumber).
    /// Handles letter suffixes ("16 A" → "16A"), comma-separated floor/door info
    /// (", 3 th" is stripped), and multi-word street names ("Gammel Fiktivgadem 13").
    /// </summary>
    public static (string streetName, string buildingNumber) ParseStreetAddress(string street)
    {
        if (string.IsNullOrWhiteSpace(street))
            return (string.Empty, string.Empty);

        // Strip everything after comma (floor/door info like ", 3 th" or ", st tv")
        var commaIdx = street.IndexOf(',');
        var addressPart = commaIdx >= 0 ? street[..commaIdx].Trim() : street.Trim();

        var parts = addressPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
            return (street.Trim(), string.Empty);

        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && char.IsDigit(parts[i][0]))
            {
                var streetName = string.Join(" ", parts[..i]);
                // Building number is the digit part, optionally followed by a single letter suffix
                // "16" → "16", "16 A" → "16A", "110A" → "110A", "13 33" → "13" (33 is door/apt)
                var bldNr = parts[i];
                if (i + 1 < parts.Length)
                {
                    var next = parts[i + 1];
                    if (next.Length == 1 && char.IsLetter(next[0]))
                        bldNr += next;
                }
                return (streetName, bldNr);
            }
        }

        return (street.Trim(), string.Empty);
    }
}
