using DataMigration.Domain.Xellent;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// Fingerprint-split-sektionen skal kunne forklare hvorfor to konti under samme CVR blev til
/// hver sin Settl-kunde. Den forklaring holder kun hvis variationerne bærer alle felter
/// fingerprintet er regnet på — mangler ét, står to rækker med præcis samme tekst og et
/// menneske kan ikke se forskellen. Det skete for Email, som kom tilbage i merge-nøglen
/// 2026-06-04 uden at komme tilbage her.
/// </summary>
public class FingerprintSplitsByCvrTests
{
    private static XellentLoadResult Result(
        params (string Account, string Cvr, AccountFingerprint Fingerprint)[] accounts) =>
        new(
            Customers: [],
            MeteringPoints: [],
            OwnerClusters: [],
            JointInvoiceGroups: [],
            SnapshotDate: new DateOnly(2026, 8, 11),
            MpOrderIdentifiers: new Dictionary<string, string?>(),
            AccountGroupings: [.. accounts.Select(a => new AccountGrouping(
                RepresentativeAccount: a.Account,
                MemberAccounts: [a.Account],
                GroupNum: null,
                GroupName: null,
                Cvr: a.Cvr,
                AccountNames: new Dictionary<string, string> { [a.Account] = a.Fingerprint.Name ?? "" }))],
            FingerprintByRepresentative: accounts.ToDictionary(a => a.Account, a => a.Fingerprint));

    private static AccountFingerprint Fingerprint(string? email) => new(
        Fingerprint: $"cepheus ejendommeas|jernbanegade26|8900|randersc|{email}",
        Name: "Cepheus Ejendomme A/S",
        AdditionalName: null,
        Street: "Fiktivvejhw 26",
        PostalCode: "8900",
        City: "Randers C",
        Email: email);

    /// <summary>
    /// Det egentlige fund: to konti der kun er uenige om email. Uden email på variationen er
    /// de to rækker tegn for tegn ens, og rapporten påstår et split den ikke kan begrunde.
    /// </summary>
    [Fact]
    public void Variations_that_differ_only_by_email_are_told_apart()
    {
        var splits = Result(
            ("100277", "17423274", Fingerprint("ubd@dq.example.dk")),
            ("100355", "17423274", Fingerprint("ube@dq.example.dk"))).FingerprintSplitsByCvr();

        var split = Assert.Single(splits);
        Assert.Equal("17423274", split.Cvr);
        Assert.Equal(
            ["ubd@dq.example.dk", "ube@dq.example.dk"],
            split.Variations.Select(v => v.Email).OrderBy(e => e, StringComparer.Ordinal));
    }

    /// <summary>
    /// Ingen tom celle hvor der står en værdi i Xellent: hvert felt fingerprintet bruger skal
    /// nå hele vejen ud på variationen.
    /// </summary>
    [Fact]
    public void Variation_carries_every_field_the_fingerprint_is_computed_from()
    {
        var splits = Result(
            ("100277", "17423274", Fingerprint("ubd@dq.example.dk")),
            ("100355", "17423274", Fingerprint("ube@dq.example.dk"))).FingerprintSplitsByCvr();

        var variation = splits.Single().Variations.Single(v => v.Accountnums.Contains("100277"));
        Assert.Equal("Cepheus Ejendomme A/S", variation.Name);
        Assert.Equal("Fiktivvejhw 26", variation.Street);
        Assert.Equal("8900", variation.PostalCode);
        Assert.Equal("Randers C", variation.City);
        Assert.Equal("ubd@dq.example.dk", variation.Email);
        Assert.Contains("ubd@dq.example.dk", variation.Fingerprint);
    }

    /// <summary>
    /// Et CVR hvor alle er enige er ikke et split — det merger som det skal, og en række om det
    /// ville kun være støj i en sektion der handler om undtagelserne.
    /// </summary>
    [Fact]
    public void Cvr_with_a_single_representative_is_not_reported()
    {
        var splits = Result(("100277", "17423274", Fingerprint("ubd@dq.example.dk")))
            .FingerprintSplitsByCvr();

        Assert.Empty(splits);
    }
}
