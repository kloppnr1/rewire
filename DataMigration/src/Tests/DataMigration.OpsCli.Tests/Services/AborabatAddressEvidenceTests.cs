using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class AborabatAddressEvidenceTests
{
    private static XellentChargerAddressEvidence Evidence(string? chargerDar, string? address,
        params XellentMpAddressCandidate[] candidates) =>
        new(chargerDar, address, "8930", "EXU_INSTALLATION[test]", [.. candidates]);

    private static XellentMpAddressCandidate Candidate(string? dar, string? street,
        int? number, string? letter = null) =>
        new("A1", "D1", street, number, letter, "8930", dar, "EXU_DELPOINT[test]");

    [Fact]
    public void Whitespace_does_not_create_an_address_difference()
    {
        var evidence = Evidence(null, "Kongelysdalen  21, 8930 Randers NØ",
            Candidate(null, "Kongelysdalen", 21));
        Assert.Contains("passer", AborabatAddressEvidence.Explain(evidence));
    }

    [Fact]
    public void Different_dar_ids_explain_mismatch_even_when_street_matches()
    {
        var evidence = Evidence("11111111-1111-1111-1111-111111111111", "Kongelysdalen 21",
            Candidate("22222222-2222-2222-2222-222222222222", "Kongelysdalen", 21));
        Assert.Contains("DAR-id er forskellige", AborabatAddressEvidence.Explain(evidence));
    }

    [Fact]
    public void Different_streets_explain_mismatch()
    {
        var evidence = Evidence(null, "Kongelysdalen 21", Candidate(null, "Enebærstien", 12));
        Assert.Contains("Gade eller husnummer er forskelligt", AborabatAddressEvidence.Explain(evidence));
    }

    [Fact]
    public void Several_candidates_are_explicit()
    {
        var evidence = Evidence(null, "Kongelysdalen 21",
            Candidate(null, "Kongelysdalen", 21), Candidate(null, "Enebærstien", 12));
        Assert.Contains("Flere adressekandidater", AborabatAddressEvidence.Explain(evidence));
    }

    [Fact]
    public void Missing_values_are_explicit()
    {
        Assert.Contains("ingen El-adresse", AborabatAddressEvidence.Explain(Evidence(null, "Kongelysdalen 21")));
        Assert.Contains("mangler gade", AborabatAddressEvidence.Explain(
            Evidence(null, null, Candidate(null, null, null))));
    }
}
