using DataMigration.Domain.Xellent.Validation;

namespace DataMigration.Domain.Xellent.Tests.Validation;

public class EmailOnlyFingerprintSplitValidatorTests
{
    private readonly EmailOnlyFingerprintSplitValidator _validator = new();

    [Fact]
    public void Same_cvr_name_address_some_with_email_some_without_names_only_the_minority()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "66291", cvr: "18184931", name: "Fiktiv Personk"),
            TestCustomer.Build(accountNumber: "66292", cvr: "18184931", name: "Fiktiv Personk"),
            TestCustomer.Build(accountNumber: "66294", cvr: "18184931", name: "Fiktiv Personk", email: "ubh@ds.example.dk"),
        };

        var entries = _validator.Validate(customers).ToList();

        Assert.Single(entries);
        Assert.Equal("Warning", entries[0].Severity);

        // The two without email are the majority — they are counted, not listed. Only the one
        // that deviates is named, because that is the account someone has to open in Xellent.
        Assert.Equal("66294", entries[0].EntityId);
        Assert.Contains("ubh@ds.example.dk", entries[0].XellentValue);

        // The whole group is still visible in the pattern, as counts per email value.
        Assert.Contains("3 konti med samme navn/adresse", entries[0].Pattern);
        Assert.Contains("(ingen email) — 2 konti (flertal)", entries[0].Pattern);
        Assert.Contains("ubh@ds.example.dk — 1 konto: 66294", entries[0].Pattern);
    }

    [Fact]
    public void Majority_email_is_counted_and_the_deviating_accounts_are_named()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "423748", cvr: "36665030", name: "Newsec", email: "ubi@dt.example.dk"),
            TestCustomer.Build(accountNumber: "423753", cvr: "36665030", name: "Newsec", email: "ubi@dt.example.dk"),
            TestCustomer.Build(accountNumber: "423757", cvr: "36665030", name: "Newsec", email: "ubi@dt.example.dk"),
            TestCustomer.Build(accountNumber: "423747", cvr: "36665030", name: "Newsec", email: "ubj@dt.example.dk"),
            TestCustomer.Build(accountNumber: "423766", cvr: "36665030", name: "Newsec"),
        };

        var entries = _validator.Validate(customers).ToList();

        var entry = Assert.Single(entries);
        Assert.Contains("423747", entry.EntityId);
        Assert.Contains("423766", entry.EntityId);
        Assert.DoesNotContain("423748", entry.EntityId);

        Assert.Contains("5 konti med samme navn/adresse fordelt på 3 email-værdier", entry.Pattern);
        Assert.Contains("ubi@dt.example.dk — 3 konti (flertal)", entry.Pattern);
        Assert.Contains("ubj@dt.example.dk — 1 konto: 423747", entry.Pattern);
        Assert.Contains("(ingen email) — 1 konto: 423766", entry.Pattern);
    }

    [Fact]
    public void Finding_is_structured_only_so_it_does_not_repeat_as_a_log_line()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "66291", cvr: "18184931"),
            TestCustomer.Build(accountNumber: "66294", cvr: "18184931", email: "ubh@ds.example.dk"),
        };

        Assert.False(Assert.Single(_validator.Validate(customers)).LogAsMessage);
    }

    [Fact]
    public void All_without_email_emits_no_warning()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "66291", cvr: "18184931"),
            TestCustomer.Build(accountNumber: "66292", cvr: "18184931"),
        };

        Assert.Empty(_validator.Validate(customers));
    }

    [Fact]
    public void All_with_same_email_emits_no_warning()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "66291", cvr: "18184931", email: "ubk@du.example.dk"),
            TestCustomer.Build(accountNumber: "66292", cvr: "18184931", email: "ubk@du.example.dk"),
        };

        Assert.Empty(_validator.Validate(customers));
    }

    [Fact]
    public void Different_address_within_same_cvr_does_not_trigger()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "1", cvr: "18184931", streetName: "Gade A"),
            TestCustomer.Build(accountNumber: "2", cvr: "18184931", streetName: "Gade B", email: "ubl@dv.example.dk"),
        };

        Assert.Empty(_validator.Validate(customers));
    }

    [Fact]
    public void Single_customer_emits_no_warning()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "66294", cvr: "18184931", email: "ubh@ds.example.dk"),
        };

        Assert.Empty(_validator.Validate(customers));
    }

    [Fact]
    public void Null_cvr_accounts_are_ignored()
    {
        var customers = new[]
        {
            TestCustomer.Build(accountNumber: "1", cvr: null),
            TestCustomer.Build(accountNumber: "2", cvr: null, email: "ubl@dv.example.dk"),
        };

        Assert.Empty(_validator.Validate(customers));
    }
}
