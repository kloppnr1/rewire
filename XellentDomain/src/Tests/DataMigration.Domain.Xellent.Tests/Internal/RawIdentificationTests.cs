using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.Domain.Xellent.Tests.Validation;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The raw identification fields exist so a consumer can tell an empty field apart from an
/// unusable one. These tests pin exactly that: nothing is validated, nothing is interpreted,
/// and nothing falls back from one field to another.
/// </summary>
public class RawIdentificationTests
{
    [Fact]
    public void Invalid_cvr_is_carried_out_as_it_stands()
    {
        // 7 digits — Cvr on the customer would be null here; the raw field must still show it.
        var raw = CustomerMapper.BuildRawIdentification(
            cvr: "1234567", cpr: "", alternativeNumber: "", alternativeCode: 0, anonymize: false);

        Assert.Equal("1234567", raw.Cvr);
    }

    [Fact]
    public void Empty_fields_come_out_as_null_not_empty_string()
    {
        var raw = CustomerMapper.BuildRawIdentification(
            cvr: "", cpr: "", alternativeNumber: "", alternativeCode: 0, anonymize: false);

        Assert.Null(raw.Cvr);
        Assert.Null(raw.Cpr);
        Assert.Null(raw.AlternativeNumber);
        Assert.Equal(0, raw.AlternativeNumberCode);
    }

    [Fact]
    public void Alternative_number_and_code_are_both_carried_out()
    {
        // Code 2 = CVR. The number is passed on untouched — nobody promotes it to Cvr here.
        var raw = CustomerMapper.BuildRawIdentification(
            cvr: "", cpr: "", alternativeNumber: "36665030", alternativeCode: 2, anonymize: false);

        Assert.Equal("36665030", raw.AlternativeNumber);
        Assert.Equal(2, raw.AlternativeNumberCode);
        Assert.Null(raw.Cvr);
    }

    [Fact]
    public void Cpr_with_pnr_prefix_in_alternative_number_is_not_parsed()
    {
        var raw = CustomerMapper.BuildRawIdentification(
            cvr: "", cpr: "", alternativeNumber: "PNR=0101801234", alternativeCode: 6, anonymize: false);

        Assert.Equal("PNR=0101801234", raw.AlternativeNumber);
        Assert.Equal(6, raw.AlternativeNumberCode);
        Assert.Null(raw.Cpr);
    }

    [Fact]
    public void Anonymize_masks_the_cpr_bearing_fields_but_keeps_them_non_empty()
    {
        var raw = CustomerMapper.BuildRawIdentification(
            cvr: "36665030", cpr: "0101801234", alternativeNumber: "PNR=0101801234",
            alternativeCode: 6, anonymize: true);

        Assert.Equal("36665030", raw.Cvr);          // CVR is not personal data
        Assert.NotNull(raw.Cpr);
        Assert.NotEqual("0101801234", raw.Cpr);
        Assert.NotNull(raw.AlternativeNumber);
        Assert.NotEqual("PNR=0101801234", raw.AlternativeNumber);
        Assert.Equal(6, raw.AlternativeNumberCode);
    }

    [Fact]
    public void Customer_built_without_a_loader_defaults_to_none()
    {
        Assert.Equal(RawIdentification.None, TestCustomer.Build().RawIdentification);
    }
}
