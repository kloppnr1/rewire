using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// Warns when accounts within the same CVR group share identical name/address but differ only
/// on email — one or more have an email, others have none. Such accounts form separate merge
/// groups (email participates in the fingerprint) and therefore become separate Settl customers,
/// which may not be the operator's intent.
///
/// <para>The merge rule itself is NOT changed. This validator makes the split visible so the
/// operator can decide whether to equalise the email in Xellent before migrating.</para>
///
/// <para>Reported as a structured entry only (<c>LogAsMessage: false</c>) — it is a worksheet
/// item to act on after the run, not something to react to while it runs. The entry counts
/// accounts per email value and names only the ones that deviate from the majority, so a
/// 30-account group reads as one decision instead of 30 identifiers.</para>
/// </summary>
public sealed class EmailOnlyFingerprintSplitValidator : ICustomerBatchValidator
{
    public IEnumerable<ValidationEntry> Validate(IReadOnlyList<Customer> customers) =>
        Validate([.. customers.Select(c => new CustomerEmailFacts(
            AccountNumber: c.AccountNumber,
            Cvr: c.Cvr,
            Name: c.Name,
            AdditionalName: c.AdditionalName,
            StreetName: c.Address.StreetName,
            PostalCode: c.Address.PostalCode,
            City: c.Address.City,
            Email: c.Contact.Email))]);

    /// <summary>
    /// The same rule over the handful of fields it actually reads.
    ///
    /// <para>For a run that migrates in batches: no batch can answer "how many accounts share this
    /// name and address", because it only holds a slice of them, and an operator who acts on
    /// "2 accounts" when the group really has 30 is editing Xellent in the dark. The batches
    /// carry these facts out and the run asks the question once, with every account in view.</para>
    /// </summary>
    public IEnumerable<ValidationEntry> Validate(IReadOnlyList<CustomerEmailFacts> customers)
    {
        var byCvr = customers
            .Where(c => !string.IsNullOrEmpty(c.Cvr))
            .GroupBy(c => c.Cvr!);

        foreach (var cvrGroup in byCvr)
        {
            if (cvrGroup.Count() < 2) continue;

            var withFp = cvrGroup
                .Select(c => (
                    Customer: c,
                    EmailFreeFp: FingerprintCalculator.Compute(
                        c.Name, c.AdditionalName,
                        c.StreetName, c.PostalCode, c.City)))
                .ToList();

            foreach (var fpGroup in withFp.GroupBy(x => x.EmailFreeFp))
            {
                var members = fpGroup.ToList();
                if (members.Count < 2) continue;

                var withEmail = members.Where(x => !string.IsNullOrEmpty(x.Customer.Email)).ToList();
                var withoutEmail = members.Where(x => string.IsNullOrEmpty(x.Customer.Email)).ToList();

                if (withEmail.Count == 0 || withoutEmail.Count == 0) continue;

                // Count per email value and name only the accounts that deviate from the
                // majority. A group of 30 accounts where 27 agree is one decision, not 30 —
                // listing all of them buries the three that actually need editing.
                var buckets = members
                    .GroupBy(x => x.Customer.Email ?? "", StringComparer.Ordinal)
                    .Select(g => (
                        Email: g.Key,
                        Accounts: g.Select(x => x.Customer.AccountNumber)
                            .OrderBy(a => a, StringComparer.Ordinal).ToList()))
                    .OrderByDescending(b => b.Accounts.Count)
                    .ThenBy(b => b.Email, StringComparer.Ordinal)
                    .ToList();

                var majority = buckets[0];
                var deviating = buckets.Skip(1).ToList();

                var header = $"{Describe(members[0].Customer)} — {Count(members.Count)} med samme navn/adresse fordelt på {buckets.Count} email-værdier; oprettes som separate Settl-kunder";
                var lines = new List<string> { header, $"{Label(majority.Email)} — {Count(majority.Accounts.Count)} (flertal)" };
                lines.AddRange(deviating.Select(b =>
                    $"{Label(b.Email)} — {Count(b.Accounts.Count)}: {Join(b.Accounts)}"));
                lines.Add("Ensret email i Xellent hvis de skal blive én kunde.");

                yield return new ValidationEntry(
                    Severity: "Warning",
                    EntityType: "Customer",
                    // The accounts to go and edit — not all members of the group.
                    EntityId: Join(deviating.SelectMany(b => b.Accounts).ToList()),
                    Pattern: string.Join("\n", lines),
                    SettlField: "CustomerNumber",
                    XellentField: "Email / DirECommunication",
                    XellentValue: string.Join(", ", deviating.Select(b => Label(b.Email))),
                    LogAsMessage: false);
            }
        }
    }

    private const int MaxNamedAccounts = 10;

    private static string Describe(CustomerEmailFacts c) =>
        string.IsNullOrWhiteSpace(c.City) ? c.Name : $"{c.Name}, {c.City}";

    private static string Label(string email) =>
        string.IsNullOrEmpty(email) ? "(ingen email)" : email;

    private static string Count(int n) => n == 1 ? "1 konto" : $"{n} konti";

    private static string Join(IReadOnlyList<string> accounts)
    {
        if (accounts.Count <= MaxNamedAccounts)
            return string.Join(", ", accounts);
        return string.Join(", ", accounts.Take(MaxNamedAccounts)) + $" (+{accounts.Count - MaxNamedAccounts})";
    }
}
