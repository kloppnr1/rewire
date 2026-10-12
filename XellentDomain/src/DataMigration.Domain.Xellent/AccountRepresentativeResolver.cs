using DataMigration.Core;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent;

/// <param name="FingerprintByAccount">
/// Per-account merge fingerprint. Empty when the merge is disabled (and on the legacy TG4 path):
/// nothing merges, so no fingerprint is computed and the address it needs is never fetched.
/// </param>
public record AccountRepresentativeResolution(
    List<string> Accounts,
    int MergedCount,
    IReadOnlyDictionary<string, string> NameByAccount,
    IReadOnlyDictionary<string, string> RepresentativeByAccount,
    IReadOnlyDictionary<string, AccountFingerprint> FingerprintByAccount);

/// <summary>
/// Translates Xellent Accountnums to their Settl customer representative.
/// Customers sharing <c>(Cvr, Fingerprint)</c> merge into one Settl customer
/// whose CustomerNumber is the lowest Accountnum in the group — mirrors
/// <c>SettlExtractionService</c>'s GetEffectiveGroupKey + BuildMergeKeyMapAsync.
///
/// <para>Fix-* runners MUST call this before looking up customers in Settl;
/// otherwise non-representative group members silently report as "not found".</para>
/// </summary>
public static class AccountRepresentativeResolver
{
    /// <param name="accounts">
    /// Accounts to resolve, or <c>null</c> for every account in the data area. A whole-commodity
    /// run has no meaningful list to pass: enumerating tens of thousands of ids only to send them
    /// back to the same database exceeds SQL Server's parameter budget and the query planner
    /// gives up. Null keeps the filter out of the SQL entirely — the same idiom
    /// <see cref="XellentAccountSearch"/> uses for its optional pre-filter.
    /// </param>
    /// <param name="disableMerge">
    /// Skips the CVR+fingerprint merge entirely: every account resolves to itself, so each Xellent
    /// Accountnum becomes its own Settl customer. No fingerprint is computed in that state —
    /// nothing consumes it once it forms no edges — so <c>FingerprintByAccount</c> comes back empty
    /// and the primary-address lookup behind it is left out of the query.
    ///
    /// <para>Deliberate opt-in for whole-category runs, where the account set is migrated in
    /// independent batches. A merge group split across two batches produces two Settl customers
    /// anyway, so a per-batch merge is worse than none: it makes the result depend on the batch
    /// layout. Profile-based runs keep merging and must not set this.</para>
    /// </param>
    public static async Task<AccountRepresentativeResolution> ResolveAsync(
        XellentDbContext context,
        string[]? accounts,
        string dataAreaId,
        bool legacyTg4Merge = false,
        DateOnly referenceDate = default,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? ignoredGroupNumbers = null,
        bool disableMerge = false)
    {
        // TG4/kundegruppe sammenlignes case-insensitivt. Genopbyg det indkomne sæt med
        // OrdinalIgnoreCase, så ignore-matchet ikke afhænger af kalderens comparer.
        var ignored = ignoredGroupNumbers is { Count: > 0 }
            ? new HashSet<string>(ignoredGroupNumbers, StringComparer.OrdinalIgnoreCase)
            : null;

        // disableMerge wins over the legacy rule: "no merge" is not a merge variant.
        if (legacyTg4Merge && !disableMerge)
            return await ResolveLegacyAsync(context, accounts, dataAreaId, ignored, cancellationToken);

        // Address-relationships have multiple Isprimary=1 rows over a customer's lifetime.
        // Filter by Validtodatetime so the fingerprint uses the address valid on referenceDate —
        // matches old SettlExtractionService.BuildMergeKeyMapAsync. referenceDate=default → today.
        var validTo = referenceDate == default ? DateTime.Today : referenceDate.ToDateTime(TimeOnly.MinValue);

        // Without a merge the address is dead weight: it exists only to build the fingerprint, and
        // the fingerprint only exists to form edges. Name, CVR, TG4, additional name and email all
        // sit on CustTable itself and are still needed, so the query keeps those and drops the
        // three left-joins — over a whole data area that is ~45.000 accounts' worth of join work.
        // Read in ChunkSize portions. The account filter becomes an inlined IN list, and a whole
        // data area (54.918 accounts on the 2026-09-01 population) makes that one statement too
        // large for SQL Server to compile at all — error 8632, no join required. Unioning the
        // portions client-side cannot change the answer: every row here is independent, and all
        // grouping, fingerprinting and edge-building happens below, after the loop. A null filter
        // stays null — one query, no IN list, "every account in the data area" as before.
        var custRows = new List<CustRow>();
        foreach (var chunk in AccountFilterChunks.Split(accounts))
        {
            var chunkRows = disableMerge
            ? await (
                from cust in context.CustTables
                where cust.Dataareaid == dataAreaId
                    && (chunk == null || chunk.Contains(cust.Accountnum))
                select new CustRow(
                    cust.Accountnum,
                    cust.Name,
                    cust.ExuCvrDisponent1,
                    cust.ExuTechnicgroup4,
                    cust.ExuAdditionalname,
                    cust.Email,
                    null,
                    null,
                    null)
            ).ToListAsync(cancellationToken)
            : await (
            from cust in context.CustTables
            join partyAddr in context.DirPartyAddressRelationships
                on new { cust.Partyid, cust.Dataareaid }
                equals new { partyAddr.Partyid, partyAddr.Dataareaid } into partyJoin
            from partyAddr in partyJoin.Where(p => p.Isprimary == 1 && p.Validtodatetime >= validTo).DefaultIfEmpty()
            join addrLink in context.DirPartyAddressRelationshi1066s
                on new { Recid = partyAddr != null ? partyAddr.Recid : 0L, partyAddr!.Dataareaid }
                equals new { Recid = addrLink.Partyaddressrelationshiprecid, addrLink.Dataareaid } into linkJoin
            from addrLink in linkJoin.DefaultIfEmpty()
            join addr in context.Addresses
                on new { Recid = addrLink != null ? addrLink.Addressrecid : 0L, addrLink!.Dataareaid }
                equals new { addr.Recid, addr.Dataareaid } into addrJoin
            from addr in addrJoin.Where(a => a.Addrtableid == MigrationConstants.AddressTableId).DefaultIfEmpty()
            // Accountnum genbruges på tværs af dataområder: ~28.000 hol-konti findes også i HJH,
            // hil, Han m.fl. Uden dataområde-filteret vandt den fremmede række vilkårligt, og
            // navn/CVR/kundegruppe/email kom fra en anden virksomheds kunde — tomt CVR betyder
            // desuden at kontoen aldrig merger. Legacy-vejen har altid filtreret.
            where cust.Dataareaid == dataAreaId
                && (chunk == null || chunk.Contains(cust.Accountnum))
            select new CustRow(
                cust.Accountnum,
                cust.Name,
                cust.ExuCvrDisponent1,
                cust.ExuTechnicgroup4,
                cust.ExuAdditionalname,
                cust.Email,
                addr != null ? addr.Street : null,
                addr != null ? addr.Zipcode : null,
                addr != null ? addr.City : null)
            ).ToListAsync(cancellationToken);

            custRows.AddRange(chunkRows);
        }

        var rowByAccount = custRows
            .GroupBy(r => r.Accountnum, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var fingerprintByAccount = new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal);

        // Legal-contact DAR address id per account — the merge-key address component (see KeyOf).
        // Only fetched when it can affect the outcome: with disableMerge there are no edges to
        // build, and this is a full extra query over the delpoint contact table.
        var legalAddressByAccount = disableMerge
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await FetchLegalContactAddressIdsAsync(context, accounts, dataAreaId, cancellationToken);

        static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        // Transitive (union-find) merge, scoped to CVR. Two same-CVR accounts merge when they share
        // EITHER the street fingerprint OR the legal-contact DAR address id — not a single composite
        // key. Street alone split same-entity accounts on trivial string noise (extra address lines,
        // double spaces); DAR id alone split them when the same entity carries different DAR ids
        // across its delpoints. Taking the UNION of both edges heals both directions and can only
        // consolidate groups relative to the historical street-only key — it never fragments.
        var edgeBuckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void AddEdge(string key, string acc)
        {
            if (!edgeBuckets.TryGetValue(key, out var list))
                edgeBuckets[key] = list = [];
            list.Add(acc);
        }

        // Skipped wholesale when the merge is off: every statement in here either builds an edge or
        // builds the fingerprint the edge is keyed on, and neither survives into the result.
        if (!disableMerge)
        {
            foreach (var acc in rowByAccount.Keys)
            {
                var row = rowByAccount[acc];
                if (string.IsNullOrEmpty(row.Cvr))
                    continue; // no CVR → stands alone, never merges (handled as its own union-find singleton)

                // Explicitly ignored TechnicGroup4 → stands alone, never merges. Same singleton
                // treatment as a CVR-less account: add no edges so union-find leaves it as its own root.
                if (ignored != null && !string.IsNullOrEmpty(row.Tg4) && ignored.Contains(row.Tg4))
                    continue;

                var name = row.Name?.Trim();
                var additionalName = row.ExuAdditionalname?.Trim();
                var email = row.Email?.Trim();
                var street = row.Street?.Trim();
                var zip = row.Zipcode?.Trim();
                var city = row.City?.Trim();

                // Street edge: same CVR + same street fingerprint (the exact historical merge key).
                // Email participates: same-CVR accounts whose contact email differs do not merge.
                var streetFp = FingerprintCalculator.Compute(name, additionalName, street, zip, city, email);
                AddEdge("S|" + row.Cvr + "|" + streetFp, acc);

                // DAR edge: same CVR + same name + same legal-contact DAR id. Name-scoped so a shared
                // building DAR access address only consolidates accounts that already agree on the legal
                // name, never distinct tenants of the same building. Email is folded into the name key
                // too, so a differing contact email keeps accounts apart on this edge as well.
                if (legalAddressByAccount.TryGetValue(acc, out var darId))
                {
                    var nameKey = FingerprintCalculator.Compute(name, additionalName, null, null, null, email);
                    AddEdge("D|" + row.Cvr + "|" + nameKey + "|" + darId, acc);
                }

                fingerprintByAccount[acc] = new AccountFingerprint(
                    Fingerprint: streetFp,
                    Name: NullIfEmpty(name),
                    AdditionalName: NullIfEmpty(additionalName),
                    Street: NullIfEmpty(street),
                    PostalCode: NullIfEmpty(zip),
                    City: NullIfEmpty(city),
                    Email: NullIfEmpty(email));
            }
        }

        // Union-find: each account starts as its own group; every edge bucket of ≥2 accounts unions
        // its members. Transitive closure merges chains (A~B via street, B~C via DAR ⇒ {A,B,C}).
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var acc in rowByAccount.Keys) parent[acc] = acc;

