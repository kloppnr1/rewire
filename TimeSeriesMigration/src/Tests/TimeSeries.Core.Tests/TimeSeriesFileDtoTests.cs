using DataMigration.SettlApi.Models;
using TimeSeries.Core.Models;

namespace TimeSeries.Core.Tests;

public class TimeSeriesFileDtoTests
{
    [Fact]
    public void ToApiRequest_CopiesScalarFields()
    {
        var txId = Guid.NewGuid();
        var registration = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var revision = new DateTimeOffset(2024, 6, 2, 9, 30, 0, TimeSpan.Zero);

        var file = new TimeSeriesFileDto
        {
            TransactionId = txId,
            RegistrationTime = registration,
            Revision = revision,
            AccountForQuantityQuality = false
        };

        var request = file.ToApiRequest();

        Assert.Equal(txId, request.TransactionId);
        Assert.Equal(registration, request.RegistrationTime);
        Assert.Equal(revision, request.Revision);
        Assert.Null(request.InvoiceRelevant);
        Assert.False(request.AccountForQuantityQuality);
    }

    [Fact]
    public void ToApiRequest_CopiesPoints()
    {
        var points = new List<SettlTimeSeriesPointDto>
        {
            new() { Time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Quantity = 1.23m, Quality = "Measured" },
            new() { Time = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc), Quantity = 4.56m, Quality = "Estimated" }
        };

        var file = new TimeSeriesFileDto { Points = points };

        var request = file.ToApiRequest();

        Assert.Same(points, request.Points);
        Assert.Equal(2, request.Points.Count);
        Assert.Equal(1.23m, request.Points[0].Quantity);
        Assert.Equal("Estimated", request.Points[1].Quality);
    }

    [Fact]
    public void ToApiRequest_DefaultInvoiceRelevanceIsNull()
    {
        var file = new TimeSeriesFileDto();

        var request = file.ToApiRequest();

        Assert.Null(request.InvoiceRelevant);
        Assert.True(request.AccountForQuantityQuality);
    }
}
