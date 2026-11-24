using DataMigration.Core;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// Pairing a logged contract part with the live row must not depend on how Xellent happened to spell
/// the company id or the delivery category. Both vary in production dataarea 'hol':
/// <c>EXU_CONTRACTPARTTABLE</c> holds 371.982 rows with <c>COMPANYID</c> 'for' and 13.074 with 'For',
/// and <c>EXU_CONTRACTPARTLOG</c> writes 'El-Ekstern' where the live tables write 'El-ekstern'.
/// SQL Server compares them equal; an ordinal dictionary does not — so an end date the batch
/// overwrote would silently stay overwritten.
/// </summary>
public class ContractPartRestore_KeyCasingTests
{
    private static ContractPartLogKey Key(string companyId, string deliveryCategory)
        => new("10291987", "3040895", deliveryCategory, companyId, "V Kvartal", "", new DateTime(2016, 4, 1));

    [Fact]
    public void Company_id_casing_does_not_change_the_key()
    {
        Assert.Equal(
            ContractPartRestore.NormalizeKey(Key("for", "El-ekstern")),
            ContractPartRestore.NormalizeKey(Key("For", "El-ekstern")));
    }

    [Fact]
    public void Delivery_category_casing_does_not_change_the_key()
    {
        Assert.Equal(
            ContractPartRestore.NormalizeKey(Key("for", "El-ekstern")),
            ContractPartRestore.NormalizeKey(Key("for", "El-Ekstern")));
    }

    [Fact]
    public void Normalized_key_carries_the_live_delivery_category_constant()
    {
        Assert.Equal(
            MigrationConstants.DeliveryCategoryElectricity,
            ContractPartRestore.NormalizeKey(Key("For", "El-Ekstern")).DeliveryCategory);
    }

    [Fact]
    public void Metering_point_lookup_key_folds_both_columns_too()
    {
        Assert.Equal(
            ContractPartRestore.LookupKey(Key("for", "El-ekstern")),
            ContractPartRestore.LookupKey(Key("For", "El-Ekstern")));
    }

    [Fact]
    public void Non_electricity_delivery_categories_are_left_alone()
    {
        Assert.Equal("Vand", ContractPartRestore.NormalizeKey(Key("van", "Vand")).DeliveryCategory);
    }

    [Fact]
    public void Company_key_handles_a_missing_value()
    {
        Assert.Equal(string.Empty, ContractPartRestore.CompanyKey(null));
    }
}
