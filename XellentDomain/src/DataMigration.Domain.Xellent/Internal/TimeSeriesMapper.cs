using DataMigration.Domain.Common;
using DataMigration.Domain.TimeSeries;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Maps Xellent <c>EMS_TIMESERIES</c> + <c>EMS_TIMESERIESVALUES</c> rows into a domain
/// <see cref="MeteringPointTimeSeries"/> for a single metering point in a date range.
/// </summary>
internal sealed class TimeSeriesMapper
{
    private static readonly DateTime VersionToExclSentinel = new(2154, 12, 31, 23, 59, 59);

    private readonly XellentDbContext _context;
    private readonly string _dataAreaId;

    public TimeSeriesMapper(XellentDbContext context, string dataAreaId)
    {
        _context = context;
        _dataAreaId = dataAreaId;
    }

    public async Task<MeteringPointTimeSeries?> LoadAsync(
        string meteringPointId,
        string deliveryCategory,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var header = await _context.EmsTimeseries
            .Where(t => t.Dataareaid == _dataAreaId
                && t.Meteringpoint == meteringPointId
                && t.Deliverycategory == deliveryCategory)
            .Select(t => new { t.Recid, t.Quantitycodevalue, t.Timeresolution })
            .FirstOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        var values = await _context.EmsTimeseriesValues
            .Where(v => v.Dataareaid == _dataAreaId
                && v.Timeseriesrefrecid == header.Recid
                && v.Versiontoexcl == VersionToExclSentinel
                && v.Timeofvalue >= fromUtc
                && v.Timeofvalue <= toUtc)
            .OrderBy(v => v.Timeofvalue)
            .Select(v => new TimeSeriesPoint(
                v.Timeofvalue,
                v.Value,
                (QualityOfValue)v.Qualityofvalue))
            .ToListAsync(cancellationToken);

        return new MeteringPointTimeSeries(
            meteringPointId,
            (TimeResolution)header.Timeresolution,
            header.Quantitycodevalue,
            new Period(DateOnly.FromDateTime(fromUtc), DateOnly.FromDateTime(toUtc)),
            values);
    }
}
