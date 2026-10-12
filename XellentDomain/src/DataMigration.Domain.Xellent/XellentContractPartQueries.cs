using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Identifies one contract part in <c>EXU_CONTRACTPARTLOG</c>. The log carries seven of the ten
/// key columns of <c>EXU_CONTRACTPARTTABLE</c> — <c>CONTRACTPARTSUBREFERENCETYPE</c> and
/// <c>CONTRACTPARTSUBREFERENCEID</c> are not logged — so the key is theoretically incomplete.
/// Joined to the live table for the 2026-08-31 batch window it produced no multi-match at all,
/// which is why it is usable here; it is not a general-purpose part identity.
/// </summary>
public sealed record ContractPartLogKey(
    string ContractNum,
    string InstAgreeNum,
    string DeliveryCategory,
    string CompanyId,
    string ProductNum,
    string AmrCode,
    DateTime PartStartDate);

/// <summary>One logged change, carrying the part state <i>before</i> the change.</summary>
/// <param name="Key">The part the change applied to.</param>
/// <param name="LoggedStartDate">Start date as it was before the change (same as the key's).</param>
/// <param name="LoggedEndDate">End date as it was before the change — the value to restore.</param>
/// <param name="ChangeType">Raw <c>ERRORMSG</c>, trimmed. The Danish change-type text.</param>
/// <param name="DeliveryGroupRateCode">Raw <c>DELIVERYGROUPNUM_RATECODE</c>, trimmed.</param>
public sealed record ContractPartLogChange(
    ContractPartLogKey Key,
    DateTime LoggedStartDate,
    DateTime LoggedEndDate,
    string ChangeType,
    string? DeliveryGroupRateCode);

/// <summary>
/// What <see cref="XellentContractPartQueries.LookupContractPartLogChangesInWindowAsync"/> found in a
/// time window: the changes that may be acted on, plus the keys that also have log rows outside the
/// window. The second list is context, not a veto — a part with a history before the window is still
/// restored, but a caller can say so in the run log.
/// </summary>
public sealed record ContractPartLogChangeSet(
    IReadOnlyList<ContractPartLogChange> Changes,
    IReadOnlySet<ContractPartLogKey> KeysAlsoChangedOutsideWindow)
{
    public static ContractPartLogChangeSet Empty { get; } =
        new([], new HashSet<ContractPartLogKey>());
}

