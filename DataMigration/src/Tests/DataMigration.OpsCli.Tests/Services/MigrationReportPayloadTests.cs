using System.IO.Compression;
using System.Text.Json;
using DataMigration.Core;
using DataMigration.Core.Models;
using DataMigration.Migration.Runners;
using DataMigration.OpsCli.Reporting;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

/// <summary>
/// Rapporten renderer træet i browseren fra en gzip'et payload i selve HTML-filen.
/// Testene her låser payloadens form: rækkefølge og typer af trærækker, hvilken blok
/// og hvilket shard en række peger på, og at detaljerne rent faktisk ligger i det shard.
/// </summary>
public class MigrationReportPayloadTests
{
    private const string Mp1 = "571313179104952731";
    private const string Mp2 = "571313179104952732";
    private const string Mp3 = "571313179104952733";
    private const string Mp4 = "571313179104952734";

    private static readonly Guid Cust1001 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Cust2001 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Cust3001 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// To ejergrupper: den første med en enkeltstående konto og en sammenlagt gruppe
    /// (2001 er repræsentant, 2002 smeltet ind), den anden med en enkelt ugrupperet konto.
    /// </summary>
    private static OpsMigrationReport BuildReport()
    {
        var report = new OpsMigrationReport
        {
            AccountGroupings =
            [
                new AccountGrouping("1001", ["1001"], "100", "Gruppe 100", "36665030",
                    new Dictionary<string, string> { ["1001"] = "Fiktivvejelg ApS" }),
                new AccountGrouping("2001", ["2001", "2002"], "100", "Gruppe 100", "87654321",
                    new Dictionary<string, string> { ["2001"] = "Beta A/S", ["2002"] = "Beta Filial" }),
                new AccountGrouping("3001", ["3001"], null, null, null,
                    new Dictionary<string, string> { ["3001"] = "Gamma I/S" }),
            ],
            AccountMeteringPoints = new Dictionary<string, List<string>>
            {
                ["1001"] = [Mp1, Mp2],
                ["2001"] = [Mp3],
                ["2002"] = [Mp4],
            },
            MpContractNames = new Dictionary<string, string>
            {
                [Mp1] = "E_A", [Mp2] = "E_B", [Mp3] = "E_A", [Mp4] = "E_A",
            },
            JoinedMeteringPoints = [Mp2],
        };

        report.Customers.Add(Customer(Cust1001, "1001", "Fiktivvejelg ApS"));
        report.Customers.Add(Customer(Cust2001, "2001", "Beta A/S"));
        report.Customers.Add(Customer(Cust3001, "3001", "Gamma I/S"));
        report.Payers.Add(Payer(Cust1001, "1001"));
        foreach (var mp in new[] { Mp1, Mp2, Mp3, Mp4 })
            report.MeteringPoints.Add(MeteringPoint(mp));

        return report;
    }

    private static CreateCustomerDto Customer(Guid id, string number, string name) => new()
    {
        Id = id,
        CustomerNumber = number,
        CustomerType = "Company",
        CountryCode = "DK",
        Address = new CustomerAddressDto
        {
            Start = new DateOnly(2020, 1, 1),
            PostalAddress = new PostalAddressDto
            {
                Name1 = new ConsumerNameDto { FullName = name },
                PhysicalAddress = new PhysicalAddressDto { PostalCode = "8900", City = "Randers C", CountryCode = "DK" },
            },
        },
    };

    private static CreateCustomerDto Person(string number, string name, string? cpr)
    {
        var c = Customer(Guid.NewGuid(), number, name);
        c.CustomerType = "Person";
        c.NationalIdentificationNumber = cpr;
        return c;
    }

