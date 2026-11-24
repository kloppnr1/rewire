using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Engros;

namespace DataMigration.Domain.Engros.Tests;

public class SubscriptionFeeShapeTests
{
    [Fact]
    public void Can_construct_subscription_fee()
    {
        var fee = new SubscriptionFee(
            Code: "ABO-MONTHLY",
            Description: "Månedligt abonnement",
            OwnerGln: null,
            Amount: 49m,
            BillingFrequency: BillingFrequency.Monthly,
            Validity: new Period(new DateOnly(2024, 1, 1), null));

        Assert.Equal("ABO-MONTHLY", fee.Code);
        Assert.Equal(49m, fee.Amount);
        Assert.Equal(BillingFrequency.Monthly, fee.BillingFrequency);
    }

    [Fact]
    public void Records_with_same_values_are_value_equal()
    {
        var period = new Period(new DateOnly(2024, 1, 1), null);
        var a = new SubscriptionFee("X", "X-fee", null, 100m, BillingFrequency.Quarterly, period);
        var b = new SubscriptionFee("X", "X-fee", null, 100m, BillingFrequency.Quarterly, period);
        Assert.Equal(a, b);
    }
}
