using System.Text.RegularExpressions;

namespace DataMigration.Domain.Xellent;

/// <summary>Pure helpers for customer identifiers: CPR resolution and email validation.</summary>
public static class IdentifierHelpers
{
    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public static bool IsValidEmail(string email) => EmailRegex.IsMatch(email);

    /// <summary>
    /// Resolve a Danish CPR from the two Xellent sources: CustTable.CprDisponent (preferred) or
    /// AlternativeCustomerNumber with the <c>PNR=</c> prefix. Both must be exactly 10 digits.
    /// Returns null when no valid CPR is found.
    /// </summary>
    public static string? ResolveCpr(string? cprDisponent, string? alternativeCustomerNumber)
    {
        if (!string.IsNullOrEmpty(cprDisponent) && cprDisponent.Length == 10)
            return cprDisponent;

        if (!string.IsNullOrEmpty(alternativeCustomerNumber) &&
            alternativeCustomerNumber.StartsWith("PNR=", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = alternativeCustomerNumber[4..];
            if (parsed.Length == 10)
                return parsed;
        }

        return null;
    }
}
