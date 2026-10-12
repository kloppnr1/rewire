using System.Text.RegularExpressions;

namespace DataMigration.OpsCli.Services;

internal static partial class AborabatAddressEvidence
{
    public static string CandidateSummary(XellentChargerAddressEvidence? evidence) =>
        evidence is null ? "" : string.Join(" | ", evidence.MeteringPointCandidates
            .Select(c => $"{c.Street} {c.HouseNumber}{c.HouseLetter}, {c.PostalCode} (DAR: {c.DarId ?? "mangler"}; {c.SourceRecord})"));

    public static string Explain(XellentChargerAddressEvidence? evidence, bool? mapperMatch = null)
    {
        if (evidence is null) return "Xellent-adresser mangler i dette offline-udtræk.";
        if (evidence.MeteringPointCandidates.Count == 0)
            return "Xellent-udtrækket indeholder ingen El-adresse for dette målepunkt og denne kunde.";
        var outcomes = evidence.MeteringPointCandidates.Select(c => Compare(evidence, c)).Distinct().ToList();
        if (outcomes.Count > 1 || evidence.MeteringPointCandidates.Count > 1)
            return "Flere adressekandidater i Xellent: " + string.Join(" | ",
                evidence.MeteringPointCandidates.Select(c => $"{c.SourceRecord}: {Compare(evidence, c)}"));
        if (mapperMatch == false && outcomes[0].Contains("passer", StringComparison.OrdinalIgnoreCase))
            return "Xellents adresser ser ens ud, men laderens registrerede adressetilknytning afviser målepunktet. Afklar forskellen i Xellent.";
        return outcomes[0];
    }

    private static string Compare(XellentChargerAddressEvidence charger, XellentMpAddressCandidate mp)
    {
        if (UsableDar(charger.InstallationDarId, out var chargerDar)
            && UsableDar(mp.DarId, out var mpDar))
            return chargerDar == mpDar
                ? "Lader og målepunkt har samme DAR-id i Xellent."
                : $"DAR-id er forskellige i Xellent: lader {charger.InstallationDarId}; målepunkt {mp.DarId}.";
        var chargerStreet = Normalize((charger.InstallationAddress ?? "").Split(',')[0]);
        var mpStreet = mp.HouseNumber is > 0
            ? Normalize($"{mp.Street} {mp.HouseNumber}{mp.HouseLetter?.Trim()}") : "";
        if (chargerStreet.Length == 0 || string.IsNullOrWhiteSpace(mp.Street) || mpStreet.Length == 0)
            return "Xellent mangler gade eller husnummer på lader eller målepunkt; adresserne kan ikke sammenlignes.";
        return chargerStreet.StartsWith(mpStreet, StringComparison.Ordinal)
            && (chargerStreet.Length == mpStreet.Length || !char.IsLetterOrDigit(chargerStreet[mpStreet.Length]))
            ? "Gade og husnummer passer efter normalisering af mellemrum i Xellent."
            : $"Gade eller husnummer er forskelligt i Xellent: lader {charger.InstallationAddress}; målepunkt {mp.Street} {mp.HouseNumber}{mp.HouseLetter}, {mp.PostalCode}.";
    }

    private static bool UsableDar(string? raw, out Guid id) =>
        Guid.TryParse(raw, out id) && id != Guid.Empty;

    private static string Normalize(string value) =>
        DigitLetter().Replace(Whitespace().Replace(value.Trim(), " ").ToLowerInvariant(), "$1$2");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"(\d)\s+([a-zæøå])\b", RegexOptions.CultureInvariant)]
    private static partial Regex DigitLetter();
}
