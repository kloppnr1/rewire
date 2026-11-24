using System.Text.Json;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Tests;

public class DateOnlyUtcConverterTests
{
    [Theory]
    [InlineData("2026-09-01T22:00:00Z", "2026-09-02")]
    [InlineData("2026-09-01T22:00:00+00:00", "2026-09-02")]
    [InlineData("2026-09-01T23:00:00-02:00", "2026-09-02")]
    [InlineData("2026-09-02T00:00:00+03:00", "2026-09-01")]
    [InlineData("2026-01-01T23:00:00Z", "2026-01-02")]
    [InlineData("2026-01-01T22:00:00Z", "2026-01-01")]
    [InlineData("2026-03-28T23:00:00Z", "2026-03-29")]
    [InlineData("2026-03-29T22:00:00Z", "2026-03-30")]
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25")]
    [InlineData("2026-10-25T23:00:00Z", "2026-10-26")]
    [InlineData("2026-09-01", "2026-09-01")]
    [InlineData("2026-09-01T22:00:00", "2026-09-01")]
    [InlineData("9999-12-31T23:59:58.999Z", "9999-12-31")]
    [InlineData("9999-12-31T23:59:58.999+00:00", "9999-12-31")]
    public void Zoned_timestamps_use_Danish_date_and_unspecified_dates_are_preserved(string wire, string expected)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DateOnlyUtcConverter());

        var date = JsonSerializer.Deserialize<DateOnly>(JsonSerializer.Serialize(wire), options);

        Assert.Equal(DateOnly.Parse(expected), date);
    }

    [Fact]
    public void Historical_payer_link_preserves_single_day_delivery_period()
    {
        var link = JsonSerializer.Deserialize<PayerMeteringPointDto>(
            """{"Start":"2026-08-31T22:00:00Z","End":"2026-09-01T22:00:00Z"}""",
            SettlApiClientBase.JsonOptions)!;

        Assert.Equal(new DateOnly(2026, 9, 1), link.Start);
        Assert.Equal(new DateOnly(2026, 9, 2), link.End);
    }

    [Fact]
    public void New_payer_link_preserves_open_end_sentinel()
    {
        var link = JsonSerializer.Deserialize<PayerMeteringPointDto>(
            """{"Start":"2026-09-01T22:00:00Z","End":"9999-12-31T23:59:58.999Z"}""",
            SettlApiClientBase.JsonOptions)!;

        Assert.Equal(new DateOnly(2026, 9, 2), link.Start);
        Assert.Equal(DateOnly.MaxValue, link.End);
    }

    [Theory]
    [InlineData("2026-09-01T22:00:00Z", "2026-09-01")]
    [InlineData("2026-09-01T23:00:00-02:00", "2026-09-01")]
    [InlineData("2026-09-02", "2026-09-01")]
    [InlineData("9999-12-31T23:59:58.999Z", "9999-12-31")]
    [InlineData("9999-12-31", "9999-12-30")]
    public void Exclusive_contract_end_subtracts_one_day_after_Danish_conversion(string wire, string expected)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NullableDateOnlyPlusOneConverter());

        var date = JsonSerializer.Deserialize<DateOnly?>(JsonSerializer.Serialize(wire), options);

        Assert.Equal(DateOnly.Parse(expected), date);
    }

    [Theory]
    [InlineData("2026-09-02", "2026-09-01T22:00:00Z")]
    [InlineData("2026-01-02", "2026-01-01T23:00:00Z")]
    [InlineData("9999-12-31", "9999-12-31T23:59:58.999Z")]
    public void Writing_Danish_midnight_is_unchanged_and_round_trips(string dateText, string wire)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DateOnlyUtcConverter());
        var date = DateOnly.Parse(dateText);

        var json = JsonSerializer.Serialize(date, options);

        Assert.Equal(JsonSerializer.Serialize(wire), json);
        Assert.Equal(date, JsonSerializer.Deserialize<DateOnly>(json, options));
    }

    [Theory]
    [InlineData("9999-12-31T23:59:58.999Z")]
    [InlineData("9999-12-31T23:59:58.999+00:00")]
    [InlineData("9999-12-31T23:59:58.999+01:00")]
    public void Nullable_converters_preserve_zoned_open_end_without_local_time_overflow(string wire)
    {
        foreach (var converter in new System.Text.Json.Serialization.JsonConverter[]
                 { new NullableDateOnlyUtcConverter(), new NullableDateOnlyPlusOneConverter() })
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(converter);

            var date = JsonSerializer.Deserialize<DateOnly?>(JsonSerializer.Serialize(wire), options);

            Assert.Equal(DateOnly.MaxValue, date);
            Assert.Equal("\"9999-12-31T23:59:58.999Z\"", JsonSerializer.Serialize(date, options));
        }
    }

    [Fact]
    public void Nullable_converters_preserve_null()
    {
        foreach (var converter in new System.Text.Json.Serialization.JsonConverter[]
                 { new NullableDateOnlyUtcConverter(), new NullableDateOnlyPlusOneConverter() })
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(converter);
            Assert.Null(JsonSerializer.Deserialize<DateOnly?>("null", options));
            Assert.Equal("null", JsonSerializer.Serialize<DateOnly?>(null, options));
        }
    }
}
