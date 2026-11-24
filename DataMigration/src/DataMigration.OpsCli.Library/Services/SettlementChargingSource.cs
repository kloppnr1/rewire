namespace DataMigration.OpsCli.Services;

/// <summary>Loads dated ownership independently of current Settl contracts.</summary>
public static class SettlementChargingSource
{
    public sealed record Result(IReadOnlyList<SettlementConfigEntry> Entries,
        IReadOnlyDictionary<string, string> Answers, IReadOnlySet<string> FailedCustomers);

    public static async Task<Result> ResolveAsync(IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, CustomerIdentityKind> identities,
        Func<IReadOnlyCollection<(string Account, DateOnly Date)>, CancellationToken,
            Task<IReadOnlyDictionary<(string Account, DateOnly Date), IReadOnlyList<string>>>> read,
        CancellationToken ct = default, bool requireCompleteRead = false)
    {
        var requestsByCustomer = entries.Where(e => e.CustomerInXellent
                && identities.GetValueOrDefault(e.CustomerNumber) != CustomerIdentityKind.Cvr
                && e.XellentAccount is not null && e.ReferenceDate is not null)
            .GroupBy(e => e.CustomerNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => (Account: e.XellentAccount!, Date: e.ReferenceDate!.Value)).Distinct().ToList(), StringComparer.Ordinal);
        var charging = entries.Where(e => string.Equals(SettlementConfigKeys.NormalizeTemplate(e.ContractTemplate ?? ""),
                SettlementConfigKeys.NormalizeTemplate("V_LadePriv"),StringComparison.OrdinalIgnoreCase))
            .Select(e => e.CustomerNumber).ToHashSet(StringComparer.Ordinal);
        var answers = new Dictionary<string,string>(StringComparer.Ordinal);
        var failures = new HashSet<string>(StringComparer.Ordinal);
        if (requestsByCustomer.Count == 0) return new(entries.ToList(), answers, failures);
        try
        {
            var products = await read(requestsByCustomer.Values.SelectMany(v => v).Distinct().ToList(),ct);
            foreach (var (customer, requests) in requestsByCustomer)
            {
                if (requireCompleteRead && requests.Any(r => !products.ContainsKey(r)))
                { failures.Add(customer); continue; }
                var answer = XellentChargeSubscriptionReader.SegmentFor(requests.SelectMany(r => products.GetValueOrDefault(r) ?? []));
                if (answer != "Privat" || charging.Contains(customer)) answers[customer] = answer;
            }
            var updated = entries.Select(e => requireCompleteRead && requestsByCustomer.ContainsKey(e.CustomerNumber)
                && e.XellentAccount is not null && e.ReferenceDate is not null
                && !products.ContainsKey((e.XellentAccount,e.ReferenceDate.Value))
                ? e with { ChargeReadStatus = "Ambiguous",ChargeProducts = null }
                : requestsByCustomer.ContainsKey(e.CustomerNumber)
                && e.XellentAccount is not null && e.ReferenceDate is not null
                ? e with { ChargeReadStatus = "Read", ChargeProducts = products.GetValueOrDefault((e.XellentAccount,e.ReferenceDate.Value)) ?? [] }
                : e).ToList();
            return new(updated,answers,failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.UnionWith(requestsByCustomer.Keys);
            return new(entries.Select(e => failures.Contains(e.CustomerNumber) ? e with { ChargeReadStatus = "ReadFailed" } : e).ToList(),answers,failures);
        }
    }
}