public static class XellentContractPartQueries
{
    /// <summary>
    /// Returns (contractNum, amrCode, startDate) → DELIVERYGROUPNUM_RATECODE
    /// for contract parts that have a non-empty rate code.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(string ContractNum, string AmrCode, DateTime StartDate), string>> LookupDeliveryGroupRateCodesAsync(
        XellentDbContext context,
        string[] contractNums,
        string dataAreaId,
        CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string, DateTime), string>();
        if (contractNums.Length == 0) return result;

        foreach (var chunk in contractNums.Chunk(ChunkSize))
        {
            var rows = await context.ExuContractPartTables
                .Where(cp => cp.Dataareaid == dataAreaId
                          && chunk.Contains(cp.Contractnum)
                          && cp.Deliverygroupnumratecode != null
                          && cp.Deliverygroupnumratecode != "")
                .Select(cp => new { cp.Contractnum, cp.Amrcode, cp.Startdate, cp.Deliverygroupnumratecode })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                var rateCode = row.Deliverygroupnumratecode!.Trim();
                if (rateCode.Length > 0)
                    result.TryAdd((row.Contractnum, row.Amrcode, row.Startdate), rateCode);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns (contractNum, amrCode, productNum, startDate) → CONTRACTPARTENDDATE from EXU_CONTRACTPARTLOG.
    /// For each (CONTRACTNUM, AMRCODE, PRODUCTNUM, CONTRACTPARTSTARTDATE) the most recent log entry
    /// (by LOGDATETIME DESC) is selected.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(string ContractNum, string AmrCode, string ProductNum, DateTime StartDate), DateTime>> LookupContractPartLogEndDatesAsync(
        XellentDbContext context,
        string[] contractNums,
        string dataAreaId,
        CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string, string, DateTime), DateTime>();
        if (contractNums.Length == 0) return result;

        var conn = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (var chunk in contractNums.Chunk(ChunkSize))
            {
                await using var cmd = conn.CreateCommand();
                var paramNames = chunk.Select((_, i) => $"@p{i}").ToArray();
                cmd.CommandText = $"""
                    WITH ranked AS (
                        SELECT CONTRACTNUM, AMRCODE, PRODUCTNUM, CONTRACTPARTSTARTDATE, CONTRACTPARTENDDATE,
                               ROW_NUMBER() OVER (
                                   PARTITION BY CONTRACTNUM, AMRCODE, PRODUCTNUM, CONTRACTPARTSTARTDATE
                                   ORDER BY LOGDATETIME DESC
                               ) AS rn
                        FROM EXU_CONTRACTPARTLOG
                        WHERE DATAAREAID = @da
                          AND CONTRACTNUM IN ({string.Join(",", paramNames)})
                    )
                    SELECT CONTRACTNUM, AMRCODE, PRODUCTNUM, CONTRACTPARTSTARTDATE, CONTRACTPARTENDDATE
                    FROM ranked
                    WHERE rn = 1
                    """;

                var daParam = cmd.CreateParameter();
                daParam.ParameterName = "@da";
                daParam.Value = dataAreaId;
                cmd.Parameters.Add(daParam);

                for (var i = 0; i < chunk.Length; i++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = paramNames[i];
                    p.Value = chunk[i];
                    cmd.Parameters.Add(p);
                }

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var contractNum = reader.GetString(0);
                    var amrCode = reader.GetString(1);
                    var productNum = reader.GetString(2);
                    var startDate = reader.GetDateTime(3);
                    var endDate = reader.GetDateTime(4);
                    result.TryAdd((contractNum, amrCode, productNum, startDate), endDate);
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
        return result;
    }

    /// <summary>
    /// Reads <c>EXU_CONTRACTPARTLOG</c> for changes logged inside the half-open window
    /// <c>[changedFrom, changedBefore)</c> and returns, per part key, the state as it was before the
    /// <b>first</b> change in that window — which is what a restore has to put back.
    ///
    /// <para>The window is bounded, and both bounds come from the caller. A restore is always aimed at
    /// one known run: an open-ended "everything since" would also undo changes made after it.</para>
    ///
    /// <para>Deliberately a sibling of <see cref="LookupContractPartLogEndDatesAsync"/> rather than an
    /// option on it. That method takes the <i>latest</i> row over <i>all</i> time and is what the
    /// pricelist export builds on; both of those are the opposite of what is needed here, and changing
    /// it would silently change the pricelist.</para>
    ///
    /// <para>Rows written by a named user are excluded from <see cref="ContractPartLogChangeSet.Changes"/>
    /// — a person's decision is never undone. For the 2026-08-31 incident every named-user row falls
    /// outside the window anyway, so the filter is belt-and-braces there; it is kept because the next
    /// incident may not be as tidy, and it must never be the only thing standing between a run and a
    /// wrong restore.</para>
    ///
    /// <para><see cref="ContractPartLogChangeSet.KeysAlsoChangedOutsideWindow"/> is informational: the
    /// keys that also have log rows before or after the window. They are still restored — the logged
    /// old value is by definition the state the previous writer left behind.</para>
    /// </summary>
    public static async Task<ContractPartLogChangeSet> LookupContractPartLogChangesInWindowAsync(
        XellentDbContext context,
        string[] contractNums,
        string dataAreaId,
        DateTime changedFrom,
        DateTime changedBefore,
        CancellationToken ct = default)
    {
        if (changedFrom >= changedBefore)
            throw new ArgumentException(
                $"Restore window is empty or inverted: from {changedFrom:O} is not before {changedBefore:O}.",
                nameof(changedFrom));
        if (contractNums.Length == 0) return ContractPartLogChangeSet.Empty;

        var changes = new List<ContractPartLogChange>();
        var outsideWindowKeys = new HashSet<ContractPartLogKey>();

        const string KeyColumns =
            "CONTRACTNUM, INSTAGREENUM, DELIVERYCATEGORY, COMPANYID, PRODUCTNUM, AMRCODE, CONTRACTPARTSTARTDATE";
        const string BlankUser = "(LOGEMPLID IS NULL OR LTRIM(RTRIM(LOGEMPLID)) = '')";

        var conn = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (var chunk in contractNums.Chunk(ChunkSize))
            {
                var paramNames = chunk.Select((_, i) => $"@p{i}").ToArray();
                var inList = string.Join(",", paramNames);

                await using (var cmd = conn.CreateCommand())
                {
                    // Earliest first inside the window: the state before the first change is the
                    // state to restore. RECID breaks ties between two changes in the same second.
                    cmd.CommandText = $"""
                        WITH ranked AS (
                            SELECT {KeyColumns}, CONTRACTPARTENDDATE, ERRORMSG, DELIVERYGROUPNUM_RATECODE,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY {KeyColumns}
                                       ORDER BY LOGDATETIME ASC, RECID ASC
                                   ) AS rn
                            FROM EXU_CONTRACTPARTLOG
                            WHERE DATAAREAID = @da
                              AND LOGDATETIME >= @from AND LOGDATETIME < @before
                              AND {BlankUser}
                              AND CONTRACTNUM IN ({inList})
                        )
                        SELECT {KeyColumns}, CONTRACTPARTENDDATE, ERRORMSG, DELIVERYGROUPNUM_RATECODE
                        FROM ranked
                        WHERE rn = 1
                        """;
                    AddParameters(cmd, dataAreaId, changedFrom, changedBefore, paramNames, chunk);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var startDate = reader.GetDateTime(6);
                        var key = new ContractPartLogKey(
                            ContractNum: reader.GetString(0),
                            InstAgreeNum: reader.GetString(1),
                            DeliveryCategory: reader.GetString(2),
                            CompanyId: reader.GetString(3),
                            ProductNum: reader.GetString(4),
                            AmrCode: reader.GetString(5),
                            PartStartDate: startDate);
                        changes.Add(new ContractPartLogChange(
                            Key: key,
                            LoggedStartDate: startDate,
                            LoggedEndDate: reader.GetDateTime(7),
                            ChangeType: reader.IsDBNull(8) ? string.Empty : reader.GetString(8).Trim(),
                            DeliveryGroupRateCode: reader.IsDBNull(9) ? null : NullIfBlank(reader.GetString(9))));
                    }
                }

                await using (var outsideCmd = conn.CreateCommand())
                {
                    outsideCmd.CommandText = $"""
                        SELECT DISTINCT {KeyColumns}
                        FROM EXU_CONTRACTPARTLOG
                        WHERE DATAAREAID = @da
                          AND (LOGDATETIME < @from OR LOGDATETIME >= @before)
                          AND CONTRACTNUM IN ({inList})
                        """;
                    AddParameters(outsideCmd, dataAreaId, changedFrom, changedBefore, paramNames, chunk);

                    await using var reader = await outsideCmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        outsideWindowKeys.Add(new ContractPartLogKey(
                            ContractNum: reader.GetString(0),
                            InstAgreeNum: reader.GetString(1),
                            DeliveryCategory: reader.GetString(2),
                            CompanyId: reader.GetString(3),
                            ProductNum: reader.GetString(4),
                            AmrCode: reader.GetString(5),
                            PartStartDate: reader.GetDateTime(6)));
                    }
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        return new ContractPartLogChangeSet(changes, outsideWindowKeys);
    }

    private static void AddParameters(
        System.Data.Common.DbCommand cmd,
        string dataAreaId,
        DateTime changedFrom,
        DateTime changedBefore,
        string[] paramNames,
        string[] contractNums)
    {
        var daParam = cmd.CreateParameter();
        daParam.ParameterName = "@da";
        daParam.Value = dataAreaId;
        cmd.Parameters.Add(daParam);

        var fromParam = cmd.CreateParameter();
        fromParam.ParameterName = "@from";
        fromParam.Value = changedFrom;
        cmd.Parameters.Add(fromParam);

        var beforeParam = cmd.CreateParameter();
        beforeParam.ParameterName = "@before";
        beforeParam.Value = changedBefore;
        cmd.Parameters.Add(beforeParam);

        for (var i = 0; i < contractNums.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = paramNames[i];
            p.Value = contractNums[i];
            cmd.Parameters.Add(p);
        }
    }

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
