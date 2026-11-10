using DataMigration.Migration.Services.Extraction;

namespace DataMigration.Migration.Tests.Extraction;

public class MissingIdentityClassifierTests
{
    [Fact]
    public void Alternative_number_with_cvr_code_is_a_cvr()
    {
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "", rawCpr: "", alternativeNumber: "29189668", alternativeCode: 2);

        Assert.Equal(MissingIdentityCause.CvrInAlternativeNumber, cause);
    }

    [Fact]
    public void Alternative_number_with_cpr_code_is_a_cpr()
    {
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "", rawCpr: "", alternativeNumber: "0101801234", alternativeCode: 6);

        Assert.Equal(MissingIdentityCause.CprInAlternativeNumber, cause);
    }

    [Fact]
    public void Code_decides_not_digit_count()
    {
        // Otte cifre under CPR-koden er ikke et CVR. Læste vi længden i stedet for koden,
        // ville den her lande i den forkerte gruppe — og det er hele pointen med at gruppere.
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "", rawCpr: "", alternativeNumber: "36665030", alternativeCode: 6);

        Assert.Equal(MissingIdentityCause.CprInAlternativeNumber, cause);
    }

    [Fact]
    public void Unknown_code_is_not_guessed_at()
    {
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "", rawCpr: "", alternativeNumber: "10004641", alternativeCode: 255);

        Assert.Equal(MissingIdentityCause.UninterpretedAlternativeCode, cause);
    }

    [Fact]
    public void Malformed_cvr_is_told_apart_from_an_empty_one()
    {
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "1234567", rawCpr: "", alternativeNumber: null, alternativeCode: 0);

        Assert.Equal(MissingIdentityCause.MalformedCvr, cause);
    }

    [Fact]
    public void Malformed_cpr_is_told_apart_from_an_empty_one()
    {
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "", rawCpr: "010180", alternativeNumber: null, alternativeCode: 0);

        Assert.Equal(MissingIdentityCause.MalformedCpr, cause);
    }

    [Fact]
    public void A_usable_alternative_number_beats_a_typo_in_the_cvr_field()
    {
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: "1234567", rawCpr: "", alternativeNumber: "29189668", alternativeCode: 2);

        Assert.Equal(MissingIdentityCause.CvrInAlternativeNumber, cause);
    }

    [Fact]
    public void Every_field_empty_is_its_own_answer()
    {
        Assert.Equal(
            MissingIdentityCause.Nothing,
            MissingIdentityClassifier.Classify(null, null, null, 0));
        Assert.Equal(
            MissingIdentityCause.Nothing,
            MissingIdentityClassifier.Classify("  ", "  ", "  ", 0));
    }

    [Fact]
    public void A_code_without_a_number_says_nothing()
    {
        // Koden alene er ikke en identifikation — står feltet tomt, er der intet at hente.
        var cause = MissingIdentityClassifier.Classify(
            rawCvr: null, rawCpr: null, alternativeNumber: "", alternativeCode: 2);

        Assert.Equal(MissingIdentityCause.Nothing, cause);
    }

    [Fact]
    public void Every_cause_has_a_heading()
    {
        foreach (MissingIdentityCause cause in Enum.GetValues<MissingIdentityCause>())
            Assert.False(string.IsNullOrWhiteSpace(MissingIdentityClassifier.Describe(cause)));
    }
}
