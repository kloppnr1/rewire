using DataMigration.Core;
using DataMigration.Domain.Engros;
using DataMigration.Domain.Xellent;

namespace DataMigration.OpsCli.Services;

public interface IXellentDomainLoader
{
    /// <param name="accounts">
    /// Konti der skal indlæses, eller <c>null</c> for hele dataområdet i mapperens
    /// leveringskategori. Whole-area-formen bruges af <c>--all-active</c>: at enumerere
    /// ~45.000 konti og sende dem tilbage som query-parametre er mere end SQL Server vil
    /// planlægge, og mapperens egne prædikater udtrykker allerede samme scope.
    /// </param>
    Task<XellentLoadResult> LoadAsync(
        string[]? accounts,
        MapperOptions options,
        string dataAreaId,
        WarningCollector warnings,
        CancellationToken ct = default);

    Task<SettlementBasis?> LoadSettlementBasisAsync(
        string meteringPointId,
        DateTime periodStart,
        DateTime periodEnd,
        MapperOptions options,
        string dataAreaId,
        WarningCollector warnings,
        CancellationToken ct = default);

    /// <param name="disableMerge">
    /// Skip the CVR+fingerprint merge: every account resolves to itself. Off by default, so an
    /// existing caller gets exactly the resolution it always got. Set it for an all-active
    /// population, where one Xellent account became one Settl customer and a merge would name
    /// representatives Settl does not have. Names still come back; the fingerprints do not, since
    /// nothing computes them when they form no edges.
    /// </param>
    Task<AccountRepresentativeResolution> ResolveRepresentativesAsync(
        string[] accounts,
        string dataAreaId,
        CancellationToken ct = default,
        bool disableMerge = false);

    Task<MeteringPointXellentData?> LoadMeteringPointDataAsync(
        string identification,
        string dataAreaId,
        CancellationToken ct = default);

    /// <summary>Returns contractNum → custaccount for the given contract numbers.</summary>
    Task<Dictionary<string, string>> LookupContractAccountsAsync(
        string[] contractNums,
        string dataAreaId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns (contractNum, amrCode, startDate) → DELIVERYGROUPNUM_RATECODE
    /// for contract parts that have a non-empty rate code.
    /// </summary>
    Task<IReadOnlyDictionary<(string ContractNum, string AmrCode, DateTime StartDate), string>> LookupDeliveryGroupRateCodesAsync(
        string[] contractNums,
        string dataAreaId,
        CancellationToken ct = default);

    /// <summary>Returns contractNum → Todate (contract end date) from ExuContractTable.</summary>
    Task<Dictionary<string, DateTime>> LookupContractEndDatesAsync(
        string[] contractNums,
        string dataAreaId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns (contractNum, amrCode, productNum, startDate) → CONTRACTPARTENDDATE from EXU_CONTRACTPARTLOG.
    /// For each (CONTRACTNUM, AMRCODE, PRODUCTNUM, CONTRACTPARTSTARTDATE) the most recent log entry with
    /// LOGDATETIME ≤ referenceDate is selected. Use instead of EXU_CONTRACTPARTTABLE.ENDDATE
    /// when DataHub may have overwritten the table after migration.
    /// </summary>
    Task<IReadOnlyDictionary<(string ContractNum, string AmrCode, string ProductNum, DateTime StartDate), DateTime>> LookupContractPartLogEndDatesAsync(
        string[] contractNums,
        string dataAreaId,
        CancellationToken ct = default);

    Task VerifyConnectionAsync(CancellationToken ct = default);
}
