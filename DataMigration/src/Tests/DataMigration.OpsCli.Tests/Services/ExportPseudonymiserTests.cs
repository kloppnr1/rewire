using System.Text.Json.Nodes;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class ExportPseudonymiserTests
{
    private const string Sample = """
    {
      "customerNumber": "405013",
      "customerV2": {
        "NationalIdentificationNumber": "0101801234",
        "VatIdentificationNumber": "36665030",
        "Address": {
          "Email": "someone@example.dk",
          "Phone": "59212761",
          "PostalAddress": {
            "Name1": { "FullName": "Jane Doe" },
            "PhysicalAddress": {
              "StreetName": "Fiktivvejm", "BuildingNumber": "83", "PostalCode": "8960", "City": "Randers SØ",
              "AddressIdentifier": { "Identification": "31876bdc-5a0b-4584-9091-701e5bde6153", "IdentificationKind": "DAR" }
            }
          }
        }
      },
      "payers": [ { "payer": { "PayerBilling": { "NationalIdentificationNumber": "0101801234", "AccountNumber": 1234567 } } } ],
      "meteringPoints": [ {
        "billing": { "MeteringPointIdentification": "571313115242231955" },
        "market": { "Consumer": { "ContactPoints": [ { "CommunicationLines": [ { "Type": "Email", "Value": "someone@example.dk" }, { "Type": "Phone", "Value": "59212761" } ] } ] } }
      } ],
      "contracts": [ { "ContractName": "V Variabel", "ContractDescription": "Fastpris Jane Doe" } ],
      "extraInvoiceLines": [ { "Data": { "Text": "Refusion Fiktivvejm 83" } } ]
    }
    """;

    [Fact]
    public void Replaces_personal_data_and_keeps_shape()
    {
        var node = JsonNode.Parse(Sample)!;
        new ExportPseudonymiser().Apply(node);
        var json = node.ToJsonString();

        foreach (var real in new[] { "0101801234", "36665030", "someone@example.dk", "61000006", "Jane Doe",
                     "Fiktivvejm", "571313115242231955", "31876bdc-5a0b-4584-9091-701e5bde6153", "405013", "1234567" })
            Assert.DoesNotContain(real, json);

        var cpr = node["customerV2"]!["NationalIdentificationNumber"]!.GetValue<string>();
        Assert.Matches("^[1-9][0-9]{9}$", cpr);
        Assert.Matches("^[1-9][0-9]{17}$", node["meteringPoints"]![0]!["billing"]!["MeteringPointIdentification"]!.GetValue<string>());
        Assert.Matches("^[0-9a-z]{8}-[0-9a-z]{4}-", node["customerV2"]!["Address"]!["PostalAddress"]!["PhysicalAddress"]!["AddressIdentifier"]!["Identification"]!.GetValue<string>());
        Assert.EndsWith("@example.invalid", node["customerV2"]!["Address"]!["Email"]!.GetValue<string>());
        Assert.IsType<long>(node["payers"]![0]!["payer"]!["PayerBilling"]!["AccountNumber"]!.GetValue<long>());
    }

    [Fact]
    public void Same_value_gets_same_fake_within_a_run_and_business_fields_stay()
    {
        var node = JsonNode.Parse(Sample)!;
        new ExportPseudonymiser().Apply(node);

        Assert.Equal(
            node["customerV2"]!["NationalIdentificationNumber"]!.GetValue<string>(),
            node["payers"]![0]!["payer"]!["PayerBilling"]!["NationalIdentificationNumber"]!.GetValue<string>());
        Assert.Equal(
            node["customerV2"]!["Address"]!["Email"]!.GetValue<string>(),
            node["meteringPoints"]![0]!["market"]!["Consumer"]!["ContactPoints"]![0]!["CommunicationLines"]![0]!["Value"]!.GetValue<string>());

        var address = node["customerV2"]!["Address"]!["PostalAddress"]!["PhysicalAddress"]!;
        Assert.Equal("8960", address["PostalCode"]!.GetValue<string>());
        Assert.Equal("Randers SØ", address["City"]!.GetValue<string>());
        Assert.Equal("DAR", address["AddressIdentifier"]!["IdentificationKind"]!.GetValue<string>());
        Assert.Equal("V Variabel", node["contracts"]![0]!["ContractName"]!.GetValue<string>());
        Assert.StartsWith("Fastpris Name-", node["contracts"]![0]!["ContractDescription"]!.GetValue<string>());
        Assert.StartsWith("Refusion Street-", node["extraInvoiceLines"]![0]!["Data"]!["Text"]!.GetValue<string>());
    }
}