        string Find(string a)
        {
            while (!string.Equals(parent[a], a, StringComparison.Ordinal))
                a = parent[a] = parent[parent[a]];
            return a;
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (string.Equals(ra, rb, StringComparison.Ordinal)) return;
            // Lower accountnum stays the canonical root (deterministic; matches representative choice).
            if (AccountnumComparer.Instance.Compare(ra, rb) <= 0) parent[rb] = ra;
            else parent[ra] = rb;
        }

        foreach (var members in edgeBuckets.Values)
            for (var i = 1; i < members.Count; i++)
                Union(members[0], members[i]);

        var groups = rowByAccount.Keys
            .GroupBy(Find, StringComparer.Ordinal)
            .Select(g => new
            {
                Members = g.OrderBy(x => x, AccountnumComparer.Instance).ToArray(),
                Representative = g.OrderBy(x => x, AccountnumComparer.Instance).First()
            })
            .ToList();

        var representatives = groups.Select(g => g.Representative).ToHashSet(StringComparer.Ordinal);

        var representativeByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
            foreach (var member in group.Members)
                representativeByAccount[member] = group.Representative;

        var xellentAccounts = rowByAccount.Keys.ToHashSet(StringComparer.Ordinal);
        // Nothing was requested when accounts is null, so nothing can be missing.
        var notInXellent = accounts?.Where(a => !xellentAccounts.Contains(a)).ToList() ?? [];
        var mergedCount = xellentAccounts.Count - representatives.Count;

