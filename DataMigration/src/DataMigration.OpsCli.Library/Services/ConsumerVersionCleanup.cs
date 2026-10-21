using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Services;

/// <summary>What happened to one dated Consumer cleanup. Only Rejected and Unverified mean a write reached Settl and failed.</summary>
public enum ConsumerCleanupOutcome { NotSent, Rejected, Unverified, Fixed }

public interface IConsumerVersionCleanup
{
    Task<(ConsumerCleanupOutcome Outcome, string? Error)> BlankAsync(Guid id, DateTime start, CancellationToken cancellationToken);

    /// <summary>Clears only consumer 2's CPR when it is the fictitious 1111110000 and CPR 1 is real. The name is kept.</summary>
    Task<(ConsumerCleanupOutcome Outcome, string? Error)> ClearFictitiousSecondCprAsync(Guid id, DateTime start, CancellationToken cancellationToken);
}

/// <summary>Dated, minimal PATCH using the existing authenticated Settl transport.</summary>
public sealed class ConsumerVersionCleanup(HttpClient http, ISettlAuthService auth,
    IOptions<SettlApiOptions> options, ILogger<ConsumerVersionCleanup> logger)
    : SettlApiClientBase(http, auth, options, logger), IConsumerVersionCleanup
{
    public async Task<(ConsumerCleanupOutcome Outcome, string? Error)> BlankAsync(Guid id, DateTime start, CancellationToken cancellationToken)
    {
        var utc = ConsumerDeliveryEvidence.Utc(start);
        // Same wire format as SettlClient's EffectiveDate reads; Settl rejects "+00:00" as Kind Local.
        var settlDate = utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var url = $"{Options.GetMarketBaseUrl(null)}/api/v4/meteringPoints/{id}?EffectiveDate={Uri.EscapeDataString(settlDate)}";
        var (before, error) = await GetRawAsync(url, cancellationToken);
        if (before is null || error is not null) return (ConsumerCleanupOutcome.NotSent, "Pre-write read failed: " + error);
        var sent = false;
        try
        {
            var point = JsonSerializer.Deserialize<MeteringPointV4Dto>(before, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (point?.Id != id || point.Consumer?.Start is not { } actual || ConsumerDeliveryEvidence.Utc(actual) != utc)
                return (ConsumerCleanupOutcome.NotSent, "Pre-write read returned a different Consumer version.");
            bool? secondHasId = null;
            if (FixConsumer2DuplicateRunner.NeedsNationalIdCheck(point))
            {
                var (hasId, idError) = await SecondHasNationalIdAsync(id, settlDate, cancellationToken);
                if (hasId is null) return (ConsumerCleanupOutcome.NotSent, "Pre-write national id read failed: " + idError);
                secondHasId = hasId;
            }
            if (FixConsumer2DuplicateRunner.Consumer2Reason(point, secondHasId) is null)
                return (ConsumerCleanupOutcome.NotSent, "Consumer is no longer duplicated; no write made.");
            var payload = new JsonObject
            {
                ["TransactionId"] = Guid.NewGuid().ToString(),
                ["MeteringPointPatches"] = new JsonArray(new JsonObject
                {
                    ["Id"] = id.ToString(),
                    ["PatchTime"] = settlDate,
                    ["ConsumerPatch"] = new JsonObject
                    {
                        ["SecondConsumerParty"] = new JsonObject
                        {
                            ["Name"] = new JsonObject { ["FullName"] = null, ["FirstName"] = null, ["LastName"] = null },
                            ["NationalIdentificationNumber"] = ""
                        }
                    }
                })
            };
            var (success, writeError) = await PatchRawAsync($"{Options.GetMarketBaseUrl(null)}/api/v3/meteringPoints", payload.ToJsonString(), cancellationToken);
            sent = true;
            if (!success) return (ConsumerCleanupOutcome.Rejected, writeError);
            var (after, verifyError) = await GetRawAsync(url, cancellationToken);
            if (after is null || verifyError is not null) return (ConsumerCleanupOutcome.Unverified, "Post-write read failed: " + verifyError);
            using var document = JsonDocument.Parse(after);
            var root = document.RootElement;
            var consumer = Field(root, "consumer");
            if (!Guid.TryParse(Field(root, "id").ToString(), out var actualId) || actualId != id
                || !DateTimeOffset.TryParse(Field(consumer, "start").ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var actualStart) || actualStart != utc)
                return (ConsumerCleanupOutcome.Unverified, "Post-write read returned a different Consumer version.");
            var party = Field(consumer, "secondConsumerParty");
            var name = Field(party, "name");
            if (!Blank(Field(name, "fullName")) || !Blank(Field(name, "firstName")) || !Blank(Field(name, "lastName")))
                return (ConsumerCleanupOutcome.Unverified, "Post-write verification: second Consumer name is not blank.");
            // The metering-point read never carries national ids, so check the dedicated read.
            var (stillHasId, verifyIdError) = await SecondHasNationalIdAsync(id, settlDate, cancellationToken);
            if (stillHasId is null) return (ConsumerCleanupOutcome.Unverified, "Post-write national id read failed: " + verifyIdError);
            if (stillHasId.Value)
                return (ConsumerCleanupOutcome.Unverified, "Post-write verification: second Consumer national identification is not blank.");
            return (ConsumerCleanupOutcome.Fixed, null);
        }
        catch (JsonException) { return (sent ? ConsumerCleanupOutcome.Unverified : ConsumerCleanupOutcome.NotSent, "Invalid Consumer JSON; cleanup could not be verified."); }
    }

    public async Task<(ConsumerCleanupOutcome Outcome, string? Error)> ClearFictitiousSecondCprAsync(Guid id, DateTime start, CancellationToken cancellationToken)
    {
        var utc = ConsumerDeliveryEvidence.Utc(start);
        var settlDate = utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var url = $"{Options.GetMarketBaseUrl(null)}/api/v4/meteringPoints/{id}?EffectiveDate={Uri.EscapeDataString(settlDate)}";
        var (before, error) = await GetRawAsync(url, cancellationToken);
        if (before is null || error is not null) return (ConsumerCleanupOutcome.NotSent, "Pre-write read failed: " + error);
        var sent = false;
        try
        {
            string beforeName;
            JsonObject keptName;
            using (var document = JsonDocument.Parse(before))
            {
                var consumer = Field(document.RootElement, "consumer");
                if (!SameVersion(document.RootElement, id, utc))
                    return (ConsumerCleanupOutcome.NotSent, "Pre-write read returned a different Consumer version.");
                if (Field(consumer, "customerType").ToString() != "Person")
                    return (ConsumerCleanupOutcome.NotSent, "Consumer is not a private customer; no write made.");
                var name = Field(Field(consumer, "secondConsumerParty"), "name");
                beforeName = Raw(name);
                // Settl replaces the whole second party, so a patch without the name clears it (seen on featuretest).
                // Send the name back unchanged.
                string? Text(string field) => Field(name, field) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
                keptName = new JsonObject { ["FullName"] = Text("fullName"), ["FirstName"] = Text("firstName"), ["LastName"] = Text("lastName") };
            }
            var (fictitious, idError) = await FictitiousSecondCprAsync(id, settlDate, cancellationToken);
            if (fictitious is null) return (ConsumerCleanupOutcome.NotSent, "Pre-write national id read failed: " + idError);
            if (!fictitious.Value) return (ConsumerCleanupOutcome.NotSent, "CPR 2 is no longer fictitious next to a real CPR 1; no write made.");
            var payload = new JsonObject
            {
                ["TransactionId"] = Guid.NewGuid().ToString(),
                ["MeteringPointPatches"] = new JsonArray(new JsonObject
                {
                    ["Id"] = id.ToString(),
                    ["PatchTime"] = settlDate,
                    // Empty string: Settl ignores null, so null would leave the CPR in place.
                    ["ConsumerPatch"] = new JsonObject
                    {
                        ["SecondConsumerParty"] = new JsonObject { ["Name"] = keptName, ["NationalIdentificationNumber"] = "" }
                    }
                })
            };
            var (success, writeError) = await PatchRawAsync($"{Options.GetMarketBaseUrl(null)}/api/v3/meteringPoints", payload.ToJsonString(), cancellationToken);
            sent = true;
            if (!success) return (ConsumerCleanupOutcome.Rejected, writeError);
            var (after, verifyError) = await GetRawAsync(url, cancellationToken);
            if (after is null || verifyError is not null) return (ConsumerCleanupOutcome.Unverified, "Post-write read failed: " + verifyError);
            using (var document = JsonDocument.Parse(after))
            {
                if (!SameVersion(document.RootElement, id, utc))
                    return (ConsumerCleanupOutcome.Unverified, "Post-write read returned a different Consumer version.");
                var afterName = Raw(Field(Field(Field(document.RootElement, "consumer"), "secondConsumerParty"), "name"));
                if (afterName != beforeName) return (ConsumerCleanupOutcome.Unverified, "Post-write verification: second Consumer name changed.");
            }
            var (stillHasId, verifyIdError) = await SecondHasNationalIdAsync(id, settlDate, cancellationToken);
            if (stillHasId is null) return (ConsumerCleanupOutcome.Unverified, "Post-write national id read failed: " + verifyIdError);
            if (stillHasId.Value)
                return (ConsumerCleanupOutcome.Unverified, "Post-write verification: second Consumer national identification is not blank.");
            return (ConsumerCleanupOutcome.Fixed, null);
        }
        catch (JsonException) { return (sent ? ConsumerCleanupOutcome.Unverified : ConsumerCleanupOutcome.NotSent, "Invalid Consumer JSON; cleanup could not be verified."); }
    }

    private static string Raw(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "" : value.GetRawText();

    private static bool SameVersion(JsonElement root, Guid id, DateTimeOffset utc) =>
        Guid.TryParse(Field(root, "id").ToString(), out var actualId) && actualId == id
        && DateTimeOffset.TryParse(Field(Field(root, "consumer"), "start").ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var actualStart) && actualStart == utc;

    /// <summary>Whether CPR 2 is fictitious while CPR 1 is real. Only the answer is returned, never the values.</summary>
    private async Task<(bool? Fictitious, string? Error)> FictitiousSecondCprAsync(Guid id, string settlDate, CancellationToken cancellationToken)
    {
        var url = $"{Options.GetMarketBaseUrl(null)}/api/v4/meteringPoints/{id}/nationalIdentificationNumber?EffectiveDate={Uri.EscapeDataString(settlDate)}";
        var (json, error) = await GetRawAsync(url, cancellationToken);
        if (json is null || error is not null) return (null, error);
        try
        {
            using var document = JsonDocument.Parse(json);
            var first = Field(document.RootElement, "firstConsumerPartyNationalIdentificationNumber");
            var second = Field(document.RootElement, "secondConsumerPartyNationalIdentificationNumber");
            if (first.ValueKind == JsonValueKind.Undefined || second.ValueKind == JsonValueKind.Undefined)
                return (null, "National id response is missing a party field.");
            static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (FixConsumer2DuplicateRunner.IsFictitiousCpr(Text(second)) && FixConsumer2DuplicateRunner.IsRealCpr(Text(first)), null);
        }
        catch (JsonException) { return (null, "Invalid national id JSON."); }
    }

    /// <summary>Whether consumer 2 has a national id on the dated version. Only presence is returned, never the value.</summary>
    private async Task<(bool? HasId, string? Error)> SecondHasNationalIdAsync(Guid id, string settlDate, CancellationToken cancellationToken)
    {
        var url = $"{Options.GetMarketBaseUrl(null)}/api/v4/meteringPoints/{id}/nationalIdentificationNumber?EffectiveDate={Uri.EscapeDataString(settlDate)}";
        var (json, error) = await GetRawAsync(url, cancellationToken);
        if (json is null || error is not null) return (null, error);
        try
        {
            using var document = JsonDocument.Parse(json);
            var second = Field(document.RootElement, "secondConsumerPartyNationalIdentificationNumber");
            // A missing field must not read as "blank", or a changed response shape would pass every check.
            if (second.ValueKind == JsonValueKind.Undefined) return (null, "National id response has no second-party field.");
            return (!Blank(second), null);
        }
        catch (JsonException) { return (null, "Invalid national id JSON."); }
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static bool Blank(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
}
