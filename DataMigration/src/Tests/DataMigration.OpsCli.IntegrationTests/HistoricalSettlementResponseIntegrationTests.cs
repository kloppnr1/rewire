using System.Text.Json;
using System.Text.Json.Serialization;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Sanitized production response regression: Sep 1 delivery, Sep 2 tenant replacement.
/// Uses the published SettlClient DTO converters; no network, credentials or personal fields.</summary>
public class HistoricalSettlementResponseIntegrationTests
{
    [Fact]
    public async Task Strict_list_keeps_original_customer_from_published_history_and_payer_JSON_after_later_move_in()
    {
        var mpId = Guid.Parse("e450b65d-e7b3-4356-991d-660237b3db3b");
        var original = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string historyJson = """
            {"DeliveryStatuses":[
              {"InDelivery":true,"Period":{"Start":"2026-08-31T22:00:00Z","End":"2026-09-27T22:00:00Z"},"StartReason":"ChangeOfSupplier"},
              {"InDelivery":true,"Period":{"Start":"2026-09-27T22:00:00Z","End":"9999-12-31T23:59:58.999Z"},"StartReason":"MoveIn"}]}
            """;
        const string payersJson = """
            [{"Payer":{"CustomerId":"11111111-1111-1111-1111-111111111111","IsPrimaryCustomerPayer":true},"MeteringPoints":[
              {"MeteringPointId":"e450b65d-e7b3-4356-991d-660237b3db3b","Start":"2026-08-31T22:00:00Z","End":"2026-09-27T22:00:00Z"}]},
             {"Payer":{"CustomerId":"22222222-2222-2222-2222-222222222222","IsPrimaryCustomerPayer":true},"MeteringPoints":[
              {"MeteringPointId":"e450b65d-e7b3-4356-991d-660237b3db3b","Start":"2026-09-27T22:00:00Z","End":"9999-12-31T23:59:58.999Z"}]}]
            """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true,Converters = { new JsonStringEnumConverter() } };
        var history = JsonSerializer.Deserialize<GetDeliveryStatusesResponse>(historyJson,options);
        var payers = JsonSerializer.Deserialize<List<PayerForMeteringPointsDto>>(payersJson,options);
        var result = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(
            new Dictionary<string,Guid> { ["571313179104682026"] = mpId },
            (_,_) => Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((history,null)),
            (_,date,_) =>
            {
                Assert.InRange(date,new DateTime(2026,8,31,22,0,0,DateTimeKind.Utc),
                    new DateTime(2026,9,1,22,0,0,DateTimeKind.Utc).AddTicks(-1));
                return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>((payers,null));
            });
        Assert.Equal(new DateOnly(2026,9,1),Assert.Single(result.DeliveryStartByMeteringPoint).Value);
        Assert.Equal(original,Assert.Single(result.CustomerByMeteringPoint).Value);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(1, "101681")]
    [InlineData(2, "600256")]
    public async Task Production_response_dates_keep_the_customer_on_the_correct_Danish_day(int day, string expectedCustomer)
    {
        var oldId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var newId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        const string historyJson = """
            {"DeliveryStatuses":[
              {"InDelivery":false,"Period":{"Start":"2016-03-31T22:00:00Z","End":"2026-08-31T22:00:00Z"},"StartReason":"Na"},
              {"InDelivery":true,"Period":{"Start":"2026-08-31T22:00:00Z","End":"9999-12-31T23:59:58.999Z"},"StartReason":"ChangeOfSupplier"}]}
            """;
        const string oldPayerJson = """
            [{"Payer":{"CustomerId":"11111111-1111-1111-1111-111111111111","IsPrimaryCustomerPayer":true},"MeteringPoints":[
              {"MeteringPointId":"e450b65d-e7b3-4356-991d-660237b3db3b","Start":"2016-03-31T22:00:00Z","End":"2026-09-01T22:00:00Z"}]}]
            """;
        const string newPayerJson = """
            [{"Payer":{"CustomerId":"22222222-2222-2222-2222-222222222222","IsPrimaryCustomerPayer":true},"MeteringPoints":[
              {"MeteringPointId":"e450b65d-e7b3-4356-991d-660237b3db3b","Start":"2026-09-01T22:00:00Z","End":"9999-12-31T23:59:58.999Z"}]}]
            """;
        var history = JsonSerializer.Deserialize<GetDeliveryStatusesResponse>(historyJson, options);
        var oldPayers = JsonSerializer.Deserialize<List<PayerForMeteringPointsDto>>(oldPayerJson, options);
        var newPayers = JsonSerializer.Deserialize<List<PayerForMeteringPointsDto>>(newPayerJson, options);
        Assert.Equal(new DateOnly(2026, 9, 2), oldPayers![0].MeteringPoints![0].End);
        Assert.Equal(new DateOnly(2026, 9, 2), newPayers![0].MeteringPoints![0].Start);
        Assert.Equal(DateOnly.MaxValue, newPayers[0].MeteringPoints![0].End);
        var result = await HistoricalSettlementSource.ReadAsync(new(2026, 9, day),
            new Dictionary<string, Guid> { ["571313115200421466"] = Guid.Parse("e450b65d-e7b3-4356-991d-660237b3db3b") },
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((history, null)),
            (_, instant, _) => Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>((
                instant < new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc) ? oldPayers : newPayers, null)));
        var names = new Dictionary<Guid, string> { [oldId] = "101681", [newId] = "600256" };
        Assert.Equal(expectedCustomer, names[Assert.Single(result.CustomerByMeteringPoint).Value]);
        Assert.Equal(new DateOnly(2026, 9, 1), Assert.Single(result.DeliveryStartByMeteringPoint).Value);
        Assert.Equal(day == 1 ? 1 : 0, result.ChangeOfSupplierMeteringPoints.Count);
        Assert.Empty(result.Issues);
        // Only the customer returned for the selected day owns this point. A later
        // PROD period must not turn the old tenant into an E18 production customer.
        var on = new DateOnly(2026, 9, day).AddDays(-1);
        var type = SettlementMeterPointTypeEvidence.Resolve([
            new("FORB", new(2020, 1, 1), new(2026, 8, 31)),
            new("PROD", new(2026, 9, 1), new(1900, 1, 1))], on);
        var identity = new Dictionary<string, CustomerIdentityKind> { [expectedCustomer] = CustomerIdentityKind.Cvr };
        SettlementConfigEntry[] entries = [new("571313115200421466", "Month", expectedCustomer, null, "business",
            ReferenceDate: on, MeterPointTypeCode: type.Code, MeterPointTypeReadStatus: type.ReadStatus)];
        var segments = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string, SegmentRule> { ["business"] = new(["Erhverv"], ["Privat"], ["Privat"]) }, identity);
        var production = SettlementE18Proposal.Apply(segments, entries, identity);
        Assert.Equal(day == 1 ? "Erhverv" : SettlementE18Proposal.Segment, Assert.Single(production.Segments.Segments).Segment);
        Assert.Equal(expectedCustomer, Assert.Single(production.Segments.Segments[0].Customers).CustomerNumber);

        var current = await SettlementCurrentCustomerSource.ReadAsync(
            new Dictionary<string, Guid> { ["571313115200421466"] = Guid.Parse("e450b65d-e7b3-4356-991d-660237b3db3b") }, names,
            (_, _, _) => Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>((newPayers, null)),
            new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        var final = SettlementExemptionProposal.Apply(production.Segments, entries, identity,
            [new("571313115200421466", null, "Read", [new(Guid.NewGuid(), "ProductElement", Guid.NewGuid(), 100)], "NotVerified", null)],
            new(2026, 9, day), DateTime.UtcNow, current, "production");
        var exported = SettlementScheduleGrouper.Build(entries, [], new Dictionary<string, string> { ["Month"] = "Monthly" },
            1, "production", "hol", DateTime.UtcNow) with { Segments = final.Segments.Segments, ExemptionReview = final.Review };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(exported, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(day == 1 ? "Erhverv" : SettlementE18Proposal.Segment, json.RootElement.GetProperty("segments")[0].GetProperty("segment").GetString());
        Assert.Equal("Monthly", json.RootElement.GetProperty("schedules")[0].GetProperty("schedule").GetString());
        var exemption = json.RootElement.GetProperty("exemptionReview").GetProperty("meteringPoints")[0];
        Assert.Equal(day == 1 ? "DifferentCustomer" : "SameCustomer", exemption.GetProperty("attributionStatus").GetString());
        Assert.Equal("600256", exemption.GetProperty("currentCustomerNumber").GetString());
        Assert.Equal(expectedCustomer, exemption.GetProperty("customerNumber").GetString());

    }
}
