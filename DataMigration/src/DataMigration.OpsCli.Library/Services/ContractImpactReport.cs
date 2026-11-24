using System.Text;

namespace DataMigration.OpsCli.Services;

internal sealed record ContractImpactSummary(int XellentCandidates, int NamedSettlLinks,
    int InBoth, string Csv);

internal static class ContractImpactReport
{
    public static ContractImpactSummary Build(ContractSourceSnapshot snapshot, string product)
    {
        var source = snapshot.LiveParts.Concat(snapshot.LoggedParts)
            .Where(p => string.Equals(p.ProductNumber, product, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.MeteringPoint).ToHashSet(StringComparer.Ordinal);
        var named = snapshot.SettlLinks
            .Where(l => string.Equals(l.ContractName, product, StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.MeteringPoint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var all = snapshot.SettlLinks.GroupBy(l => l.MeteringPoint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var linkedChargers = snapshot.ChargerAssignments
            .Where(a => a.MeteringPoint is not null)
            .GroupBy(a => a.MeteringPoint!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Charger).ToArray(), StringComparer.Ordinal);
        var accountsByMp = snapshot.LiveParts.Concat(snapshot.LoggedParts)
            .Where(p => string.Equals(p.ProductNumber, product, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.MeteringPoint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Account).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var accountOnly = snapshot.ChargerAssignments
            .Where(a => a.MeteringPoint is null)
            .Select(a => a.Charger).ToArray();
        var csv = new StringBuilder("MeteringPoint;XellentProduct;NamedSettlLinkCount;NamedSettlLinkPeriods;OtherSettlContracts;ChargerEvidenceCaptured;LinkedChargerSubscriptions;AccountOnlyChargerSubscriptions\r\n");
        foreach (var mp in source.Union(named.Keys).Order(StringComparer.Ordinal))
        {
            named.TryGetValue(mp, out var links);
            all.TryGetValue(mp, out var allLinks);
            linkedChargers.TryGetValue(mp, out var chargers);
            accountsByMp.TryGetValue(mp, out var accounts);
            csv.Append(Escape(mp)).Append(';').Append(source.Contains(mp) ? "Yes" : "No")
                .Append(';').Append(links?.Length ?? 0).Append(';')
                .Append(Escape(string.Join('|', (links ?? []).Select(l => $"{l.Start:O}..{l.End:O}"))))
                .Append(';').Append(Escape(string.Join('|', (allLinks ?? [])
                    .Where(l => !string.Equals(l.ContractName, product, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.ContractName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))
                .Append(';').Append(snapshot.HasChargerAssignments ? "Yes" : "No")
                .Append(';').Append(Escape(string.Join('|', (chargers ?? [])
                    .SelectMany(c => c.Parts.Where(p => p.DeliveryCategory == "Charge_Abo")
                        .Select(p => $"{c.InstallationNumber}:{p.ProductCode}"))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))
                .Append(';').Append(Escape(string.Join('|', accountOnly
                    .Where(c => accounts?.Contains(c.CustomerAccount) == true)
                    .SelectMany(c => c.Parts.Where(p => p.DeliveryCategory == "Charge_Abo")
                        .Select(p => $"{c.InstallationNumber}:{p.ProductCode}"))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))
                .Append("\r\n");
        }
        return new ContractImpactSummary(source.Count, named.Count, named.Keys.Count(source.Contains), csv.ToString());
    }

    private static string Escape(string value) => value.IndexOfAny([';', '"', '\r', '\n']) < 0
        ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
