using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The pure address comparison behind the charger tie-break: DAR id when both sides have a real one,
/// else the street part of the installation's formatted address against the delpoint's street and
/// house number. Formatted addresses follow Xellent's shape "Street  83, st th, 8960 City".
/// </summary>
public class EvChargerAddressMatchTests
{
    private const string DarA = "31876bdc-5a0b-4584-9091-701e5bde6153";
    private const string DarB = "649762c9-dcf0-4759-a175-8756d98a92b1";
    private const string DarZero = "00000000-0000-0000-0000-000000000000";

    private static EvChargerDelpointAddress At(string? street, int? number, string? letter = "", string? dar = "")
        => new(dar, street, number, letter);

    // ── Normalisation ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fiktivvejm  83", "fiktivvejm 83")]
    [InlineData("  FIKTIVVEJM 83  ", "fiktivvejm 83")]
    [InlineData("Fiktivvejm\t 83 A", "fiktivvejm 83a")]
    [InlineData("Fiktivvejbfh 12 b", "fiktivvejbfh 12b")]
    [InlineData("Fiktivvejm 83 st", "fiktivvejm 83 st")]
    public void Normalize_lower_cases_collapses_whitespace_and_joins_the_house_letter(string raw, string expected)
        => Assert.Equal(expected, EvChargerAddressMatch.Normalize(raw));

    [Theory]
    [InlineData("Fiktivvejm  83, 8960 Randers SØ", "fiktivvejm 83")]
    [InlineData("Fiktivvejek  23, st th, 8260 Viby J", "fiktivvejek 23")]
    [InlineData("Fiktivveja   3", "fiktivveja 3")]
    public void StreetLine_takes_the_part_before_the_first_comma(string formatted, string expected)
        => Assert.Equal(expected, EvChargerAddressMatch.StreetLine(formatted));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", 8960 Randers SØ")]
    public void StreetLine_is_null_when_there_is_no_street(string? formatted)
        => Assert.Null(EvChargerAddressMatch.StreetLine(formatted));

    [Fact]
    public void StreetKey_joins_street_number_and_letter()
    {
        Assert.Equal("fiktivvejm 83", EvChargerAddressMatch.StreetKey("Fiktivvejm ", 83, ""));
        Assert.Equal("fiktivvejm 83a", EvChargerAddressMatch.StreetKey("Fiktivvejm", 83, "A"));
        Assert.Equal("fiktivvejm 83a", EvChargerAddressMatch.StreetKey("Fiktivvejm", 83, " a "));
    }

    [Fact]
    public void StreetKey_is_null_without_a_street_or_a_house_number()
    {
        Assert.Null(EvChargerAddressMatch.StreetKey(null, 83, ""));
        Assert.Null(EvChargerAddressMatch.StreetKey("  ", 83, ""));
        Assert.Null(EvChargerAddressMatch.StreetKey("Fiktivvejm", null, ""));
        Assert.Null(EvChargerAddressMatch.StreetKey("Fiktivvejm", 0, ""));
    }

    // ── DAR id ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DarA, true)]
    [InlineData(" " + DarA + " ", true)]
    [InlineData(DarZero, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-a-guid", false)]
    public void TryDarId_accepts_only_a_real_non_zero_guid(string? raw, bool expected)
        => Assert.Equal(expected, EvChargerAddressMatch.TryDarId(raw, out _));

    [Fact]
    public void Equal_dar_ids_match_whatever_the_street_text_says()
        => Assert.True(EvChargerAddressMatch.Matches(DarA, "Fiktivgadep 1, 8000 Aarhus C", At("Fiktivvejm", 83, dar: DarA)));

    [Fact]
    public void Different_dar_ids_do_not_match_even_when_the_street_text_agrees()
        => Assert.False(EvChargerAddressMatch.Matches(DarA, "Fiktivvejm 83, 8960 Randers SØ", At("Fiktivvejm", 83, dar: DarB)));

    [Fact]
    public void An_all_zero_dar_id_on_either_side_falls_through_to_the_street()
    {
        Assert.True(EvChargerAddressMatch.Matches(DarZero, "Fiktivvejm 83, 8960 Randers SØ", At("Fiktivvejm", 83, dar: DarA)));
        Assert.True(EvChargerAddressMatch.Matches(DarA, "Fiktivvejm 83, 8960 Randers SØ", At("Fiktivvejm", 83, dar: DarZero)));
        Assert.False(EvChargerAddressMatch.Matches(DarZero, "Fiktivgadep 1, 8000 Aarhus C", At("Fiktivvejm", 83, dar: DarZero)));
    }

    [Fact]
    public void A_missing_dar_id_on_one_side_falls_through_to_the_street()
        => Assert.True(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83, 8960 Randers SØ", At("Fiktivvejm", 83, dar: DarA)));

    // ── Street ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fiktivvejm  83, 8960 Randers SØ")]
    [InlineData("fiktivvejm 83")]
    [InlineData("Fiktivvejm 83, st th, 8960 Randers SØ")]
    [InlineData("Fiktivvejm 83 st.")]
    [InlineData("Fiktivvejm 83 1. th")]
    [InlineData("Fiktivvejm 83, 1. th")]
    public void Street_and_house_number_match_with_a_trailing_floor_or_side_ignored(string formatted)
        => Assert.True(EvChargerAddressMatch.Matches(null, formatted, At("Fiktivvejm", 83)));

    [Fact]
    public void The_house_letter_matches_with_or_without_a_space()
    {
        Assert.True(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83A, 8960 Randers SØ", At("Fiktivvejm", 83, "A")));
        Assert.True(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83 a, 8960 Randers SØ", At("Fiktivvejm", 83, "A")));
    }

    [Theory]
    [InlineData("Fiktivvejm 8, 8960 Randers SØ")]
    [InlineData("Fiktivvejm 830, 8960 Randers SØ")]
    [InlineData("Fiktivvejm 83A, 8960 Randers SØ")]
    [InlineData("Fiktivvejm 85, 8960 Randers SØ")]
    [InlineData("Fiktivgadep 83, 8960 Randers SØ")]
    [InlineData("Hjortevejen 83, 8960 Randers SØ")]
    public void A_different_number_letter_or_street_does_not_match(string formatted)
        => Assert.False(EvChargerAddressMatch.Matches(null, formatted, At("Fiktivvejm", 83)));

    [Fact]
    public void A_delpoint_letter_the_installation_lacks_does_not_match()
        => Assert.False(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83, 8960 Randers SØ", At("Fiktivvejm", 83, "A")));

    [Fact]
    public void Blank_on_either_side_cannot_be_told_and_is_false()
    {
        Assert.False(EvChargerAddressMatch.Matches(null, null, At("Fiktivvejm", 83)));
        Assert.False(EvChargerAddressMatch.Matches(null, "  ", At("Fiktivvejm", 83)));
        Assert.False(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83", At("", 83)));
        Assert.False(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83", At("Fiktivvejm", null)));
        Assert.False(EvChargerAddressMatch.Matches(null, "Fiktivvejm 83", null));
    }
}
