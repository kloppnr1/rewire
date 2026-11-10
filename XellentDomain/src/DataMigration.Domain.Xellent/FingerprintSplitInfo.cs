namespace DataMigration.Domain.Xellent;

/// <summary>
/// Reporting view: within one CVR, two or more representatives have diverging
/// fingerprints — i.e. they would have merged under representant-princippet but
/// got split because Name/AdditionalName/Street/Zip/City/Email differ. Diagnostic
/// input for the create-report's "Fingerprint splits" section.
/// </summary>
public sealed record FingerprintSplitInfo(
    string Cvr,
    int VariationCount,
    IReadOnlyList<FingerprintVariation> Variations);

/// <summary>
/// One variant within a <see cref="FingerprintSplitInfo"/>. <see cref="Accountnums"/>
/// are the Xellent accounts that hash to this fingerprint inside the CVR.
///
/// <para>Felterne skal dække fingerprintet fuldstændigt. Mangler ét, kan to variationer
/// stå med præcis samme tekst i rapporten selvom de blev til hver sin Settl-kunde, og
/// så er sektionen ulæselig netop hvor den skulle forklare splittet. Det var tilfældet
/// for Email fra 2026-06-04, hvor den kom tilbage i merge-nøglen men ikke her.</para>
/// </summary>
public sealed record FingerprintVariation(
    string Fingerprint,
    string? Name,
    string? AdditionalName,
    string? Street,
    string? PostalCode,
    string? City,
    string? Email,
    IReadOnlyList<string> Accountnums);
