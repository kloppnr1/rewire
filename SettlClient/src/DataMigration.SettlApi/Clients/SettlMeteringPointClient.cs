using System.Text.Json.Nodes;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public class SettlMeteringPointClient : SettlApiClientBase, ISettlMeteringPointClient
{
    private string? _deliveryCategory;
    private Dictionary<string, MeteringPointInfoDto>? _waterMpCache;

    public SettlMeteringPointClient(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger<SettlMeteringPointClient> logger)
        : base(httpClient, authService, options, logger)
    {
    }

    public void SetDeliveryCategory(string deliveryCategory)
    {
        _deliveryCategory = deliveryCategory;
    }

    public async Task<(bool Success, string? Error)> CreateAsync(
        CreateMeteringPointsRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> CreateV4Async(
        CreateMeteringPointsV4Request request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v4/meteringPoints";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints?MeteringPointIdentification={Uri.EscapeDataString(meteringPointIdentification)}";

        var (response, error) = await SendAsync<FindResponse<MeteringPointInfoDto>>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);

        if (error != null)
            return (null, error);

        if (response?.Items is { Count: > 0 })
            return (response.Items[0], null);

        return (null, null);
    }

    public async Task<(bool Success, string? Error)> CreateWaterAsync(
        CreateWaterMeteringPointRequest request, CancellationToken cancellationToken = default)
    {
        var url = Options.WaterMeteringPointsUrl;
        var result = await PostAsync(url, request, CamelCaseJsonOptions, cancellationToken);

        // Invalidate cache so next search picks up the new MP
        if (result.Success)
            _waterMpCache = null;

        return result;
    }

    public async Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default)
    {
        if (_waterMpCache == null)
        {
            // Settl water endpoint paginates (default page size 50). MeteringPointIdentification
            // query param is ignored — only Snapshot+Limit+Skip pagination filters. We must walk
            // every page to populate the cache, otherwise MPs on later pages are reported as
            // "not found" even when they exist.
            _waterMpCache = new Dictionary<string, MeteringPointInfoDto>(StringComparer.OrdinalIgnoreCase);
            var snapshot = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            const int requestedLimit = 200;
            var skip = 0;

            while (true)
            {
                var url = $"{Options.WaterMeteringPointsUrl}?CountryCode=DK&Snapshot={Uri.EscapeDataString(snapshot)}&Limit={requestedLimit}&Skip={skip}";
                var (response, error) = await SendAsync<FindResponse<MeteringPointInfoDto>>(
                    HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);

                if (error != null)
                    return (null, error);

                if (response?.Items == null || response.Items.Count == 0)
                    break;

                foreach (var mp in response.Items)
                {
                    if (mp.MeteringPointIdentification != null)
                        _waterMpCache[mp.MeteringPointIdentification] = mp;
                }

                if (response.Items.Count < requestedLimit)
                    break;
                skip += response.Items.Count;
            }

            Logger.LogInformation("Loaded {Count} water metering points into cache", _waterMpCache.Count);
        }

        return _waterMpCache.TryGetValue(meteringPointIdentification, out var found)
            ? (found, null)
            : (null, null);
    }

    public async Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        // Warm cache via SearchWaterByIdentificationAsync (noop if already warm).
        var (_, error) = await SearchWaterByIdentificationAsync("__warm__", cancellationToken);
        if (error != null)
            return (null, error);

        var found = _waterMpCache!.Values.FirstOrDefault(mp => mp.Id == meteringPointId);
        return (found, null);
    }

    public async Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(
        CancellationToken cancellationToken = default)
    {
        // Warm the cache; noop if already populated from a prior search call.
        var (_, error) = await SearchWaterByIdentificationAsync("__warm__", cancellationToken);
        if (error != null)
            return ([], error);
        return (_waterMpCache!.Values.ToList(), null);
    }

    public async Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(
        Guid billingMeteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints/{billingMeteringPointId}";
        if (effectiveDate.HasValue)
            url += $"?EffectiveDate={effectiveDate.Value:yyyy-MM-ddTHH:mm:ssZ}";
        return await SendAsync<BillingMeteringPointCurrentDto>(HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> UpdateBillingAsync(
        Guid billingMeteringPointId, UpdateBillingMeteringPointRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints/{billingMeteringPointId}/meteringPoint";
        return await PutAsync(url, request, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}";
        if (effectiveDate.HasValue)
            url += $"?EffectiveDate={effectiveDate.Value:yyyy-MM-ddTHH:mm:ssZ}";
        return await SendAsync<MeteringPointCurrentDto>(HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}/deliveryStatuses";
        return await SendAsync<GetDeliveryStatusesResponse>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}/deliveryStatuses";
        return GetRawAsync(url, cancellationToken);
    }

    public async Task<(string? Json, string? Error)> GetByIdRawAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}";
        var (response, error) = await SendAsync<System.Text.Json.JsonDocument>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
        if (error != null)
            return (null, error);
        return (System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(
        Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}";
        dto.Id = meteringPointId;
        var request = new UpdateMeteringPointWithHistoricalDataRequest { MeteringPoint = dto };
        return await PutAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints?MeteringPointIdentification={Uri.EscapeDataString(meteringPointIdentification)}";

        var (response, error) = await SendAsync<FindResponse<BillingMeteringPointInfoDto>>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);

        if (error != null)
            return (null, error);

        if (response?.Items is { Count: > 0 })
            return (response.Items[0], null);

        return (null, null);
    }

    public async Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(
        Guid billingMeteringPointId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints/{billingMeteringPointId}/history";
        return await SendAsync<BillingMeteringPointHistoryResponse>(HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}/history";
        return await SendAsync<MeteringPointV3HistoryResponse>(HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v4/meteringPoints/{meteringPointId}";
        if (effectiveDate.HasValue)
            url += $"?EffectiveDate={effectiveDate.Value:yyyy-MM-ddTHH:mm:ssZ}";
        return await SendAsync<MeteringPointV4Dto>(HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(MeteringPointConsumerNationalIdsDto? Ids, string? Error)> GetConsumerNationalIdsAsync(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v4/meteringPoints/{meteringPointId}/nationalIdentificationNumber";
        if (effectiveDate.HasValue)
            url += $"?EffectiveDate={effectiveDate.Value:yyyy-MM-ddTHH:mm:ssZ}";
        return await SendAsync<MeteringPointConsumerNationalIdsDto>(HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);
    }

    public async Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"{Options.MarketTenantBaseUrl}/v1/market-balance-areas?Limit=100";
        var (response, error) = await SendAsync<FindResponse<MarketBalanceAreaInfoDto>>(
            HttpMethod.Get, url, null, JsonOptions, cancellationToken);
        return (response?.Items, error);
    }

    public async Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(
        int limit = 100, int skip = 0, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints?CountryCode=DK&Limit={limit}&Skip={skip}&DoCount=true";

        var (response, error) = await SendAsync<FindResponse<BillingMeteringPointInfoDto>>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);

        if (error != null)
            return ([], 0, error);

        var totalCount = response?.CountResult?.Count ?? 0;
        return (response?.Items ?? [], totalCount, null);
    }

    public async Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(
        int limit = 100, int skip = 0, bool doCount = false, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints?Limit={limit}&Skip={skip}&DoCount={(doCount ? "true" : "false")}";

        var (response, error) = await SendAsync<FindResponse<MeteringPointInfoDto>>(
            HttpMethod.Get, url, null, CamelCaseJsonOptions, cancellationToken);

        if (error != null)
            return ([], 0, error);

        var totalCount = response?.CountResult?.Count ?? 0;
        return (response?.Items ?? [], totalCount, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(
        Guid meteringPointId, double annualVolumeKwh, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}/annualConsumptions";
        var request = new UpdateAnnualConsumptionsRequest
        {
            AnnualConsumptions = new QuantityPeriodItem
            {
                Quantity = new QuantityDto { Unit = "kWh", Kind = "Na", Value = annualVolumeKwh }
            }
        };
        return await PutAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> AddExemptionsAsync(
        Guid billingMeteringPointId, AddMeteringPointExemptionsRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{Options.BillingBaseUrl}/api/v1/meteringPoints/{billingMeteringPointId}/exemptions";
        return await PostAsync(url, request, JsonOptions, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(
        Guid meteringPointId, double annualVolumeKwh, DateOnly effectiveStart, CancellationToken cancellationToken = default)
    {
        // Full-object read-modify-write: GET the whole master, change ONLY the EstimatedAnnualVolume
        // value + the MeterCharacteristics.Start (so it becomes the current period), echo everything
        // else back verbatim. The v3 master PUT is a full replace — a partial body would drop the
        // other sections — so we round-trip the raw JSON Settl returned (mirrors the admin-UI flow).
        var url = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}";

        var (raw, getErr) = await GetRawAsync(url, cancellationToken);
        if (raw == null)
            return (false, $"GET master: {getErr}");

        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (System.Text.Json.JsonException ex) { return (false, $"parse master: {ex.Message}"); }

        if (root is not JsonObject mp)
            return (false, "master response is not a JSON object");
        if (mp["MeterCharacteristics"] is not JsonObject mc)
            return (false, "master has no MeterCharacteristics to update");

        // Overwrite the value in place (preserve Unit/Kind/IsEmpty) or seed a fresh quantity.
        if (mc["EstimatedAnnualVolume"] is JsonObject eav)
            eav["Value"] = annualVolumeKwh;
        else
            mc["EstimatedAnnualVolume"] = new JsonObject { ["Unit"] = "kWh", ["Kind"] = "Na", ["Value"] = annualVolumeKwh };

        // Periodised field: a new Start splits the history; Settl ends the prior entry automatically.
        // Settl validates these period boundaries as "date only" in DANISH time — UTC midnight
        // (…T00:00:00Z) is 02:00 Danish in summer and is rejected with "Start must be date only".
        // Use the same Danish-midnight-UTC encoding the DTO converters use (today ≤ today ⇒ effective now).
        mc["Start"] = DateOnlyUtcConverter.FormatAsDanishMidnightUtc(effectiveStart);

        var body = new JsonObject
        {
            ["MeteringPoint"] = mp,
            ["TransactionId"] = Guid.NewGuid().ToString(),
        };
        return await PutRawAsync(url, body.ToJsonString(), cancellationToken);
    }

    public async Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        // The v3 master document at /api/v3/meteringPoints/{id} only has a GET in APIM — there is
        // no PUT/PATCH on that route, and the MP's own Commands[] carries no mutation for consumer
        // fields either (confirmed live, featuretest, 2026-09-09). The only mutation path for these
        // two leaves is the bulk endpoint PATCH /api/v3/meteringPoints (no {id} — array-of-patches
        // body, MeteringPointPatchDto). We still GET the master first, but only to read the two
        // things we need: whether the names actually match, and the Consumer's own Start, which we
        // round-trip verbatim as PatchTime so the PATCH updates the existing historical entry in
        // place instead of opening a new one (Start match on the entry = in-place update, same rule
        // as the billing PUT; confirmed against featuretest 2026-09-09).
        var getUrl = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints/{meteringPointId}";

        var (raw, getErr) = await GetRawAsync(getUrl, cancellationToken);
        if (raw == null)
            return (false, $"GET master: {getErr}");

        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (System.Text.Json.JsonException ex) { return (false, $"parse master: {ex.Message}"); }

        if (root is not JsonObject mp)
            return (false, "master response is not a JSON object");
        if (mp["Consumer"] is not JsonObject consumer)
            return (false, "master has no Consumer section to update");

        var first = consumer["FirstConsumerParty"] as JsonObject;
        var second = consumer["SecondConsumerParty"] as JsonObject;
        if (first == null || second == null)
            return (true, null); // no second party — nothing to blank, not an error

        if (!NamesDeepEqual(first["Name"] as JsonObject, second["Name"] as JsonObject))
            return (true, null); // names differ — no-op, method is safe to call unconditionally

        var start = consumer["Start"];
        if (start == null)
            return (false, "Consumer has no Start — cannot anchor the patch to the existing entry");

        // The two leaves take opposite blanking sentinels on this endpoint (confirmed against
        // featuretest 2026-09-09, live probe on MP b92c81d2-d202-4604-8d81-341174871ec0): Name's
        // subfields are cleared by null, but NationalIdentificationNumber ignores null (treated as
        // "leave unchanged") and is only cleared by an empty string.
        var patchUrl = $"{Options.GetMarketBaseUrl(_deliveryCategory)}/api/v3/meteringPoints";
        var body = new JsonObject
        {
            ["TransactionId"] = Guid.NewGuid().ToString(),
            ["MarketUpdate"] = false,
            ["MeteringPointPatches"] = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = meteringPointId.ToString(),
                    ["PatchTime"] = start.DeepClone(),
                    ["MarketUpdate"] = false,
                    ["ConsumerPatch"] = new JsonObject
                    {
                        ["SecondConsumerParty"] = new JsonObject
                        {
                            ["Name"] = new JsonObject
                            {
                                ["FullName"] = null,
                                ["FirstName"] = null,
                                ["LastName"] = null,
                            },
                            ["NationalIdentificationNumber"] = "",
                        }
                    }
                }
            }
        };

        return await PatchRawAsync(patchUrl, body.ToJsonString(), cancellationToken);
    }

    private static bool NamesDeepEqual(JsonObject? a, JsonObject? b)
    {
        // Both null/absent counts as equal (nothing to dedupe against). Compares every
        // ConsumerNameDto leaf — FullName, FirstName, LastName — not just one field.
        if (a == null && b == null)
            return true;
        if (a == null || b == null)
            return false;

        return NameField(a) == NameField(b);

        static (string? FullName, string? FirstName, string? LastName) NameField(JsonObject o) => (
            (string?)o["FullName"],
            (string?)o["FirstName"],
            (string?)o["LastName"]);
    }

    public async Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(
        CancellationToken cancellationToken = default)
    {
        // List endpoint returns only Id+Uri — need detail endpoint for MeteringGridAreaIdentification
        var listUrl = $"{Options.MarketTenantBaseUrl}/v1/metering-grid-areas?Limit=100";
        var (listResponse, listError) = await SendAsync<FindResponse<MeteringGridAreaInfoDto>>(
            HttpMethod.Get, listUrl, null, JsonOptions, cancellationToken);

        if (listResponse?.Items == null)
            return (null, listError);

        // Fetch detail for each to get MeteringGridAreaIdentification
        var result = new List<MeteringGridAreaInfoDto>();
        foreach (var item in listResponse.Items)
        {
            var detailUrl = $"{Options.MarketTenantBaseUrl}/v1/metering-grid-areas/{item.Id}";
            var (detail, _) = await SendAsync<MeteringGridAreaInfoDto>(
                HttpMethod.Get, detailUrl, null, JsonOptions, cancellationToken);
            if (detail != null)
                result.Add(detail);
        }

        return (result, null);
    }
}
