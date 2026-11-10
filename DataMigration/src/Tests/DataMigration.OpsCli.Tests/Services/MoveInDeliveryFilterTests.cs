using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class MoveInDeliveryFilterTests
{
    [Fact]
    public async Task MoveIn_is_excluded_but_change_of_supplier_is_kept_at_selected_Danish_date()
    {
        var cos = new DateOnly(2026, 5, 19);
        var utcStart = new DateTimeOffset(2026, 5, 18, 22, 0, 0, TimeSpan.Zero);
        var moveIn = Mp("571313115400163609", cos, utcStart);
        var migrated = Mp("571313115200285419", cos, utcStart);
        var byId = new Dictionary<Guid, DeliveryStartReason>
        {
            [moveIn.Id] = DeliveryStartReason.MoveIn,
            [migrated.Id] = DeliveryStartReason.ChangeOfSupplier
        };

        var result = await MoveInDeliveryFilter.FilterAsync([moveIn, migrated],
            (id, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((
                Response(utcStart, byId[id]), null)), CancellationToken.None);

        Assert.Equal([migrated.Id], result.Kept.Select(m => m.Id));
        Assert.Equal(0, result.Unresolved);
        var audit = Assert.Single(result.Audit);
        Assert.Equal(moveIn.Identification, audit.MeteringPoint);
        Assert.Equal(cos, audit.SelectedCos);
        Assert.Equal("MoveIn", audit.Disposition);
    }

    [Fact]
    public async Task Missing_reason_is_reported_and_metering_point_is_excluded()
    {
        var mp = Mp("571313115400163609", new DateOnly(2026, 5, 19),
            new DateTimeOffset(2026, 5, 18, 22, 0, 0, TimeSpan.Zero));
        var result = await MoveInDeliveryFilter.FilterAsync([mp],
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((
                new GetDeliveryStatusesResponse { DeliveryStatuses = [] }, null)), CancellationToken.None);

        Assert.Empty(result.Kept);
        Assert.Equal(1, result.Unresolved);
        Assert.Equal("NoFirstDeliveryStart", Assert.Single(result.Audit).Disposition);
    }

    [Fact]
    public async Task Failed_status_lookup_is_audited_and_excluded_with_move_in()
    {
        var cos = new DateOnly(2026, 5, 19);
        var start = new DateTimeOffset(2026, 5, 18, 22, 0, 0, TimeSpan.Zero);
        var unverified = Mp("UNVERIFIED", cos, start);
        var moveIn = Mp("MOVEIN", cos, start);
        var result = await MoveInDeliveryFilter.FilterAsync([unverified, moveIn],
            (id, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>(id == unverified.Id
                ? (null, "temporary API failure")
                : (Response(start, DeliveryStartReason.MoveIn), null)), CancellationToken.None);

        Assert.Empty(result.Kept);
        Assert.Equal(1, result.Unresolved);
        Assert.Contains(result.Audit, a => a.MeteringPoint == "UNVERIFIED"
            && a.Disposition == "DeliveryStatusLookupFailed");
        Assert.Contains(result.Audit, a => a.MeteringPoint == "MOVEIN"
            && a.Disposition == "MoveIn");
    }

    [Fact]
    public async Task Not_in_delivery_point_requires_real_first_delivery_history()
    {
        var mp = new SettlMpWithCos("MP", Guid.NewGuid(), new DateOnly(2026, 6, 1),
            new MeteringPointInfoDto { InDelivery = false });
        var calls = 0;
        var result = await MoveInDeliveryFilter.FilterAsync([mp],
            (_, _) => { calls++; return Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((
                new GetDeliveryStatusesResponse { DeliveryStatuses = [] }, null)); }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Empty(result.Kept);
        Assert.Equal("NoFirstDeliveryStart", Assert.Single(result.Audit).Disposition);
    }

    [Fact]
    public async Task First_delivery_decides_even_when_later_delivery_changed_supplier()
    {
        var mp = Mp("MP", new DateOnly(2026, 8, 1),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var response = new GetDeliveryStatusesResponse { DeliveryStatuses =
        [
            new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto
                { Start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero) },
                StartReason = DeliveryStartReason.ChangeOfSupplier },
            new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto
                { Start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                StartReason = DeliveryStartReason.MoveIn }
        ] };
        var result = await MoveInDeliveryFilter.FilterAsync([mp],
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((response, null)),
            CancellationToken.None);
        Assert.Empty(result.Kept);
        Assert.Equal(new DateOnly(2025, 1, 1), Assert.Single(result.Audit).SelectedCos);
    }

    [Fact]
    public async Task Opt_in_accepts_only_dated_unknown_first_start_and_keeps_its_date()
    {
        var first = new DateTimeOffset(2025, 10, 2, 22, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var unknown = Mp("UNKNOWN", new DateOnly(2026, 1, 1), later);
        var moveIn = Mp("MOVEIN", new DateOnly(2025, 10, 3), first);
        var na = Mp("NA", new DateOnly(2025, 10, 3), first);
        var response = new GetDeliveryStatusesResponse { DeliveryStatuses =
        [
            new DeliveryStatusDto { InDelivery = true,
                Period = new TimePeriodDto { Start = first },
                StartReason = DeliveryStartReason.Unknown },
            new DeliveryStatusDto { InDelivery = true,
                Period = new TimePeriodDto { Start = later },
                StartReason = DeliveryStartReason.ChangeOfSupplier }
        ] };
        var withoutFlag = await MoveInDeliveryFilter.FilterAsync([unknown],
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((response, null)),
            CancellationToken.None);
        Assert.Empty(withoutFlag.Kept);
        Assert.Equal("UnknownDeliveryStartReason", Assert.Single(withoutFlag.Audit).Disposition);

        var withFlag = await MoveInDeliveryFilter.FilterAsync([unknown, moveIn, na],
            (id, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((
                id == unknown.Id ? response : Response(first,
                    id == moveIn.Id ? DeliveryStartReason.MoveIn : DeliveryStartReason.Na), null)),
            CancellationToken.None, includeUnknownFirstStarts: true);
        Assert.Equal(new DateOnly(2025, 10, 3), Assert.Single(withFlag.Kept).Cos);
        Assert.Equal(unknown.Id, withFlag.Kept[0].Id);
        Assert.Equal(1, withFlag.IncludedUnknown);
        Assert.Contains(withFlag.Audit, a => a.MeteringPoint == "UNKNOWN"
            && a.Disposition == "IncludedUnknownDeliveryStartReason");
        Assert.Contains(withFlag.Audit, a => a.Disposition == "MoveIn");
        Assert.Contains(withFlag.Audit, a => a.Disposition == "OtherDeliveryStartReason");
    }

    [Fact]
    public async Task First_supplier_change_date_replaces_current_delivery_date()
    {
        var mp = Mp("MP", new DateOnly(2026, 8, 1),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var response = new GetDeliveryStatusesResponse { DeliveryStatuses =
        [
            new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto
                { Start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero) },
                StartReason = DeliveryStartReason.MoveIn },
            new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto
                { Start = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero) },
                StartReason = DeliveryStartReason.ChangeOfSupplier }
        ] };
        var result = await MoveInDeliveryFilter.FilterAsync([mp],
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((response, null)),
            CancellationToken.None);
        Assert.Equal(new DateOnly(2025, 5, 1), Assert.Single(result.Kept).Cos);
    }

    private static SettlMpWithCos Mp(string id, DateOnly cos, DateTimeOffset start) =>
        new(id, Guid.NewGuid(), cos, new MeteringPointInfoDto
        {
            InDelivery = true,
            CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = start }
        });

    private static GetDeliveryStatusesResponse Response(DateTimeOffset start, DeliveryStartReason reason) =>
        new() { DeliveryStatuses = [new DeliveryStatusDto
        {
            InDelivery = true,
            Period = new TimePeriodDto { Start = start },
            StartReason = reason
        }] };
}
