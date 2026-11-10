using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Tests.Customers;

public class GetEligibleAccountsTests
{
    private static Invoice MakeInvoice(string account, DateTime date, BillingCategory category) =>
        new(account, account, date, date, string.Empty, string.Empty, category, PaymentMedia.None, InvoiceStatus.None, 0m, 0m);

    [Fact]
    public void Includes_account_with_final_as_most_recent()
    {
        var invoices = new[]
        {
            MakeInvoice("100001", new DateTime(2025, 1, 1), BillingCategory.Aconto),
            MakeInvoice("100001", new DateTime(2025, 6, 1), BillingCategory.Final)
        };

        var result = InvoiceAccountFilter.GetEligibleAccounts(invoices);

        Assert.Contains("100001", result);
    }

    [Fact]
    public void Includes_account_with_pure_final_as_most_recent()
    {
        var invoices = new[] { MakeInvoice("100001", new DateTime(2025, 6, 1), BillingCategory.PureFinal) };

        var result = InvoiceAccountFilter.GetEligibleAccounts(invoices);

        Assert.Contains("100001", result);
    }

    [Fact]
    public void Excludes_account_with_aconto_as_most_recent()
    {
        var invoices = new[]
        {
            MakeInvoice("100001", new DateTime(2025, 1, 1), BillingCategory.Final),
            MakeInvoice("100001", new DateTime(2025, 6, 1), BillingCategory.Aconto)
        };

        var result = InvoiceAccountFilter.GetEligibleAccounts(invoices);

        Assert.DoesNotContain("100001", result);
    }

    [Fact]
    public void Excludes_account_with_settlement_as_most_recent()
    {
        var invoices = new[] { MakeInvoice("100001", new DateTime(2025, 6, 1), BillingCategory.Settlement) };

        var result = InvoiceAccountFilter.GetEligibleAccounts(invoices);

        Assert.DoesNotContain("100001", result);
    }

    [Fact]
    public void Handles_multiple_accounts_independently()
    {
        var invoices = new[]
        {
            MakeInvoice("100001", new DateTime(2025, 6, 1), BillingCategory.Final),
            MakeInvoice("100002", new DateTime(2025, 6, 1), BillingCategory.Aconto),
            MakeInvoice("100003", new DateTime(2025, 6, 1), BillingCategory.PureFinal)
        };

        var result = InvoiceAccountFilter.GetEligibleAccounts(invoices);

        Assert.Equal(2, result.Count);
        Assert.Contains("100001", result);
        Assert.Contains("100003", result);
    }

    [Fact]
    public void Returns_empty_for_no_invoices()
    {
        var result = InvoiceAccountFilter.GetEligibleAccounts(Array.Empty<Invoice>());

        Assert.Empty(result);
    }
}

public class GetHistoryCutoffDateTests
{
    [Fact]
    public void Returns_jan_1_of_five_years_before_reference_year()
    {
        var result = InvoiceAccountFilter.GetHistoryCutoffDate(new DateTime(2026, 4, 16));

        Assert.Equal(new DateTime(2021, 1, 1), result);
    }

    [Fact]
    public void Leap_day_reference_returns_jan_1_of_five_years_before_reference_year()
    {
        var result = InvoiceAccountFilter.GetHistoryCutoffDate(new DateTime(2024, 2, 29));

        Assert.Equal(new DateTime(2019, 1, 1), result);
    }

    [Fact]
    public void Explicit_historyYears_overrides_default()
    {
        var result = InvoiceAccountFilter.GetHistoryCutoffDate(new DateTime(2026, 9, 3), historyYears: 3);

        Assert.Equal(new DateTime(2023, 1, 1), result);
    }
}

public class BillingCategoryLabelTests
{
    [Theory]
    [InlineData(BillingCategory.None, "Ingen")]
    [InlineData(BillingCategory.Settlement, "Opgørelse")]
    [InlineData(BillingCategory.Aconto, "Aconto")]
    [InlineData(BillingCategory.SettlementAndAconto, "Opgørelse og aconto")]
    [InlineData(BillingCategory.Final, "Slutafregning")]
    [InlineData(BillingCategory.PureFinal, "Ren slutopgørelse")]
    public void Maps_known_categories(BillingCategory category, string expected)
    {
        Assert.Equal(expected, category.ToLabel());
    }

    [Fact]
    public void Maps_unknown_category_with_code()
    {
        Assert.Equal("Unknown (99)", ((BillingCategory)99).ToLabel());
    }
}

public class PaymentMediaLabelTests
{
    [Theory]
    [InlineData(PaymentMedia.None, "Ingen")]
    [InlineData(PaymentMedia.Pbs, "PBS")]
    [InlineData(PaymentMedia.Fik, "FIK")]
    [InlineData(PaymentMedia.OioXml, "OIOXML")]
    [InlineData(PaymentMedia.MobilePay, "MobilePay")]
    public void Maps_known_media(PaymentMedia media, string expected)
    {
        Assert.Equal(expected, media.ToLabel());
    }

    [Fact]
    public void Maps_unknown_media_with_code()
    {
        Assert.Equal("Unknown (42)", ((PaymentMedia)42).ToLabel());
    }
}

public class InvoiceStatusLabelTests
{
    [Theory]
    [InlineData(InvoiceStatus.Invoiced, "Faktureret")]
    [InlineData(InvoiceStatus.Credited, "Krediteret")]
    public void Maps_known_status(InvoiceStatus status, string expected)
    {
        Assert.Equal(expected, status.ToLabel());
    }

    [Theory]
    [InlineData(InvoiceStatus.None)]
    public void Maps_unknown_status_to_empty(InvoiceStatus status)
    {
        Assert.Equal(string.Empty, status.ToLabel());
    }
}
