using DataMigration.SettlApi.Models;

namespace TimeSeries.Core.Models;

public class TimeSeriesFileDto
{
    public Guid TransactionId { get; set; }
    public DateTimeOffset RegistrationTime { get; set; }
    public List<SettlTimeSeriesPointDto> Points { get; set; } = new();
    public DateTimeOffset Revision { get; set; }
    public bool? InvoiceRelevant { get; set; }
    public bool AccountForQuantityQuality { get; set; } = true;
    public TimeResolutionDto? TimeResolution { get; set; }

    public SettlTimeSeriesRequestDto ToApiRequest()
    {
        return new SettlTimeSeriesRequestDto
        {
            TransactionId = TransactionId,
            RegistrationTime = RegistrationTime,
            Points = Points,
            Revision = Revision,
            InvoiceRelevant = InvoiceRelevant,
            AccountForQuantityQuality = AccountForQuantityQuality
        };
    }
}
