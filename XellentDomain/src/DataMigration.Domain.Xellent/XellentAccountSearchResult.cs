namespace DataMigration.Domain.Xellent;

/// <summary>
/// Output from <see cref="XellentAccountSearch.SearchAsync"/>. Matches are ordered by
/// relevance (street + DarId) descending; ties are broken by SQL's physical row order.
/// </summary>
/// <param name="Matches">Returned matches (capped at <see cref="XellentAccountSearchCriteria.MaxResults"/>).</param>
/// <param name="TotalCount">True row count of the address-joined+filtered query before
/// <c>Take</c>. Equals <see cref="Matches"/>.Count when results aren't capped.</param>
public sealed record XellentAccountSearchResult(
    IReadOnlyList<XellentAccountMatch> Matches,
    int TotalCount);
