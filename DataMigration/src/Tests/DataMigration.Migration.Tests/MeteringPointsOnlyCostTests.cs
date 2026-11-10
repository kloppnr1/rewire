using DataMigration.Migration.Services.Settl;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// En målepunkts-kørsel skal ikke betale for arbejde den aldrig bruger. To ting koster mest, og
/// begge er usynlige i resultatet — derfor holdes de fast her: tilbagelæsningen af de nyoprettede
/// målepunkters Settl-id'er, og opvarmningen af kunde- og betaler-snapshottet. Kørslen slår
/// hverken en kunde eller en betaler op, og den smider id'erne væk når processen slutter.
/// </summary>
public class MeteringPointsOnlyCostTests
{
    private const string Profile = "vand-sample";

    [Fact]
    public void A_metering_points_run_does_not_read_the_new_ids_back()
    {
        using var run = new ProfileTestRunner(Profile, dryRun: false, meteringPointsOnly: true);

        Assert.NotNull(run.MigrationContext);
        Assert.False(run.MigrationContext!.ResolveCreatedMeteringPointIds);
    }

    [Fact]
    public void A_full_run_still_reads_them_back()
    {
        // Id'erne er hele grundlaget for koblinger, faktureringsopsætning og fritagelser. Slukker
        // den her for dem, står en fuld kørsel tilbage uden noget at koble med.
        using var run = new ProfileTestRunner(Profile, dryRun: false);

        Assert.NotNull(run.MigrationContext);
        Assert.True(run.MigrationContext!.ResolveCreatedMeteringPointIds);
    }

    [Fact]
    public void A_metering_points_run_does_not_page_through_customers_and_payers()
    {
        var mpOnlyCustomers = new CountingCustomerClient();
        var mpOnlyPayers = new CountingPayerClient();
        using var mpOnly = new ProfileTestRunner(
            Profile, dryRun: false, meteringPointsOnly: true,
            customerClient: mpOnlyCustomers, payerClient: mpOnlyPayers);

        Assert.Equal(0, mpOnlyCustomers.FindCustomersCalls);
        Assert.Equal(0, mpOnlyPayers.FindPayersCalls);

        // Til sammenligning: den fulde kørsel over samme data henter begge dele — den skal bruge
        // dem til at afgøre hvem der allerede findes i Settl.
        var fullCustomers = new CountingCustomerClient();
        var fullPayers = new CountingPayerClient();
        using var full = new ProfileTestRunner(
            Profile, dryRun: false,
            customerClient: fullCustomers, payerClient: fullPayers);

        Assert.True(fullCustomers.FindCustomersCalls > 0);
        Assert.True(fullPayers.FindPayersCalls > 0);
    }

    [Fact]
    public async Task An_unwarmed_cache_hands_back_the_real_client()
    {
        // Det farlige er ikke at springe opvarmningen over — det er en halvvarm cache, der svarer
        // "findes ikke" ud af et tomt snapshot og dermed får kørslen til at oprette en dublet.
        // Springes en entitet over, skal klienten være præcis den man selv kom med.
        var customers = new CountingCustomerClient();
        var payers = new CountingPayerClient();
        var mps = new CountingMeteringPointClient();

        var cache = await SettlBulkReadCache.CreateAsync(
            customers, payers, mps,
            dryRun: false, deliveryCategory: "Vand",
            preloadMeteringPoints: false,
            preloadCustomers: false,
            preloadPayers: false);

        Assert.Same(customers, cache.CustomerClient);
        Assert.Same(payers, cache.PayerClient);
        Assert.Same(mps, cache.MeteringPointClient);
        Assert.Equal(0, customers.FindCustomersCalls);
        Assert.Equal(0, payers.FindPayersCalls);
        Assert.Equal(0, mps.FindMeteringPointsCalls);
    }

    [Fact]
    public async Task A_warmed_cache_wraps_and_pages()
    {
        var customers = new CountingCustomerClient();
        var payers = new CountingPayerClient();
        var mps = new CountingMeteringPointClient();

        var cache = await SettlBulkReadCache.CreateAsync(
            customers, payers, mps,
            dryRun: false, deliveryCategory: "El",
            preloadMeteringPoints: true);

        Assert.NotSame(customers, cache.CustomerClient);
        Assert.NotSame(payers, cache.PayerClient);
        Assert.NotSame(mps, cache.MeteringPointClient);
        Assert.True(customers.FindCustomersCalls > 0);
        Assert.True(payers.FindPayersCalls > 0);
        Assert.True(mps.FindMeteringPointsCalls > 0);
    }
}