        var nameByAccount = rowByAccount.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Name ?? "",
            StringComparer.OrdinalIgnoreCase);

        if (disableMerge)
            Console.WriteLine($"  representative merge DISABLED — {representatives.Count} account(s) each migrate as their own Settl customer");
        else if (mergedCount > 0)
            Console.WriteLine($"  {mergedCount} account(s) merged into representatives (CVR + street/DAR transitive)");

        var resolved = representatives
            .Concat(notInXellent)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new AccountRepresentativeResolution(resolved, mergedCount, nameByAccount, representativeByAccount, fingerprintByAccount);
    }

    /// <summary>
    /// One CustTable row as the resolver reads it. Street/Zipcode/City feed the fingerprint only,
    /// and are null on the no-merge path where the address is never fetched.
    /// </summary>
    private sealed record CustRow(
        string Accountnum,
        string? Name,
        string? Cvr,
        string? Tg4,
        string? ExuAdditionalname,
        string? Email,
        string? Street,
        string? Zipcode,
        string? City);

    /// <summary>
    /// Per-account legal-contact DAR address id (EXU_DELPOINTCONTACTINFOTABLE Contacttype=4, on the
    /// account's electricity delpoints). Used as the merge-key address component in place of the
    /// free-text primary-address street.
    ///
    /// <para>Deterministic single value per account: the lowest valid-GUID Addressid (MIN). NO
    /// end-date filter — the lookup is date-independent on purpose, so an account always resolves
    /// to the same merge key regardless of extraction reference date. Reconcile rebuilds the
    /// create-true payload one COS slice at a time and depends on a given account resolving to the
    /// same representative across slices; a date-dependent address key would reintroduce
    /// slice-dependent splits. Non-GUID values (negligible data-entry garbage) are ignored so the
    /// account falls back to the street fingerprint.</para>
    /// </summary>
    private static async Task<Dictionary<string, string>> FetchLegalContactAddressIdsAsync(
        XellentDbContext context, string[]? accounts, string dataAreaId, CancellationToken cancellationToken)
    {
        if (accounts is { Length: 0 }) return new Dictionary<string, string>(StringComparer.Ordinal);

        var deliveryCategory = MigrationConstants.DeliveryCategoryElectricity;

        // Same chunking as the CustTable read above, and for the same reason — this one carries the
        // inlined account list on top of four joins, so it hits the statement limit sooner rather
        // than later. Grouping is per account and happens after the loop, so portions are exact.
        var rows = new List<(string Custaccount, string Addressid)>();
        foreach (var chunk in AccountFilterChunks.Split(accounts))
        {
            var chunkRows = await (
            from contract in context.ExuContractTables
            join part in context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { part.Contractnum, part.Dataareaid }
            join agreement in context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            join delpoint in context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
            join contactInfo in context.ExuDelpointContactInfoTables
                on new { delpoint.Meteringpoint, delpoint.Dataareaid, delpoint.Deliverycategory, delpoint.Companyid }
                equals new { contactInfo.Meteringpoint, contactInfo.Dataareaid, contactInfo.Deliverycategory, contactInfo.Companyid }
            where contract.Dataareaid == dataAreaId
                && (chunk == null || chunk.Contains(contract.Custaccount))
                && contract.Deliverycategory == deliveryCategory
                && delpoint.Deliverycategory == deliveryCategory
                && contactInfo.Contacttype == 4
                && contactInfo.Addressid != null && contactInfo.Addressid != ""
            select new { contract.Custaccount, contactInfo.Addressid }
            ).ToListAsync(cancellationToken);

            rows.AddRange(chunkRows.Select(r => (r.Custaccount, r.Addressid)));
        }

        return rows
            .GroupBy(r => r.Custaccount, StringComparer.Ordinal)
            .Select(g => new
            {
                Account = g.Key,
                DarId = g.Select(x => x.Addressid)
                    // Valid, non-empty GUID only. The all-zeros GUID ("00000000-…") occurs as a
                    // placeholder in the data and parses as a valid Guid, but it sorts first under MIN
                    // and means nothing geographically — accepting it would bridge unrelated accounts.
                    .Where(id => Guid.TryParse(id, out var g) && g != Guid.Empty)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .FirstOrDefault()
            })
            .Where(x => x.DarId != null)
            .ToDictionary(x => x.Account, x => x.DarId!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Legacy fallback for profiles with LegacyTg4Merge=true: groups by (CVR + ExuTechnicgroup4).
    /// </summary>
    private static async Task<AccountRepresentativeResolution> ResolveLegacyAsync(
        XellentDbContext context,
        string[]? accounts,
        string dataAreaId,
        IReadOnlySet<string>? ignoredGroupNumbers,
        CancellationToken cancellationToken)
    {
        // Chunked like the other account reads: same table, same inlined IN list, same statement
        // limit. Grouping happens below, once every portion has been read.
        var custs = new List<(string Accountnum, string? Name, string? GroupNum, string? Cvr)>();
        foreach (var chunk in AccountFilterChunks.Split(accounts))
        {
            var chunkRows = await context.CustTables
                .Where(c => c.Dataareaid == dataAreaId && (chunk == null || chunk.Contains(c.Accountnum)))
                .Select(c => new
                {
                    c.Accountnum,
                    c.Name,
                    GroupNum = c.ExuTechnicgroup4,
                    Cvr = c.ExuCvrDisponent1
                })
                .ToListAsync(cancellationToken);

            custs.AddRange(chunkRows.Select(c => (c.Accountnum, (string?)c.Name, (string?)c.GroupNum, (string?)c.Cvr)));
        }

        // Ignored TG4 → key is the accountnum itself, so the account never shares a group key
        // and stands alone — same "treated as ungrouped" semantics as the fingerprint path.
        // TG4 normaliseres til upper i merge-nøglen, så samme kundegruppe med afvigende
        // casing (Foo vs foo) merger til samme repræsentant. CVR er numerisk → casing irrelevant.
        string KeyOf(string acc, string? groupNum, string? cvr) =>
            !string.IsNullOrEmpty(groupNum) && !string.IsNullOrEmpty(cvr)
                && (ignoredGroupNumbers == null || !ignoredGroupNumbers.Contains(groupNum))
                ? groupNum.ToUpperInvariant() + "|" + cvr
                : acc;

        var legacyGroups = custs
            .GroupBy(c => KeyOf(c.Accountnum, c.GroupNum, c.Cvr))
            .Select(g => new
            {
                Members = g.Select(x => x.Accountnum).OrderBy(x => x, AccountnumComparer.Instance).ToArray(),
                Representative = g.Select(x => x.Accountnum).OrderBy(x => x, AccountnumComparer.Instance).First()
            })
            .ToList();

        var representatives = legacyGroups.Select(g => g.Representative).ToHashSet(StringComparer.Ordinal);

        var representativeByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in legacyGroups)
            foreach (var member in group.Members)
                representativeByAccount[member] = group.Representative;

        var xellentAccounts = custs.Select(c => c.Accountnum).ToHashSet(StringComparer.Ordinal);
        var notInXellent = accounts?.Where(a => !xellentAccounts.Contains(a)).ToList() ?? [];
        var mergedCount = xellentAccounts.Count - representatives.Count;

        var nameByAccount = custs
            .GroupBy(c => c.Accountnum, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name ?? "", StringComparer.OrdinalIgnoreCase);

        if (mergedCount > 0)
            Console.WriteLine($"  {mergedCount} account(s) merged into representatives (CVR + TechnicGroup4 [legacy])");

        var resolved = representatives
            .Concat(notInXellent)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return new AccountRepresentativeResolution(
            resolved, mergedCount, nameByAccount, representativeByAccount,
            new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal));
    }
}
