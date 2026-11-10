namespace DataMigration.Domain.Xellent;

/// <summary>
/// Projection helpers over <see cref="XellentLoadResult"/> for reporting/diagnostics.
/// </summary>
public static class XellentLoadResultExtensions
{
    /// <summary>
    /// Computes CVR-grouped fingerprint splits: for each CVR whose accounts do not all agree,
    /// emit one <see cref="FingerprintSplitInfo"/> describing every fingerprint variation and the
    /// accounts under it. CVRs where everyone shares the same fingerprint are excluded (they merge
    /// correctly; nothing to flag).
    /// </summary>
    public static IReadOnlyList<FingerprintSplitInfo> FingerprintSplitsByCvr(this XellentLoadResult loadResult)
    {
        var fpByRep = loadResult.FingerprintByRepresentative;
        return FingerprintSplits(loadResult.AccountGroupings
            .Where(g => !string.IsNullOrEmpty(g.Cvr) && fpByRep.ContainsKey(g.RepresentativeAccount))
            .SelectMany(g => g.MemberAccounts.Select(a =>
                (Account: a, Cvr: g.Cvr!, Fingerprint: fpByRep[g.RepresentativeAccount]))));
    }

    /// <summary>
    /// The same rule over accounts carried in from somewhere else — a run that migrated in batches
    /// and kept each batch's fingerprints so the question could be asked once, over the whole
    /// scope. A batch cannot answer it: a CVR whose accounts fall in four batches looks like four
    /// unrelated CVRs, and one whose accounts fall one per batch looks like nothing at all.
    ///
    /// <para>The unit is the fingerprint, not the account: accounts that agree on name, address
    /// and email are one variation, and a CVR with only one variation is not a split. Under
    /// representative merge those two statements are the same thing — agreeing accounts have
    /// already become one representative — but a run that migrates without merging keeps them
    /// apart, and then counting representatives would report every large CVR as split.</para>
    /// </summary>
    public static IReadOnlyList<FingerprintSplitInfo> FingerprintSplits(
        IEnumerable<(string Account, string Cvr, AccountFingerprint Fingerprint)> accounts)
    {
        return accounts
            .GroupBy(a => a.Cvr, StringComparer.Ordinal)
            .Select(cvrGroup => new
            {
                Cvr = cvrGroup.Key,
                Variations = cvrGroup
                    .GroupBy(a => a.Fingerprint.Fingerprint, StringComparer.Ordinal)
                    .Select(fpGroup =>
                    {
                        var fp = fpGroup.First().Fingerprint;
                        return new FingerprintVariation(
                            Fingerprint: fp.Fingerprint,
                            Name: fp.Name,
                            AdditionalName: fp.AdditionalName,
                            Street: fp.Street,
                            PostalCode: fp.PostalCode,
                            City: fp.City,
                            Email: fp.Email,
                            Accountnums: [.. fpGroup.Select(a => a.Account)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(a => a, StringComparer.Ordinal)]);
                    })
                    .ToList()
            })
            .Where(x => x.Variations.Count >= 2)
            .Select(x => new FingerprintSplitInfo(x.Cvr, x.Variations.Count, x.Variations))
            .ToList();
    }
}
