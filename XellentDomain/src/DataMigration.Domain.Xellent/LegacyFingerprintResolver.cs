using System.Globalization;
using System.Text;
using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Diagnostic-only: re-computes the pre-2026-05-20 fingerprint formula
/// (Name|AdditionalName|Street|Zip|City|Email|EAN — 7-field) so report-payer-diff in
/// DataMigration.OpsCli can show which accounts lose their own Settl-customer status
/// under the new (slimmer) 5-field algorithm in <see cref="FingerprintCalculator"/>.
///
/// <para>Mirrors the SQL/EF logic that lived in <see cref="AccountRepresentativeResolver"/>
/// before Email/EAN were dropped. Representative-selection uses Ordinal (lexicographic)
/// sort — replicates the historical bug so the diff shows accounts as they currently
/// exist in Settl.</para>
/// </summary>
public static class LegacyFingerprintResolver
{
    private static readonly HashSet<char> StripChars = new()
    {
        ' ', '\t', '\n', '\r', ' ',
        '/', '.', ',', '-', '&', '\'', '"', '(', ')'
    };

    public static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        XellentDbContext context,
        string[]? accounts,
        string dataAreaId,
        DateOnly referenceDate,
        CancellationToken cancellationToken)
    {
        var validTo = referenceDate == default ? DateTime.Today : referenceDate.ToDateTime(TimeOnly.MinValue);

        var custRows = await (
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
            where cust.Dataareaid == dataAreaId && (accounts == null || accounts.Contains(cust.Accountnum))
            select new
            {
                cust.Accountnum,
                cust.Name,
                Cvr = cust.ExuCvrDisponent1,
                cust.ExuAdditionalname,
                Street = addr != null ? addr.Street : null,
                Zipcode = addr != null ? addr.Zipcode : null,
                City = addr != null ? addr.City : null,
                cust.Email
            }
        ).ToListAsync(cancellationToken);

        var eanRows = await (
            from contract in context.ExuContractTables
            where contract.Dataareaid == dataAreaId
                && (accounts == null || accounts.Contains(contract.Custaccount))
                && (contract.Referencetomaincontract == null || contract.Referencetomaincontract == "")
                && (contract.Contractenddate == MigrationConstants.EndDateNull
                    || contract.Contractenddate >= validTo)
            select new { Accountnum = contract.Custaccount, contract.Einvoiceeannum, contract.Contractnum }
        ).ToListAsync(cancellationToken);

        var eanByAccount = eanRows
            .GroupBy(r => r.Accountnum, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Contractnum, StringComparer.Ordinal).First().Einvoiceeannum,
                StringComparer.Ordinal);

        var rowByAccount = custRows
            .GroupBy(r => r.Accountnum, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        string KeyOf(string acc)
        {
            if (!rowByAccount.TryGetValue(acc, out var row) || string.IsNullOrEmpty(row.Cvr))
                return acc;
            eanByAccount.TryGetValue(acc, out var ean);
            var fp = ComputeLegacy(
                row.Name?.Trim(),
                row.ExuAdditionalname?.Trim(),
                row.Street?.Trim(),
                row.Zipcode?.Trim(),
                row.City?.Trim(),
                row.Email?.Trim(),
                ean?.Trim());
            return row.Cvr + "|" + fp;
        }

        var representativeByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = rowByAccount.Keys.GroupBy(KeyOf);
        foreach (var group in groups)
        {
            // Ordinal (lexicographic) ordering replicates the historical bug: that's what
            // produced the currently-migrated Settl state, so the diff makes sense.
            var rep = group.OrderBy(x => x, StringComparer.Ordinal).First();
            foreach (var member in group)
                representativeByAccount[member] = rep;
        }
        return representativeByAccount;
    }

    private static string ComputeLegacy(
        string? name, string? additionalName, string? street,
        string? zipcode, string? city, string? email, string? ean)
    {
        var raw = string.Join('|',
            name ?? "", additionalName ?? "", street ?? "",
            zipcode ?? "", city ?? "", email ?? "", ean ?? "");
        var folded = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (StripChars.Contains(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
