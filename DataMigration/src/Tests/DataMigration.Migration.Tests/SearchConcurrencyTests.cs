using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Hvor mange målepunkts-opslag der må være undervejs mod Settl på én gang, er nødbremsen hvis
/// Settl begynder at svare 429. Den skal kunne skrues ned fra kommandolinjen uden en ny udgivelse,
/// og et tal der ikke når helt frem til kørslens kontekst, er præcis lige så ubrugeligt som ingen
/// nødbremse. Derfor holdes vejen fra valg til kontekst fast her.
/// </summary>
public class SearchConcurrencyTests
{
    private const string Profile = "vand-sample";

    [Fact]
    public void Eight_at_a_time_when_nobody_asked_for_anything()
    {
        var options = new CreateOptions
        {
            DeliveryCategory = "Vand",
            ReferenceDate = new DateTime(2026, 4, 16),
        };

        Assert.Equal(8, options.SearchConcurrency);

        using var run = new ProfileTestRunner(Profile);

        Assert.NotNull(run.MigrationContext);
        Assert.Equal(8, run.MigrationContext!.SearchConcurrency);
    }

    [Fact]
    public void A_chosen_number_reaches_the_run()
    {
        using var run = new ProfileTestRunner(Profile, searchConcurrency: 2);

        Assert.NotNull(run.MigrationContext);
        Assert.Equal(2, run.MigrationContext!.SearchConcurrency);
    }

    [Fact]
    public void One_at_a_time_is_a_legal_choice()
    {
        // Tallet 1 er den gamle adfærd — ét opslag ad gangen. Den skal kunne vælges, ellers er
        // vejen tilbage til før-tilstanden lukket.
        using var run = new ProfileTestRunner(Profile, searchConcurrency: 1);

        Assert.Equal(1, run.MigrationContext!.SearchConcurrency);
    }
}
