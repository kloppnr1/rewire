using DataMigration.Domain.Customers;

namespace DataMigration.Migration.Services;

/// <summary>
/// Maps each customer to its EXPECTED Settl Settlement Group(s), purely from local data: the
/// customer's contract billing-plan codes resolved through the profile's BillingPlanNum→GroupName
/// map (<c>CreateOptions.SettlementGroupMap</c>). No Settl calls — usable in (offline) dry-run so
/// the report can show the expected group(s) before any live assignment happens.
///
/// <para>Keys per customer (AccountNumber) because Settl Settlement Groups are customer-scoped: the
/// actual assignment in <c>SettlClient.MigrateAsync</c> adds the <b>customer</b> — not the MP — to
/// each group (same granularity as <c>FixSettlementGroupsRunner</c>). Group names are de-duplicated
/// in first-resolved order across the customer's contracts.</para>
/// </summary>
public static class SettlementGroupExpectationResolver
{
    public static Dictionary<string, List<string>> ResolveByCustomer(
        IEnumerable<Customer> customers,
        IReadOnlyDictionary<string, string> settlementGroupMap)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (settlementGroupMap.Count == 0) return result;

        foreach (var customer in customers)
        {
            var groups = new List<string>();
            foreach (var contract in customer.Contracts)
            {
                var planCode = contract.Billing?.PlanCode;
                if (string.IsNullOrEmpty(planCode)) continue;
                if (!settlementGroupMap.TryGetValue(planCode, out var groupName)
                    || string.IsNullOrEmpty(groupName))
                    continue;

                if (!groups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
                    groups.Add(groupName);
            }

            if (groups.Count > 0)
                result[customer.AccountNumber] = groups;
        }

        return result;
    }
}
