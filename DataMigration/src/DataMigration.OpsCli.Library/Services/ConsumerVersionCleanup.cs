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

public interface IConsumerVersionCleanup
{
    Task<(bool Success, string? Error)> BlankAsync(Guid id, DateTime start, CancellationToken cancellationToken);
}

/// <summary>Dated, minimal PATCH using the existing authenticated Settl transport.</summary>
public sealed class ConsumerVersionCleanup(HttpClient http, ISettlAuthService auth,
    IOptions<SettlApiOptions> options, ILogger<ConsumerVersionCleanup> logger)
    : SettlApiClientBase(http, auth, options, logger), IConsumerVersionCleanup
{
    public async Task<(bool Success, string? Error)> BlankAsync(Guid id, DateTime start, CancellationToken cancellationToken)
    {
        var utc = ConsumerDeliveryEvidence.Utc(start);
        var url = $"{Options.GetMarketBaseUrl(null)}/api/v4/meteringPoints/{id}?EffectiveDate={Uri.EscapeDataString(utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}";
        var (before, error) = await GetRawAsync(url, cancellationToken);
        if (before is null || error is not null) return (false, "Pre-write read failed: " + error);
        try
        {
            var point = JsonSerializer.Deserialize<MeteringPointV4Dto>(before, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (point?.Id != id || point.Consumer?.Start is not { } actual || ConsumerDeliveryEvidence.Utc(actual) != utc)
                return (false, "Pre-write read returned a different Consumer version.");
            if (FixConsumer2DuplicateRunner.FindDuplicateName(point) is null)
                return (false, "Consumer is no longer duplicated; no write made.");
            var payload = new JsonObject
            {
                ["TransactionId"] = Guid.NewGuid().ToString(),
                ["MeteringPointPatches"] = new JsonArray(new JsonObject
                {
                    ["Id"] = id.ToString(),
                    ["PatchTime"] = utc.ToString("O", CultureInfo.InvariantCulture),
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
            if (!success) return (false, writeError);
            var (after, verifyError) = await GetRawAsync(url, cancellationToken);
            if (after is null || verifyError is not null) return (false, "Post-write read failed: " + verifyError);
            using var document = JsonDocument.Parse(after);
            var root = document.RootElement;
            var consumer = Field(root, "consumer");
            if (!Guid.TryParse(Field(root, "id").ToString(), out var actualId) || actualId != id
                || !DateTimeOffset.TryParse(Field(consumer, "start").ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var actualStart) || actualStart != utc)
                return (false, "Post-write read returned a different Consumer version.");
            var party = Field(consumer, "secondConsumerParty");
            var name = Field(party, "name");
            if (!Blank(Field(name, "fullName")) || !Blank(Field(name, "firstName")) || !Blank(Field(name, "lastName"))
                || !Blank(Field(party, "nationalIdentificationNumber")))
                return (false, "Post-write verification: second Consumer name or national identification is not blank.");
            return (true, null);
        }
        catch (JsonException) { return (false, "Invalid Consumer JSON; cleanup could not be verified."); }
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static bool Blank(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
}
