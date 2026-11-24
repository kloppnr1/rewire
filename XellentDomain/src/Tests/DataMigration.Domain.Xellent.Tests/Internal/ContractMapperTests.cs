using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// Verifies installation-path contract-part inclusion logic:
/// water/heat parts that START after ReferenceDate must be excluded;
/// parts active on ReferenceDate must be included.
/// El-path has no start-date cap (separate code path, not tested here).
/// </summary>
public class ContractMapperTests
{
    private static readonly DateTime RefDate = new(2026, 3, 31);  // COS 2026-04-01 → ReferenceDate 2026-03-31
    private static readonly DateTime Sentinel = new(1900, 1, 1);  // EndDateNull — "open" contract

    // ── Vand-alm: active on ReferenceDate (starts long ago, ends exactly on RefDate) ──────────

    [Fact]
    public void VandAlm_active_on_referenceDate_is_included()
    {
        var included = ContractMapper.InstallationPartIncluded(
            startdate: new DateTime(2003, 11, 1),
            enddate: RefDate,
            today: RefDate,
            includeEndedCutoff: null);

        Assert.True(included);
    }

    // ── VandSettl: starts the day AFTER ReferenceDate — must be excluded ─────────────────────

    [Fact]
    public void VandSettl_starts_after_referenceDate_is_excluded()
    {
        var included = ContractMapper.InstallationPartIncluded(
            startdate: new DateTime(2026, 4, 1),
            enddate: Sentinel,
            today: RefDate,
            includeEndedCutoff: null);

        Assert.False(included);
    }

    // ── Open (no end date) part active on ReferenceDate ──────────────────────────────────────

    [Fact]
    public void Open_part_active_on_referenceDate_is_included()
    {
        var included = ContractMapper.InstallationPartIncluded(
            startdate: new DateTime(2020, 1, 1),
            enddate: Sentinel,
            today: RefDate,
            includeEndedCutoff: null);

        Assert.True(included);
    }

    // ── Part ended before ReferenceDate but within includeEndedCutoff window ─────────────────

    [Fact]
    public void Part_ended_before_today_but_within_cutoff_is_included()
    {
        var included = ContractMapper.InstallationPartIncluded(
            startdate: new DateTime(2020, 1, 1),
            enddate: new DateTime(2026, 3, 15),
            today: RefDate,
            includeEndedCutoff: new DateTime(2026, 1, 1));

        Assert.True(included);
    }

    // ── Part ended before cutoff is excluded ─────────────────────────────────────────────────

    [Fact]
    public void Part_ended_before_cutoff_is_excluded()
    {
        var included = ContractMapper.InstallationPartIncluded(
            startdate: new DateTime(2020, 1, 1),
            enddate: new DateTime(2025, 12, 31),
            today: RefDate,
            includeEndedCutoff: new DateTime(2026, 1, 1));

        Assert.False(included);
    }
}
