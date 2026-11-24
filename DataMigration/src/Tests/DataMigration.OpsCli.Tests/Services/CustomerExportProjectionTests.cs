using System.Text.Json.Nodes;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class CustomerExportProjectionTests
{
    [Fact]
    public void Metering_point_contract_and_extra_line_dates_are_danish_calendar_dates()
    {
        var raw = JsonNode.Parse("""
            {
              "customer": {"CustomerNumber": "100001"},
              "meteringPoints": [{
                "meteringPointId": "mp-guid",
                "billing": {"MeteringPointIdentification": "571313100000000001"},
                "market": {
                  "Consumer": {"SupplyStart": "2022-12-31T23:00:00Z"},
                  "DeliveryStatus": {"InDelivery": true, "CalculatedDeliveryPeriod": {
                    "Start": "2023-06-30T22:00:00Z", "Stop": "9999-12-31T23:59:59Z"}}
                }
              }],
              "contracts": [{
                "Id": "contract-guid", "ContractName": "C1",
                "MeteringPoints": [{"MeteringPointId": "mp-guid",
                  "Start": "2022-12-31T23:00:00Z", "End": "2024-03-30T23:00:00Z"}]
              }],
              "extraInvoiceLines": [{"Data": {"Text": "Gebyr",
                "InvoicePeriod": {"Start": "2025-12-31T23:00:00Z", "End": "2026-01-31T23:00:00Z"}}}]
            }
            """)!.AsObject();

        var projected = CustomerExportProjection.Project(raw);

        var mp = projected["meteringPoints"]![0]!;
        Assert.Equal("2023-01-01", (string?)mp["consumer"]!["supplyStart"]);
        Assert.Equal("2023-07-01", (string?)mp["deliveryStatus"]!["start"]);
        Assert.Null(mp["deliveryStatus"]!["stop"]);
        var link = mp["contracts"]![0]!;
        Assert.Equal("2023-01-01", (string?)link["onMeteringPointFrom"]);
        Assert.Equal("2024-03-31", (string?)link["onMeteringPointTo"]);
        var extra = projected["extraInvoiceLines"]![0]!;
        Assert.Equal("2026-01-01", (string?)extra["start"]);
        Assert.Equal("2026-02-01", (string?)extra["end"]);
    }

    [Fact]
    public void A_date_without_zone_keeps_its_own_date()
    {
        var raw = JsonNode.Parse("""
            {"customer": {"CustomerNumber": "100001"},
             "meteringPoints": [{"market": {"Consumer": {"SupplyStart": "2023-01-01"}}}]}
            """)!.AsObject();

        var projected = CustomerExportProjection.Project(raw);

        Assert.Equal("2023-01-01", (string?)projected["meteringPoints"]![0]!["consumer"]!["supplyStart"]);
    }
}
