namespace DataMigration.Migration.Services.Extraction;

/// <summary>
/// Hvorfor en kunde står uden identifikation. Rækkefølgen er den rapporten grupperer efter:
/// først det der kan lukkes uden at nogen skal spørge kunden, til sidst det der ikke kan.
/// </summary>
public enum MissingIdentityCause
{
    /// <summary>Et CVR ligger i alternativ-kundenummeret (kode 2). Intet menneske skal spørges.</summary>
    CvrInAlternativeNumber,

    /// <summary>Et CPR ligger i alternativ-kundenummeret (kode 6), men uden det <c>PNR=</c>-præfiks
    /// eller den længde <see cref="DataMigration.Domain.Xellent.IdentifierHelpers.ResolveCpr"/> kræver.</summary>
    CprInAlternativeNumber,

    /// <summary>CVR-feltet er udfyldt, men ikke med 8 cifre — en tastefejl, ikke et hul.</summary>
    MalformedCvr,

    /// <summary>CPR-feltet er udfyldt, men ikke med 10 tegn.</summary>
    MalformedCpr,

    /// <summary>Der står noget i alternativ-kundenummeret under en kode vi ikke tolker.</summary>
    UninterpretedAlternativeCode,

    /// <summary>Alle identifikations-felter er tomme. Her er intet at hente i Xellent.</summary>
    Nothing,
}

/// <summary>
/// Afgør hvorfor en kunde står uden identifikation, ud fra de rå Xellent-værdier.
///
/// <para>Kilden er altid disponent 1 plus alternativ-kundenummeret. Alternativ-nummerets
/// betydning afgøres af dets kode og ikke af hvor mange cifre der tilfældigvis står:
/// <b>kode 2 er CVR, kode 6 er CPR</b>. Det <c>PNR=</c>-præfiks der læses andetsteds er en
/// tekstkonvention oven på koden — en konvention der er blind for kode 2, hvilket er
/// præcis grunden til at 385 af de 1.798 kunder uden identifikation på El-Ekstern har et
/// rigtigt CVR liggende (29189668 er Randers Kommune).</para>
///
/// <para>Alternativ-nummeret vinder over et forkert udfyldt CVR- eller CPR-felt, fordi det
/// bærer et brugbart nummer mens det andet bærer en tastefejl. Ren funktion; den ændrer
/// intet ved hvem der migreres.</para>
/// </summary>
public static class MissingIdentityClassifier
{
    /// <summary>Alternativ-kundenummerets kode for et CVR (<c>EXU_ALTERNATIVECUSTOMERCODE</c>).</summary>
    public const int AlternativeCodeCvr = 2;

    /// <summary>Alternativ-kundenummerets kode for et CPR.</summary>
    public const int AlternativeCodeCpr = 6;

    public static MissingIdentityCause Classify(
        string? rawCvr,
        string? rawCpr,
        string? alternativeNumber,
        int alternativeCode)
    {
        var hasAlternative = !string.IsNullOrWhiteSpace(alternativeNumber);

        if (hasAlternative && alternativeCode == AlternativeCodeCvr) return MissingIdentityCause.CvrInAlternativeNumber;
        if (hasAlternative && alternativeCode == AlternativeCodeCpr) return MissingIdentityCause.CprInAlternativeNumber;
        if (!string.IsNullOrWhiteSpace(rawCvr)) return MissingIdentityCause.MalformedCvr;
        if (!string.IsNullOrWhiteSpace(rawCpr)) return MissingIdentityCause.MalformedCpr;
        if (hasAlternative) return MissingIdentityCause.UninterpretedAlternativeCode;

        return MissingIdentityCause.Nothing;
    }

    /// <summary>
    /// Gruppeoverskriften, på dansk. Teksten hører hjemme her og ikke i den enkelte rapport,
    /// så report.html og dashboardet ikke kommer til at sige hver sit om samme gruppe.
    /// </summary>
    public static string Describe(MissingIdentityCause cause) => cause switch
    {
        MissingIdentityCause.CvrInAlternativeNumber => "CVR står i alternativ-kundenummeret",
        MissingIdentityCause.CprInAlternativeNumber => "CPR står i alternativ-kundenummeret",
        MissingIdentityCause.MalformedCvr => "CVR er udfyldt, men ikke med 8 cifre",
        MissingIdentityCause.MalformedCpr => "CPR er udfyldt, men ikke med 10 tegn",
        MissingIdentityCause.UninterpretedAlternativeCode => "Alternativ-kundenummer med en kode vi ikke tolker",
        _ => "Ingen identifikation nogen steder",
    };
}
