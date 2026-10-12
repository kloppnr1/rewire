using DataMigration.Domain.Xellent;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Which national identifier a Xellent customer carries. Decides the Settl Customer Segment together
/// with the contract template: Settl does not accept a private segment on a CVR number, and Xellent
/// holds both kinds of customer under the same template.
/// </summary>
public enum CustomerIdentityKind
{
    /// <summary>Neither a valid CVR nor a valid CPR — the customer is unidentified in Xellent.</summary>
    Blank,
    /// <summary>Valid 8-digit <c>CustTable.ExuCvrDisponent1</c> — a company.</summary>
    Cvr,
    /// <summary>Valid 10-digit CPR and no CVR — a person.</summary>
    Cpr
}

/// <summary>
/// Reads the national identifier of Xellent accounts and classifies each as CVR / CPR / blank.
///
/// <para>Same sources and same precedence as <c>CustomerMapper</c>: <c>ExuCvrDisponent1</c> must be
/// 8 digits to count, CPR comes from <c>ExuCprDisponent1</c> or the <c>PNR=</c>-prefixed
/// <c>Exualternativecustomernumber</c> (<see cref="IdentifierHelpers.ResolveCpr"/>), and CVR wins
/// when both are present — that is what makes the customer a company in the migration.</para>
///
/// <para><b>Ask for representatives, not raw contract accounts.</b> The segment is a property of the
/// Settl customer, so the identifier must be read on the account whose Accountnum became the Settl
/// CustomerNumber (see docs/representation-principle.md). Merged group members can only merge on a
/// non-empty CVR, so a CPR/blank account is always its own representative — but a merged member's
/// own row is still the wrong one to ask.</para>
/// </summary>
public static class CustomerIdentityResolver
{
    // SQL Server caps parameters at 2100; keep each IN-batch well under that.
    private const int BatchSize = 1500;

    public static async Task<IReadOnlyDictionary<string, CustomerIdentityKind>> ResolveAsync(
        string connectionString,
        IReadOnlyCollection<string> accounts,
        string dataAreaId,
        CancellationToken ct = default)
    {
        var distinct = accounts
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, CustomerIdentityKind>(StringComparer.Ordinal);
        if (distinct.Count == 0) return result;

        var contextOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(contextOptions);

        for (var offset = 0; offset < distinct.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = distinct.GetRange(offset, Math.Min(BatchSize, distinct.Count - offset));

            var rows = await context.CustTables
                .Where(c => c.Dataareaid == dataAreaId && batch.Contains(c.Accountnum))
                .Select(c => new
                {
                    c.Accountnum,
                    Cvr = c.ExuCvrDisponent1,
                    Cpr = c.ExuCprDisponent1,
                    Alternative = c.Exualternativecustomernumber
                })
                .ToListAsync(ct);

            foreach (var row in rows)
                result[row.Accountnum] = Classify(row.Cvr, row.Cpr, row.Alternative);
        }

        // Accounts with no CustTable row stay absent rather than being reported as blank: "no such
        // customer" and "customer without an identifier" are different findings, and the caller
        // decides how to treat the gap.
        return result;
    }

    /// <summary>Pure classification, exposed for tests and for callers that already hold the fields.</summary>
    public static CustomerIdentityKind Classify(string? cvr, string? cprDisponent, string? alternativeCustomerNumber)
    {
        if (!string.IsNullOrEmpty(cvr) && cvr.Length == 8 && cvr.All(char.IsDigit))
            return CustomerIdentityKind.Cvr;

        return IdentifierHelpers.ResolveCpr(cprDisponent, alternativeCustomerNumber) != null
            ? CustomerIdentityKind.Cpr
            : CustomerIdentityKind.Blank;
    }
}
