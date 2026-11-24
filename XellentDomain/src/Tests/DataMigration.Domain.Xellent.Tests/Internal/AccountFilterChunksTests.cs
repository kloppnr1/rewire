using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The account filter is split before it reaches SQL. Two things must survive the split: "no filter"
/// has to stay "no filter", and the portions together have to be exactly the input.
/// </summary>
public class AccountFilterChunksTests
{
    [Fact]
    public void Null_stays_one_unfiltered_read()
    {
        var portions = AccountFilterChunks.Split(null).ToList();

        // One portion, still null — the IN list never enters the SQL, so the query keeps meaning
        // "every account in the data area".
        Assert.Single(portions);
        Assert.Null(portions[0]);
    }

    [Fact]
    public void Empty_reads_nothing_at_all()
    {
        // An empty filter matched no rows before; no portions means no query, same answer.
        Assert.Empty(AccountFilterChunks.Split([]));
    }

    [Fact]
    public void Portions_together_are_the_input_in_order()
    {
        var accounts = Enumerable.Range(1, 4501).Select(i => i.ToString()).ToArray();

        var portions = AccountFilterChunks.Split(accounts).ToList();

        Assert.Equal(3, portions.Count);
        Assert.All(portions, p => Assert.NotNull(p));
        Assert.Equal(accounts, portions.SelectMany(p => p!).ToArray());
    }

    [Fact]
    public void No_portion_exceeds_the_query_limit()
    {
        var accounts = Enumerable.Range(1, 4501).Select(i => i.ToString()).ToArray();

        Assert.All(
            AccountFilterChunks.Split(accounts),
            p => Assert.InRange(p!.Length, 1, XellentQueryLimits.ChunkSize));
    }

    [Fact]
    public void A_set_below_the_limit_is_still_a_single_read()
    {
        var accounts = Enumerable.Range(1, 10).Select(i => i.ToString()).ToArray();

        var portions = AccountFilterChunks.Split(accounts).ToList();

        Assert.Single(portions);
        Assert.Equal(accounts, portions[0]);
    }
}
