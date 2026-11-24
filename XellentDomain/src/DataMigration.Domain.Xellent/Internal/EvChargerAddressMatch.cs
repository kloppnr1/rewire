using System.Text.RegularExpressions;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Decides whether a charger installation sits at a candidate metering point's address. Pure, so the
/// rule can be tested without a database.
///
/// <para>Two comparisons, in order. The DAR id first: when both sides carry a real one (non-blank,
/// not the all-zero id Xellent also stores), equal ids are a match and different ids are not — the
/// street text is not consulted. Otherwise the street: the installation's formatted address is cut
/// at the first comma, normalised, and must begin with the metering point's "street housenumber
/// letter" as a whole word — so a trailing floor or side ("st.", "1. th") on the installation does
/// no harm, while "Fiktivgaden 1" never matches "Fiktivgaden 10" or "Fiktivgaden 1A". Blank on either side means
/// the two cannot be compared, and the answer is <c>false</c>.</para>
/// </summary>
internal static class EvChargerAddressMatch
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>"83 A" → "83A": Xellent writes the house letter with and without a space between.</summary>
    private static readonly Regex DigitSpaceLetter = new(@"(\d)\s+([a-zæøå])\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the installation's address is the metering point's; false when it is not, or when
    /// either side lacks what it takes to tell.
    /// </summary>
    public static bool Matches(string? installationDarId, string? installationAddress, EvChargerDelpointAddress? meteringPoint)
    {
        if (meteringPoint is null) return false;

        if (TryDarId(installationDarId, out var installationDar) && TryDarId(meteringPoint.DarId, out var meteringPointDar))
            return installationDar == meteringPointDar;

        var line = StreetLine(installationAddress);
        var key = StreetKey(meteringPoint.Street, meteringPoint.HouseNumber, meteringPoint.HouseLetter);
        return line is not null && key is not null && StartsWithWholeWords(line, key);
    }

    /// <summary>A usable DAR id: a parseable Guid that is not all zeros.</summary>
    public static bool TryDarId(string? raw, out Guid id)
    {
        id = Guid.Empty;
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw.Trim(), out id) && id != Guid.Empty;
    }

    /// <summary>
    /// The street part of a formatted installation address — everything before the first comma —
    /// normalised. Null when blank.
    /// </summary>
    public static string? StreetLine(string? formattedAddress)
    {
        if (string.IsNullOrWhiteSpace(formattedAddress)) return null;
        var comma = formattedAddress.IndexOf(',');
        var normalized = Normalize(comma < 0 ? formattedAddress : formattedAddress[..comma]);
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// The metering point's street and house number as one normalised string ("fiktivgadeo 83a"). Null
    /// when the street is blank or there is no house number — nothing to compare against then.
    /// </summary>
    public static string? StreetKey(string? street, int? houseNumber, string? houseLetter)
    {
        if (string.IsNullOrWhiteSpace(street) || houseNumber is null or <= 0) return null;
        return Normalize($"{street} {houseNumber}{houseLetter?.Trim()}");
    }

    /// <summary>Trim, lower-case, collapse whitespace, and join a house letter onto its number.</summary>
    public static string Normalize(string text)
    {
        var collapsed = Whitespace.Replace(text.Trim(), " ").ToLowerInvariant();
        return DigitSpaceLetter.Replace(collapsed, "$1$2");
    }

    private static bool StartsWithWholeWords(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal)
        && (line.Length == key.Length || !char.IsLetterOrDigit(line[key.Length]));
}
