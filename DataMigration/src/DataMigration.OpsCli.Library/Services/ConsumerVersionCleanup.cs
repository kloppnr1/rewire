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
            if (FixConsumer2DuplicateRunner.FindDuplicateName(point) is null)
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
            if (!Blank(Field(name, "fullName")) || !Blank(Field(name, "firstName")) || !Blank(Field(name, "lastName"))
                || !Blank(Field(party, "nationalIdentificationNumber")))
                return (ConsumerCleanupOutcome.Unverified, "Post-write verification: second Consumer name or national identification is not blank.");
            return (ConsumerCleanupOutcome.Fixed, null);
        }
        catch (JsonException) { return (sent ? ConsumerCleanupOutcome.Unverified : ConsumerCleanupOutcome.NotSent, "Invalid Consumer JSON; cleanup could not be verified."); }
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static bool Blank(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
}
