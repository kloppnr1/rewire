using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimeSeries.Cli.Tests.Fixtures;

/// <summary>
/// Snapshots recorded Settl POST calls into normalized JSON suitable for golden-file comparison.
/// Replaces runner-injected non-deterministic values (TransactionId, RegistrationTime, Revision) with placeholders.
/// </summary>
internal static class PostSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly Regex TransactionIdGuid = new(
        @"""TransactionId"":\s*""[0-9a-fA-F-]{36}""",
        RegexOptions.Compiled);

    private static readonly Regex RegistrationTimeStamp = new(
        @"""RegistrationTime"":\s*""[^""]+""",
        RegexOptions.Compiled);

    private static readonly Regex RevisionStamp = new(
        @"""Revision"":\s*""[^""]+""",
        RegexOptions.Compiled);

    public static string From(
        RecordingTimeSeriesClient ts,
        RecordingMeteringPointClient mp)
    {
        var snapshot = new
        {
            TimeSeriesPosts = ts.PostCalls
                .Select(c => new { c.TsGuid, c.Payload })
                .ToList(),
            ExternallyInvoicedPeriodPosts = mp.AddEipCalls
                .Select(c => new { c.MpGuid, c.Request })
                .ToList(),
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        json = TransactionIdGuid.Replace(json, @"""TransactionId"": ""<guid>""");
        json = RegistrationTimeStamp.Replace(json, @"""RegistrationTime"": ""<timestamp>""");
        json = RevisionStamp.Replace(json, @"""Revision"": ""<timestamp>""");
        return json;
    }

    public static void AssertMatchesGolden(string actual, string goldenFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Goldens", goldenFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Golden file not found: {path}. Set TIMESERIES_UPDATE_GOLDENS=1 to write it.",
                path);

        if (Environment.GetEnvironmentVariable("TIMESERIES_UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var expected = File.ReadAllText(path);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();
}
