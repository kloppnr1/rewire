using DataMigration.XellentData.Reporting;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// Which contract parts count towards a contract's commodity. The rule is shared by the domain
/// mapper (MobilePaySubscription.SourceDeliveryCategory) and the MobilePay handover report, so a
/// change here changes both.
/// </summary>
public class ContractDeliveryCategoryTests
{
    private static readonly DateTime NeverEnds = new(1900, 1, 1);

    [Fact]
    public void Part_that_never_ends_is_in_force()
    {
        Assert.True(ContractDeliveryCategoryReader.IsInForce(NeverEnds, new DateTime(2026, 7, 30)));
    }

    [Fact]
    public void Part_that_ended_years_ago_is_not_in_force()
    {
        // Production, contracts 10067406 and 10067704: a Kloak part ended 2018-12-31 while Vand and
        // Varme stayed open. Reporting the commodity as "Kloak/Vand/Varme" says the contract still
        // delivers sewage, which it has not done for eight years.
        Assert.False(ContractDeliveryCategoryReader.IsInForce(
            new DateTime(2018, 12, 31), new DateTime(2026, 7, 30)));
    }

    [Fact]
    public void Part_ending_exactly_on_the_reference_date_still_counts()
    {
        // The end date is inclusive in Xellent — the part delivered on its last day.
        Assert.True(ContractDeliveryCategoryReader.IsInForce(
            new DateTime(2026, 3, 1), new DateTime(2026, 3, 1)));
    }

    [Fact]
    public void A_migrated_contracts_parts_are_measured_at_the_contracts_own_end_date()
    {
        // Migrating to Settl ends the Xellent contract and its parts with it. Measured at today
        // every part would look expired and the commodity column would be blank for exactly the
        // customers a handover report is about; measured at the contract's end date it survives.
        var contractEnded = new DateTime(2026, 3, 1);
        var partEndedWithIt = new DateTime(2026, 3, 1);

        Assert.True(ContractDeliveryCategoryReader.IsInForce(partEndedWithIt, contractEnded));
        Assert.False(ContractDeliveryCategoryReader.IsInForce(partEndedWithIt, DateTime.Today));
    }

    [Fact]
    public void Future_part_counts()
    {
        // Settl holds current and future contract parts, so a part not yet started still describes
        // what the contract delivers. Only ended parts are excluded.
        Assert.True(ContractDeliveryCategoryReader.IsInForce(
            new DateTime(2030, 1, 1), new DateTime(2026, 7, 30)));
    }
}
