using DataMigration.OpsCli.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// The multi-version reconcile scan's data source through the real client: the <c>/history</c>
/// endpoint exposes <c>PaymentConfigurationDates</c> alongside <c>BillingConfigurationDates</c>, and
/// each version's effective GET resolves to that entry's own Start. Runs on the fake tenant in
/// <see cref="ContractTestProvisioningFixture"/>, so it pins the shape rather than checking live Settl.
/// </summary>
public class BillingPaymentHistoryTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>
{
    [Fact]
    public async Task History_exposes_billing_and_payment_dates_and_versions_resolve()
    {
        var MultiVersionMp = fixture.MpId; // seeded with two BillingConfiguration versions
        var mp = fixture.Settl.MeteringPointClient;

        var (hist, err) = await mp.GetBillingHistoryAsync(MultiVersionMp);
        Assert.Null(err);
        Assert.NotNull(hist);

        var billingDates = hist!.BillingConfigurationDates ?? [];
        var paymentDates = hist.PaymentConfigurationDates ?? [];
        output.WriteLine($"BillingConfigurationDates: {string.Join(", ", billingDates.Select(d => d.ToString("yyyy-MM-dd")))}");
        output.WriteLine($"PaymentConfigurationDates: {string.Join(", ", paymentDates.Select(d => d.ToString("yyyy-MM-dd")))}");

        // The multi-version scan relies on both lists; this MP is known to carry several billing versions.
        Assert.True(billingDates.Count >= 2, "expected >=2 BillingConfiguration versions on the known MP");

        // PaymentConfigurationDates is the field added in SettlClient — proving it deserializes live is the
        // whole point of the cross-repo change. The /history endpoint always returns the payment timeline
        // for a billing MP, so it must be present (>=1).
        Assert.True(paymentDates.Count >= 1, "PaymentConfigurationDates did not deserialize from /history");

        // Each billing version's effective GET resolves to a distinct entry, and the entry's own Start —
        // not the raw history date — is what apply must PUT back.
        var starts = new List<DateOnly>();
        foreach (var d in billingDates.OrderBy(x => x))
        {
            var (v, gErr) = await mp.GetBillingByIdAsync(MultiVersionMp, d);
            Assert.Null(gErr);
            var start = v?.BillingConfiguration?.Start;
            Assert.NotNull(start);
            output.WriteLine($"  history {d:yyyy-MM-dd} -> entry Start {start:yyyy-MM-dd}");
            starts.Add(start!.Value);
        }
        Assert.Equal(starts.Count, starts.Distinct().Count());
    }
}
