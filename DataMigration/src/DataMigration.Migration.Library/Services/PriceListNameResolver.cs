using System.Text.RegularExpressions;

namespace DataMigration.Migration.Services;

/// <summary>
/// Translates a Xellent rate code (<c>ExuContractPartTable.AMRCODE</c> or a delivery-group
/// rate code) into the corresponding Settl PriceList <b>name</b> — the same prefix rule the
/// customer-price-list export uses (<c>"KLI " → "ST "</c>).
///
/// <para>This is the CSV-independent half of price-list resolution: the name is pure Xellent
/// data. The Settl PriceList <b>ID</b> (GUID) requires the exported PriceList CSV and is
/// deliberately NOT resolved here — report consumers want the name, not the GUID, and the
/// name alone needs no external input. Consequence: a name produced here is "as Xellent has
/// it" and is <b>not</b> validated against the set of PriceLists that actually exist in Settl.</para>
/// </summary>
public static partial class PriceListNameResolver
{
    /// <summary>
    /// Maps a Xellent rate code to its Settl PriceList name. Codes prefixed <c>"KLI "</c>
    /// become <c>"ST "</c>; a missing space between the letter prefix and the digits is
    /// inserted (Xellent data-entry inconsistency, e.g. <c>"st1000" → "st 1000"</c>, which
    /// then matches <c>"ST 1000"</c> case-insensitively). Casing is left untouched —
    /// matching against the PriceList CSV is case-insensitive.
    /// </summary>
    public static string TranslateAmrcode(string amrcode)
    {
        var translated = amrcode.StartsWith("KLI ", StringComparison.Ordinal)
            ? "ST " + amrcode["KLI ".Length..]
            : amrcode;
        return MissingSpaceRegex().Replace(translated, "$1 $2");
    }

    // Letter prefix immediately followed by digits (no separating space) → insert one space.
    // Codes without digits (e.g. delivery groups "Vest"/"Øst") and already-spaced codes
    // ("ST 1000") are left unchanged.
    [GeneratedRegex(@"^([A-Za-z]+)(\d.*)$")]
    private static partial Regex MissingSpaceRegex();
}
