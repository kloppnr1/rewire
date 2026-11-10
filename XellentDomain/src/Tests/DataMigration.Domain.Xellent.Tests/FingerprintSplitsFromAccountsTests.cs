using DataMigration.Domain.Xellent;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// The split rule asked over accounts carried in from elsewhere — a run that migrated in batches
/// and kept each batch's fingerprints so the question could be asked once.
/// </summary>
public class FingerprintSplitsFromAccountsTests
{
    private static AccountFingerprint Fp(string email) => new(
        Fingerprint: $"cepheusejendommeas||jernbanegade26|8900|randersc|{email}",
        Name: "Cepheus Ejendomme A/S",
        AdditionalName: null,
        Street: "Fiktivvejhw 26",
        PostalCode: "8900",
        City: "Randers C",
        Email: email);

    [Fact]
    public void Accounts_that_agree_on_everything_are_not_a_split()
    {
        var splits = XellentLoadResultExtensions.FingerprintSplits([
            ("100277", "17423274", Fp("ubf@dq.example.dk")),
            ("100355", "17423274", Fp("ubf@dq.example.dk")),
            ("100397", "17423274", Fp("ubf@dq.example.dk")),
        ]);

        Assert.Empty(splits);
    }

    /// <summary>
    /// The point of asking once: accounts a batched run would have met in three separate batches
    /// land under the same CVR, with the agreeing ones gathered into one variation.
    /// </summary>
    [Fact]
    public void Accounts_from_different_batches_gather_under_one_cvr()
    {
        var splits = XellentLoadResultExtensions.FingerprintSplits([
            ("100277", "17423274", Fp("ubf@dq.example.dk")),
            ("100355", "17423274", Fp("ubd@dq.example.dk")),
            ("100397", "17423274", Fp("ubf@dq.example.dk")),
        ]);

        var split = Assert.Single(splits);
        Assert.Equal("17423274", split.Cvr);
        Assert.Equal(2, split.VariationCount);
        Assert.Equal(2, split.Variations.Count);
        Assert.Equal(
            ["100277", "100397"],
            split.Variations.Single(v => v.Email == "ubf@dq.example.dk").Accountnums);
        Assert.Equal(
            ["100355"],
            split.Variations.Single(v => v.Email == "ubd@dq.example.dk").Accountnums);
    }

    /// <summary>A CVR with a single account cannot disagree with itself.</summary>
    [Fact]
    public void A_lone_account_is_not_a_split()
    {
        var splits = XellentLoadResultExtensions.FingerprintSplits([
            ("100277", "17423274", Fp("ubf@dq.example.dk")),
        ]);

        Assert.Empty(splits);
    }

    [Fact]
    public void Different_cvrs_are_judged_separately()
    {
        var splits = XellentLoadResultExtensions.FingerprintSplits([
            ("100277", "17423274", Fp("ubf@dq.example.dk")),
            ("100355", "17423274", Fp("ubd@dq.example.dk")),
            ("200100", "82453628", Fp("ubg@dr.example.dk")),
            ("200200", "82453628", Fp("ubg@dr.example.dk")),
        ]);

        Assert.Equal(["17423274"], splits.Select(s => s.Cvr));
    }

    /// <summary>The same account can arrive twice — from a retried batch — and counts once.</summary>
    [Fact]
    public void A_repeated_account_counts_once()
    {
        var splits = XellentLoadResultExtensions.FingerprintSplits([
            ("100277", "17423274", Fp("ubf@dq.example.dk")),
            ("100277", "17423274", Fp("ubf@dq.example.dk")),
            ("100355", "17423274", Fp("ubd@dq.example.dk")),
        ]);

        var split = Assert.Single(splits);
        Assert.Equal(["100277"], split.Variations.Single(v => v.Email == "ubf@dq.example.dk").Accountnums);
    }
}
