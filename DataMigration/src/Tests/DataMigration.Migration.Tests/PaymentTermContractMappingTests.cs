using System.Text.Json;
using DataMigration.Core;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// The root cause behind the production incident on account 525483: the profile said
/// <c>lbm+1d</c>, every live contract part in Xellent says <c>Lbm+1d</c>, and the lookup was
/// case-sensitive — so ~667 accounts across VGGSpotH0 and VGGSpot10 resolved to nothing, fell back
/// to the raw product name and landed in Settl with no contract. The neighbouring VGGSpot0 worked
/// only because somebody happened to type its key with a capital L. Whether an account gets its
/// contract may not depend on that, and when a term genuinely does not match, the log has to say
/// which term and which keys — the old message blamed a missing mapping that was right there.
/// </summary>
public class PaymentTermContractMappingTests
{
    private const string Account = "525483";
    private const string MeteringPoint = "571313179104952730";
    private const string ProductNum = "VGGSpotH0";

    [Fact]
    public void A_payment_term_that_differs_only_by_case_still_finds_its_contract()
    {
        // Profile key lowercase, Xellent value capitalised — the exact production combination.
        var entry = FromJson("""{"lb+31": "E_Lb31", "lbm+1d": "E_Spot_H0"}""");

        var result = Resolve(entry, paymentTerm: "Lbm+1d");

        Assert.Equal("E_Spot_H0", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void The_same_holds_for_an_entry_built_in_code()
    {
        // The rule belongs to the entry, not to the JSON reader — a dashboard-built mapping must
        // behave like a profile-loaded one.
        var entry = new ProductNumContractEntry
        {
            ByPaymentTerm = new Dictionary<string, string> { ["lbm+1d"] = "E_Spot_H0" }
        };

        Assert.Equal("E_Spot_H0", entry.Resolve("LBM+1D"));
    }

    [Fact]
    public void The_wildcard_default_still_wins_when_no_term_matches()
    {
        var entry = FromJson("""{"lbm+1d": "E_Spot_H0", "*": "E_Spot_Default"}""");

        var result = Resolve(entry, paymentTerm: "M14");

        Assert.Equal("E_Spot_Default", result.MpContractNames[MeteringPoint]);
    }

    [Fact]
    public void A_term_matching_nothing_falls_back_and_the_message_names_the_term_and_the_keys()
    {
        var entry = FromJson("""{"lbm+1d": "E_Spot_H0", "lb+31": "E_Lb31"}""");

        var (result, infos) = ResolveWithInfos(entry, paymentTerm: "M14");

        // Fallback behaviour itself is unchanged — the product name is still used.
        Assert.Equal(ProductNum, result.MpContractNames[MeteringPoint]);

        var msg = Assert.Single(infos);
        Assert.Contains($"product '{ProductNum}' is mapped", msg);
        Assert.Contains("payment term 'M14'", msg);
        Assert.Contains("'lb+31', 'lbm+1d'", msg);
        Assert.Contains("no '*' default", msg);

        // The old text blamed a missing mapping. That is what made this invisible.
        Assert.DoesNotContain("no contract mapping configured", msg);
    }

    [Fact]
    public void An_empty_payment_term_is_named_as_empty_rather_than_left_blank()
    {
        var entry = FromJson("""{"lbm+1d": "E_Spot_H0"}""");

        var (_, infos) = ResolveWithInfos(entry, paymentTerm: "");

        Assert.Contains("payment term '(empty)'", Assert.Single(infos));
    }

    [Fact]
    public void A_product_nobody_mapped_still_says_exactly_that()
    {
        var (_, infos) = ResolveWithInfos(entry: null, paymentTerm: "Lbm+1d");

        var msg = Assert.Single(infos);
        Assert.Contains("no contract mapping configured", msg);
        Assert.Contains($"using contract part product '{ProductNum}'", msg);
    }

    private static ProductNumContractEntry FromJson(string json) =>
        JsonSerializer.Deserialize<ProductNumContractEntry>(json)!;

    private static ContractNameResolver.Result Resolve(ProductNumContractEntry? entry, string paymentTerm) =>
        ResolveWithInfos(entry, paymentTerm).Result;

    /// <summary>
    /// One account, one metering point, one contract part — run through the same local resolution
    /// the runner uses, so the message under test is the one a run would actually print.
    /// </summary>
    private static (ContractNameResolver.Result Result, IReadOnlyList<string> Infos) ResolveWithInfos(
        ProductNumContractEntry? entry, string paymentTerm)
    {
        var options = new CreateOptions
        {
            Accounts = [Account],
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            ReferenceDate = new DateTime(2026, 1, 1),
            ProductNumContractMap = entry == null
                ? null
                : new Dictionary<string, ProductNumContractEntry> { [ProductNum] = entry }
        };

        var day = new DateTime(2026, 1, 1);
        var mapping = new Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>
        {
            [Account] = [(MeteringPoint, day, day, day, false, "C1", ProductNum, "", paymentTerm)]
        };

        var warnings = new WarningCollector();
        var resolver = new ContractNameResolver(new Fixtures.MockContractClient(), warnings, _ => { });
        var result = resolver.ResolveLocal(options, [], mapping);

        return (result, warnings.GetInfos());
    }
}
