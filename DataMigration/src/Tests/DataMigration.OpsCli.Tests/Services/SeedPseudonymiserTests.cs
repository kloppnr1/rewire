using System.Text.Json.Nodes;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SeedPseudonymiserTests
{
    private const string Xellent = """
    {
      "CustTables": [
        { "Accountnum": "400001", "Name": "Jane Doe Holding A/S", "Street": "Fiktivgade 12, 1. th",
          "City": "Randers SØ", "Zipcode": "8960", "Email": "jane.doe@corp.dk", "Phone": "59212761",
          "ExuCprDisponent1": "0101801234", "Exualternativecustomernumber": "C0101801234",
          "Companyregnum": "36665030" },
        { "Accountnum": "400002", "Name": "JANE DOE HOLDING A/S", "Street": "Fiktivgade  12, 1. th",
          "City": "Randers SØ", "Zipcode": "8960", "Email": "JANE.DOE@corp.dk", "Phone": "59212761",
          "ExuCprDisponent1": "1111110000", "Companyregnum": "36665030" }
      ],
      "Addresses": [ { "FullAddress": "Fiktivgade 12\n8960 Randers SØ", "City": "Randers SØ" } ]
    }
    """;

    private const string RawMeteringPoint = """
    {
      "Consumer": {
        "WebAccessCode": "ab12cd34",
        "FirstConsumerParty": { "Name": { "FullName": "Jane Doe" } },
        "ContactPoints": [ { "CommunicationLines": [ { "Type": "Phone", "Value": "59212761" } ] } ]
      },
      "Commands": [ { "Name": "UpdateMasterData", "Description": { "Text": "Update master data" } } ],
      "Notes": { "Remark": "Nøgle hos Jane" }
    }
    """;

    private static JsonNode Parse(string json, SeedPseudonymiser p) => JsonNode.Parse(p.ApplyJson(json))!;

    [Fact]
    public void Replaces_names_streets_email_phone_and_cpr()
    {
        var text = new SeedPseudonymiser().ApplyJson(Xellent);

        foreach (var real in new[] { "Jane", "Doe", "Fiktivgade", "jane.doe", "corp.dk", "59212761", "0101801234" })
            Assert.DoesNotContain(real, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeps_place_ids_cvr_and_the_sentinel_cpr()
    {
        var node = Parse(Xellent, new SeedPseudonymiser());
        var second = node["CustTables"]![1]!;

        Assert.Equal("Randers SØ", second["City"]!.GetValue<string>());
        Assert.Equal("8960", second["Zipcode"]!.GetValue<string>());
        Assert.Equal("400002", second["Accountnum"]!.GetValue<string>());
        Assert.Equal("36665030", second["Companyregnum"]!.GetValue<string>());
        Assert.Equal(SeedPseudonymiser.SentinelCpr, second["ExuCprDisponent1"]!.GetValue<string>());
        Assert.Contains("Randers", node["Addresses"]![0]!["FullAddress"]!.GetValue<string>());
    }

    [Fact]
    public void Keeps_equality_ignoring_case_and_shape_of_numbers()
    {
        var node = Parse(Xellent, new SeedPseudonymiser());
        var a = node["CustTables"]![0]!;
        var b = node["CustTables"]![1]!;

        Assert.Equal(a["Name"]!.GetValue<string>(), b["Name"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal(a["Email"]!.GetValue<string>(), b["Email"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal(a["Phone"]!.GetValue<string>(), b["Phone"]!.GetValue<string>());
        Assert.EndsWith("A/S", a["Name"]!.GetValue<string>());
        Assert.EndsWith(" 12, 1. th", a["Street"]!.GetValue<string>());
        Assert.Matches(@"^\d{8}$", a["Phone"]!.GetValue<string>());
        Assert.Matches(@"^\d{10}$", a["ExuCprDisponent1"]!.GetValue<string>());
        Assert.EndsWith(".example.dk", a["Email"]!.GetValue<string>());
    }

    [Fact]
    public void Cpr_in_alternative_customer_number_gets_the_same_fake()
    {
        var a = Parse(Xellent, new SeedPseudonymiser())["CustTables"]![0]!;

        Assert.Equal("C" + a["ExuCprDisponent1"]!.GetValue<string>(), a["Exualternativecustomernumber"]!.GetValue<string>());
    }

    [Fact]
    public void One_instance_is_consistent_across_documents()
    {
        var p = new SeedPseudonymiser();
        var xellent = Parse(Xellent, p);
        var raw = Parse(RawMeteringPoint, p);

        var phone = xellent["CustTables"]![0]!["Phone"]!.GetValue<string>();
        Assert.Equal(phone, raw["Consumer"]!["ContactPoints"]![0]!["CommunicationLines"]![0]!["Value"]!.GetValue<string>());
        var firstName = xellent["CustTables"]![0]!["Name"]!.GetValue<string>().Split(' ')[0];
        Assert.StartsWith(firstName + " ", raw["Consumer"]!["FirstConsumerParty"]!["Name"]!["FullName"]!.GetValue<string>());
    }

    [Fact]
    public void Raw_metering_point_codes_and_remarks_are_replaced_but_system_names_kept()
    {
        var raw = Parse(RawMeteringPoint, new SeedPseudonymiser());

        var code = raw["Consumer"]!["WebAccessCode"]!.GetValue<string>();
        Assert.NotEqual("ab12cd34", code);
        Assert.Matches("^[a-z]{2}[0-9]{2}[a-z]{2}[0-9]{2}$", code);
        Assert.DoesNotContain("Jane", raw["Notes"]!["Remark"]!.GetValue<string>());
        Assert.Equal("UpdateMasterData", raw["Commands"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal("Update master data", raw["Commands"]![0]!["Description"]!["Text"]!.GetValue<string>());
    }

    [Fact]
    public void A_new_instance_gives_different_fakes()
    {
        var first = new SeedPseudonymiser().ApplyJson(Xellent);
        var second = new SeedPseudonymiser().ApplyJson(Xellent);

        Assert.NotEqual(first, second);
    }
}
