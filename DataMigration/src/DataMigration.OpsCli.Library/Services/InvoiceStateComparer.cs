using System.Text;
using DataMigration.OpsCli.Data;

namespace DataMigration.OpsCli.Services;

public sealed record InvoiceStateDifference(
    string Kind,
    string Change,
    string SettlCustomer,
    string SourceAccount,
    string InvoiceId,
    string Before,
    string After);

public static class InvoiceStateComparer
{
    public static IReadOnlyList<InvoiceStateDifference> Compare(
        MigrationStateDocument before,
        MigrationStateDocument after)
    {
        var differences = new List<InvoiceStateDifference>();
        var beforeAccounts = AccountMap(before);
        var afterAccounts = AccountMap(after);

        foreach (var customer in beforeAccounts.Keys.Union(afterAccounts.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            var hasBefore = beforeAccounts.TryGetValue(customer, out var oldAccount);
            var hasAfter = afterAccounts.TryGetValue(customer, out var newAccount);
            if (!hasBefore || !hasAfter)
            {
                differences.Add(new InvoiceStateDifference("Customer", hasAfter ? "Added" : "Removed",
                    customer, "", "", hasBefore ? "present" : "", hasAfter ? "present" : ""));
                continue;
            }

            if (oldAccount!.ChangeOfSupplierDate != newAccount!.ChangeOfSupplierDate)
                differences.Add(new InvoiceStateDifference("Customer", "CosChanged", customer, "", "",
                    oldAccount.ChangeOfSupplierDate.ToString("yyyy-MM-dd"),
                    newAccount.ChangeOfSupplierDate.ToString("yyyy-MM-dd")));
        }

        CompareOwners("SourceAccount", SourceOwners(before), SourceOwners(after), differences);
        CompareOwners("Invoice", InvoiceOwners(before), InvoiceOwners(after), differences);

        return differences
            .OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.SourceAccount, StringComparer.Ordinal)
            .ThenBy(d => d.InvoiceId, StringComparer.Ordinal)
            .ThenBy(d => d.SettlCustomer, StringComparer.Ordinal)
            .ThenBy(d => d.Change, StringComparer.Ordinal)
            .ToList();
    }

    public static string ToCsv(IReadOnlyList<InvoiceStateDifference> differences)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Kind;Change;SettlCustomer;SourceAccount;InvoiceId;Before;After");
        foreach (var row in differences)
        {
            csv.Append(Csv(row.Kind)).Append(';')
                .Append(Csv(row.Change)).Append(';')
                .Append(Csv(row.SettlCustomer)).Append(';')
                .Append(Csv(row.SourceAccount)).Append(';')
                .Append(Csv(row.InvoiceId)).Append(';')
                .Append(Csv(row.Before)).Append(';')
                .AppendLine(Csv(row.After));
        }
        return csv.ToString();
    }

    private static Dictionary<string, MigratedAccount> AccountMap(MigrationStateDocument state)
    {
        var duplicates = state.Accounts.GroupBy(a => a.SettlAccountNumber, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).Take(10).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidDataException($"Duplicate Settl customer numbers in state: {string.Join(", ", duplicates)}");
        return state.Accounts.ToDictionary(a => a.SettlAccountNumber, StringComparer.Ordinal);
    }

    private static Dictionary<(string Source, string InvoiceId), string[]> SourceOwners(MigrationStateDocument state) =>
        state.Accounts.SelectMany(account => account.SourceAccounts.Select(source =>
                (Key: (Source: source, InvoiceId: ""), Owner: account.SettlAccountNumber)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Owner)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    private static Dictionary<(string Source, string InvoiceId), string[]> InvoiceOwners(MigrationStateDocument state) =>
        state.Accounts.SelectMany(account => account.Invoices.Select(invoice =>
                (Key: (Source: invoice.SourceAccount, invoice.InvoiceId), Owner: account.SettlAccountNumber)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Owner)
                .Order(StringComparer.Ordinal).ToArray());

    private static void CompareOwners(
        string kind,
        IReadOnlyDictionary<(string Source, string InvoiceId), string[]> before,
        IReadOnlyDictionary<(string Source, string InvoiceId), string[]> after,
        List<InvoiceStateDifference> differences)
    {
        foreach (var key in before.Keys.Union(after.Keys)
            .OrderBy(k => k.Source, StringComparer.Ordinal)
            .ThenBy(k => k.InvoiceId, StringComparer.Ordinal))
        {
            var oldOwners = before.GetValueOrDefault(key) ?? [];
            var newOwners = after.GetValueOrDefault(key) ?? [];
            var oldText = string.Join('|', oldOwners);
            var newText = string.Join('|', newOwners);
            if (oldText == newText && oldOwners.Length <= 1) continue;

            var change = oldOwners.Length > 1 || newOwners.Length > 1
                ? "AmbiguousOwnership"
                : oldOwners.Length == 0 ? "Added"
                : newOwners.Length == 0 ? "Removed"
                : "Moved";
            differences.Add(new InvoiceStateDifference(kind, change, "", key.Source, key.InvoiceId,
                oldText, newText));
        }
    }

    private static string Csv(string value) => value.IndexOfAny([';', '"', '\n', '\r']) < 0
        ? value
        : '"' + value.Replace("\"", "\"\"") + '"';
}
