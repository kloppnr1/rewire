namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Splits an optional account filter into query-sized portions.
///
/// <para>EF renders <c>accounts.Contains(x)</c> as an inlined <c>IN (…)</c> list. At the size of a
/// whole data area (50.000+ accounts) that single statement is too large for SQL Server to compile
/// on its own — error 8632, "an expression services limit has been reached" — with no join
/// involved. Reading in portions and unioning the rows client-side keeps every statement small.</para>
///
/// <para><c>null</c> means "no filter at all" and is preserved as such: one portion, still null, so
/// the <c>IN</c> list never enters the SQL. An empty array means "no accounts" and yields no
/// portions, which produces no rows — the same answer the unchunked query gave.</para>
///
/// <para>Only safe where the rows are aggregated client-side after every portion has landed. Any
/// grouping or comparison that spans accounts must happen after the loop, never inside it.</para>
/// </summary>
internal static class AccountFilterChunks
{
    /// <inheritdoc cref="AccountFilterChunks"/>
    public static IEnumerable<string[]?> Split(string[]? accounts) =>
        Split(accounts, XellentQueryLimits.ChunkSize);

    /// <inheritdoc cref="AccountFilterChunks"/>
    public static IEnumerable<string[]?> Split(string[]? accounts, int chunkSize) =>
        accounts is null
            ? new string[]?[] { null }
            : accounts.Chunk(chunkSize).Select(c => (string[]?)c);
}
