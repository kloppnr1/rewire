using DataMigration.Migration.Services;

namespace DataMigration.OpsCli.Services;

/// <summary>Compares the COS-pinned Xellent account with Settl's linked customer number.</summary>
internal static class InvoiceSourceMatch
{
    internal enum Kind { None, Direct, Representative }

    internal static Kind Match(string customerNumber, string source, string representative, string? env)
    {
        if (CustomerNumberPolicy.PadForEnv(source, env, out _) == customerNumber) return Kind.Direct;
        return CustomerNumberPolicy.PadForEnv(representative, env, out _) == customerNumber
            ? Kind.Representative : Kind.None;
    }

    internal static string[] ApprovedSources(Kind kind, string source,
        string representative, IReadOnlyDictionary<string, string[]> membersByRepresentative) => kind switch
    {
        Kind.Direct => [source],
        Kind.Representative => membersByRepresentative.GetValueOrDefault(representative) ?? [source],
        _ => []
    };
}