    private static CreatePayerDto Payer(Guid customerId, string payerIdentifier) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        CountryCode = "DK",
        PayerInformation = new PayerInformationDto
        {
            Start = new DateOnly(2020, 1, 1),
            PayerIdentifier = payerIdentifier,
            PostalAddress = new PostalAddressDto
            {
                Name1 = new ConsumerNameDto { FullName = "Betaler " + payerIdentifier },
                PhysicalAddress = new PhysicalAddressDto { PostalCode = "8900", City = "Randers C", CountryCode = "DK" },
            },
        },
        PayerBilling = new CreatePayerBillingDto
        {
            Start = new DateOnly(2020, 1, 1),
            InvoiceChannel = "PDF",
            PaymentChannel = "FIK",
        },
    };

    private static MeteringPointDto MeteringPoint(string identification) => new()
    {
        Id = Guid.NewGuid(),
        Identification = new() { MeteringPointIdentification = identification },
    };

    private static string Generate(OpsMigrationReport report) =>
        OpsMigrationReportGenerator.GenerateHtmlReport(
            report, "test-profil", dryRun: true, warningCollector: new WarningCollector(), accountsCount: 4);

    /// <summary>Trækker et base64+gzip-chunk ud af HTML'en og pakker det ud igen.</summary>
    private static JsonElement Unpack(string base64)
    {
        using var input = new MemoryStream(Convert.FromBase64String(base64));
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        return doc.RootElement.Clone();
    }

    private static string Between(string html, string startMarker, string endMarker)
    {
        var start = html.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Fandt ikke '{startMarker}' i rapporten");
        start += startMarker.Length;
        var end = html.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Fandt ikke afslutning på '{startMarker}'");
        return html[start..end];
    }

    private static JsonElement Index(string html) =>
        Unpack(Between(html, "<script id=\"reportIndex\" type=\"text/plain\">", "</script>").Trim());

    private static List<JsonElement> Shards(string html)
    {
        var json = Between(html, "<script id=\"reportShards\" type=\"application/json\">", "</script>").Trim();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => Unpack(e.GetString()!)).ToList();
    }

    [Fact]
    public void Tree_is_data_not_dom()
    {
        var html = Generate(BuildReport());

        Assert.Contains("class=\"tree-viewport\"", html, StringComparison.Ordinal);
        // Kontonumrene må ikke stå som tekst i HTML'en — de ligger komprimeret i payloaden.
        Assert.DoesNotContain("Gamma I/S", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Customers_without_cvr_and_cpr_are_counted_as_unknown_not_private()
    {
        var report = BuildReport();
        report.Customers.Add(Person("4001", "Fiktiv Personi", cpr: "0101801234"));
        report.Customers.Add(Person("5001", "Fiktiv Personj", cpr: null));

        var detaljer = Between(Generate(report), "<h2>Detaljer</h2>", "</table>");

        Assert.Contains("<tr><td>Privatkunder</td><td class=\"num\">1</td></tr>", detaljer, StringComparison.Ordinal);
        Assert.Contains("<tr><td>Erhvervskunder</td><td class=\"num\">3</td></tr>", detaljer, StringComparison.Ordinal);
        Assert.Contains("<tr><td>Ukendt (hverken CVR eller CPR)</td><td class=\"num\">1</td></tr>", detaljer, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_row_is_left_out_when_every_customer_is_identified()
    {
        var detaljer = Between(Generate(BuildReport()), "<h2>Detaljer</h2>", "</table>");

        Assert.DoesNotContain("Ukendt", detaljer, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_follow_the_tree_order_and_kinds()
    {
        var ix = Index(Generate(BuildReport()));
        var rows = ix.GetProperty("rows").EnumerateArray().ToList();

        int[] expectedKinds =
        [
            ReportPayload.KindOwner,          //  0  gruppe 100
            ReportPayload.KindAccount,        //  1  1001
            ReportPayload.KindContract,       //  2    E_A
            ReportPayload.KindMeteringPoint,  //  3      mp1
            ReportPayload.KindContract,       //  4    E_B
            ReportPayload.KindMeteringPoint,  //  5      mp2
            ReportPayload.KindMergeHeader,    //  6  2 konti sammenlagt
            ReportPayload.KindRepresentative, //  7    2001
            ReportPayload.KindContract,       //  8      E_A
            ReportPayload.KindMeteringPoint,  //  9        mp3
            ReportPayload.KindMergedAway,     // 10    2002
            ReportPayload.KindOwner,          // 11  (ikke grupperet)
            ReportPayload.KindAccount,        // 12  3001
        ];

        Assert.Equal(expectedKinds, rows.Select(r => r.GetProperty("k").GetInt32()).ToArray());

        // Kontrakter sorteres på navn, så E_A kommer før E_B uanset MP-rækkefølgen.
        Assert.Equal("E_A", rows[2].GetProperty("l").GetString());
        Assert.Equal("E_B", rows[4].GetProperty("l").GetString());

        // Kun samlefaktura-MP'et er markeret.
        Assert.False(rows[3].TryGetProperty("j", out _));
        Assert.Equal(1, rows[5].GetProperty("j").GetInt32());

        // Den sammenlagte konto er ikke repræsentant og får derfor ingen MP-rækker.
        Assert.Equal("2002", rows[10].GetProperty("a").GetString());
    }

    [Fact]
    public void An_unknown_customers_raw_xellent_values_reach_the_modal()
    {
        var report = BuildReport();
        report.UnknownIdentities["3001"] = new UnknownIdentitySource(
            RawCvr: null,
            RawCpr: null,
            AlternativeNumber: "29189668",
            AlternativeNumberCode: 2,
            CauseText: "CVR står i alternativ-kundenummeret");

        var customer = Shards(Generate(report))
            .Select(s => s.GetProperty("c"))
            .First(c => c.TryGetProperty("3001", out _))
            .GetProperty("3001");

        var unknown = customer.GetProperty("unknown");
        Assert.Equal("CVR står i alternativ-kundenummeret", unknown.GetProperty("causeText").GetString());
        Assert.Equal("29189668", unknown.GetProperty("alternativeNumber").GetString());
        Assert.Equal(2, unknown.GetProperty("alternativeNumberCode").GetInt32());
    }

    [Fact]
    public void An_identified_customer_carries_no_unknown_block()
    {
        var customer = Shards(Generate(BuildReport()))
            .Select(s => s.GetProperty("c"))
            .First(c => c.TryGetProperty("1001", out _))
            .GetProperty("1001");

        // Null-feltet skrives ikke ud: for en kunde med CVR er der intet at forklare, og
        // payloaden bærer én række per kunde i en fil der i forvejen er megabytes.
        Assert.True(
            !customer.TryGetProperty("unknown", out var u) || u.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void Groups_cover_their_child_rows()
    {
        var ix = Index(Generate(BuildReport()));
        var groups = ix.GetProperty("groups").EnumerateArray()
            .Select(g => g.EnumerateArray().Select(v => v.GetInt32()).ToArray())
            .ToList();

        Assert.Equal(2, groups.Count);
        Assert.Equal([0, 10], groups[0]);   // overskrift på række 0, 10 børn
        Assert.Equal([11, 1], groups[1]);
    }

    [Fact]
    public void Every_row_points_at_the_block_that_owns_it()
    {
        var ix = Index(Generate(BuildReport()));
        var rows = ix.GetProperty("rows").EnumerateArray().ToList();
        int Block(int i) => rows[i].GetProperty("b").GetInt32();

        Assert.Equal(-1, Block(0));   // ejer-overskrifter hører ikke til en blok
        Assert.Equal(-1, Block(11));
        Assert.Equal(0, Block(1));    // 1001 og dens kontrakter/MPs
        Assert.Equal(0, Block(5));
        Assert.Equal(1, Block(6));    // hele merge-gruppen, inkl. den sammenlagte konto
        Assert.Equal(1, Block(10));
        Assert.Equal(2, Block(12));

        // Alle konto-rækker i en blok slår op på repræsentanten — de øvrige medlemmer
        // findes ikke som selvstændige kunder i Settl.
        Assert.Equal(["1001", "2001", "3001"],
            ix.GetProperty("blockAccount").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Search_index_covers_account_name_cvr_and_metering_points()
    {
        var ix = Index(Generate(BuildReport()));
        var search = ix.GetProperty("blockSearch").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Contains("1001", search[0], StringComparison.Ordinal);
        Assert.Contains("fiktivvejelg aps", search[0], StringComparison.Ordinal);
        Assert.Contains("36665030", search[0], StringComparison.Ordinal);
        Assert.Contains(Mp2, search[0], StringComparison.Ordinal);
        Assert.Contains("e_b", search[0], StringComparison.Ordinal);

        // Den sammenlagte kontos nummer og navn skal kunne søges frem via sin blok.
        Assert.Contains("2002", search[1], StringComparison.Ordinal);
        Assert.Contains("beta filial", search[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Shard_holds_the_details_its_blocks_point_at()
    {
        var html = Generate(BuildReport());
        var ix = Index(html);
        var shards = Shards(html);

        Assert.Single(shards);
        Assert.All(ix.GetProperty("blockShard").EnumerateArray(), e => Assert.Equal(0, e.GetInt32()));

        var shard = shards[0];
        Assert.Equal("Fiktivvejelg ApS", shard.GetProperty("c").GetProperty("1001").GetProperty("addr").GetProperty("name").GetString());
        Assert.Equal("1001", shard.GetProperty("p").GetProperty("1001")[0].GetProperty("payerId").GetString());

        // MP-fanen viser gruppens samlede målepunkter — også dem der hænger på den
        // sammenlagte konto 2002.
        Assert.Equal([Mp3, Mp4],
            shard.GetProperty("am").GetProperty("2001").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.Equal(Mp1, shard.GetProperty("m").GetProperty(Mp1).GetProperty("gsrn").GetString());
        Assert.Equal("E_A", shard.GetProperty("cn").GetProperty(Mp1).GetString());
        Assert.Equal([Mp2], shard.GetProperty("j").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Blocks_are_split_across_shards()
    {
        var report = new OpsMigrationReport();
        var total = ReportPayload.CustomersPerShard + 5;
        for (var i = 0; i < total; i++)
        {
            var acct = (100000 + i).ToString();
            report.AccountGroupings.Add(new AccountGrouping(
                acct, [acct], null, null, null, new Dictionary<string, string> { [acct] = "Kunde " + acct }));
            report.Customers.Add(Customer(Guid.NewGuid(), acct, "Kunde " + acct));
        }

        var html = Generate(report);
        var ix = Index(html);
        var shards = Shards(html);
        var blockShard = ix.GetProperty("blockShard").EnumerateArray().Select(e => e.GetInt32()).ToList();

        Assert.Equal(2, shards.Count);
        Assert.Equal(0, blockShard[ReportPayload.CustomersPerShard - 1]);
        Assert.Equal(1, blockShard[ReportPayload.CustomersPerShard]);

        // Kunden ligger i dét shard blokken peger på — ikke i et andet.
        var lastAcct = ix.GetProperty("blockAccount")[total - 1].GetString()!;
        Assert.True(shards[1].GetProperty("c").TryGetProperty(lastAcct, out _));
        Assert.False(shards[0].GetProperty("c").TryGetProperty(lastAcct, out _));
    }
}
