namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Limits imposed by the database rather than by the domain.
/// </summary>
internal static class XellentQueryLimits
{
    /// <summary>
    /// Ids per query when a client-side list has to be pushed into SQL. EF renders
    /// <c>Contains</c> as <c>IN (@p0 … @pN)</c>, and SQL Server abandons the plan somewhere past
    /// a few thousand parameters: "the query processor ran out of internal resources".
    ///
    /// <para>Note that the same error also covers the opposite failure — a query too complex to
    /// plan <i>because</i> it has no selective predicate. Chunking fixes the first; keeping the
    /// predicate at all fixes the second. Both are needed, which is why the account set is
    /// filtered in portions rather than dropped.</para>
    /// </summary>
    public const int ChunkSize = 2000;

    /// <summary>
    /// Ids per query for the delpoint-keyed lookups, which push two lists into SQL at once
    /// (delpoint numbers and attachment numbers, plus a handful of company ids) to hit the whole
    /// clustered key. Half of <see cref="ChunkSize"/> keeps the total parameter count in the same
    /// range as a single-list query.
    /// </summary>
    public const int DelpointKeyChunkSize = ChunkSize / 2;
}
