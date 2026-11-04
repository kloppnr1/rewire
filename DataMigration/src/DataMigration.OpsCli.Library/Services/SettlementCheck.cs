using System.Text;
using DataMigration.Core;
using DataMigration.OpsCli.Data;
using DataMigration.XellentData;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Read-only check, per metering point in the state, of whether Xellent settled the customer up to
/// the change of supplier. Back office rule: the contract part that held the point at COS-1 is
/// settled when its <c>TAKEORPAYPREVDATE</c> equals COS-1. Invoice billing categories (Slutafregning)
/// are deliberately not used.
/// </summary>
internal static class SettlementCheck
{
    /// <summary>Metering points per Xellent query. Each query carries the points and their contract
    /// numbers as parameters, so this stays well under SQL Server's 2,100-parameter cap.</summary>
    internal const int MeteringPointChunkSize = 500;

    internal const string Settled = "Settled";
    internal const string NotSettled = "NotSettled";
    internal const string Missing = "Missing";

    private static readonly DateTime OpenEnd = new(1900, 1, 1);

    /// <param name="ContractNumber">The contract the COS-1 resolver pinned the point to; null when it
    /// found none.</param>
    internal sealed record Subject(string SettlCustomer, string SourceAccount, string MeteringPoint,
        string? ContractNumber, DateOnly Cos);

    /// <summary>One Xellent contract part on a metering point's delpoint. Settable properties because
    /// EF maps raw SQL results by column name.</summary>
    internal sealed class PartRow
    {
        public string MeteringPoint { get; set; } = "";
        public string ContractNumber { get; set; } = "";
        public long RecId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public DateTime TakeOrPayPrevDate { get; set; }
    }

    internal sealed record Row(Subject Subject, string ContractPartIds, string TakeOrPayPrevDates,
        string Status);

    /// <summary>
    /// The metering points that ended up in the state: an ownership claim whose source account the
    /// state lists under the claiming customer. Points with any ownership issue are left out, because
    /// their claim did not cleanly make it into the state.
    /// </summary>
    internal static IReadOnlyList<Subject> Subjects(MigrationStateDocument state,
        IEnumerable<GenerateStateOfflineCache.OwnershipEvidence> evidence,
        IReadOnlyDictionary<string, string> contractByMp,
        IEnumerable<string> meteringPointsWithIssues)
    {
        var sourcesByCustomer = state.Accounts
            .GroupBy(a => a.SettlAccountNumber, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(a => a.SourceAccounts).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var withIssues = meteringPointsWithIssues.ToHashSet(StringComparer.Ordinal);
        return evidence
            .Where(e => !withIssues.Contains(e.MeteringPoint)
                && sourcesByCustomer.TryGetValue(e.SettlCustomerNumber, out var sources)
                && sources.Contains(e.SourceAccount))
            .Select(e => new Subject(e.SettlCustomerNumber, e.SourceAccount, e.MeteringPoint,
                contractByMp.GetValueOrDefault(e.MeteringPoint), e.Cos))
            .Distinct()
            .OrderBy(s => s.SettlCustomer, StringComparer.Ordinal)
            .ThenBy(s => s.MeteringPoint, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Settled when every part of the pinned contract on this point that was in force on COS-1 has
    /// <c>TAKEORPAYPREVDATE</c> = COS-1; NotSettled when any of them has another date; Missing when
    /// no such part exists.
    /// </summary>
    internal static Row Classify(Subject subject, IEnumerable<PartRow> parts)
    {
        var day = subject.Cos.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var held = subject.ContractNumber is null
            ? []
            : parts.Where(p => p.MeteringPoint == subject.MeteringPoint
                    && string.Equals(p.ContractNumber.Trim(), subject.ContractNumber.Trim(), StringComparison.Ordinal)
                    && p.StartDate.Date <= day
                    && (p.EndDate.Date == OpenEnd || p.EndDate.Date >= day))
                .GroupBy(p => p.RecId).Select(g => g.First())
                .OrderBy(p => p.RecId).ToList();
        if (held.Count == 0) return new Row(subject, "", "", Missing);
        var status = held.All(p => p.TakeOrPayPrevDate.Date == day) ? Settled : NotSettled;
        return new Row(subject,
            string.Join('|', held.Select(p => p.RecId)),
            string.Join('|', held.Select(p => FormatDate(p.TakeOrPayPrevDate)).Distinct()),
            status);
    }

    /// <summary>Reads the contract parts for the subjects' points and pinned contracts, in chunks.</summary>
    internal static async Task<IReadOnlyList<PartRow>> ReadPartsAsync(XellentDbContext context,
        IReadOnlyList<Subject> subjects, CancellationToken ct)
    {
        var rows = new List<PartRow>();
        var pinned = subjects.Where(s => s.ContractNumber is not null).ToList();
        foreach (var chunk in pinned.Select(s => s.MeteringPoint).Distinct(StringComparer.Ordinal)
                     .Chunk(MeteringPointChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var points = chunk.ToHashSet(StringComparer.Ordinal);
            var contracts = pinned.Where(s => points.Contains(s.MeteringPoint))
                .Select(s => s.ContractNumber!).Distinct(StringComparer.Ordinal).ToArray();
            var parameters = new List<SqlParameter>
            {
                new("@area", MigrationConstants.DataAreaId),
                new("@category", MigrationConstants.DeliveryCategoryElectricity),
            };
            var pointNames = AddList(parameters, "m", chunk);
            var contractNames = AddList(parameters, "c", contracts);
            // Same join as MeteringPointAccountResolver: part → agreement → delpoint on
            // agreement, data area and company. TAKEORPAYPREVDATE is not in the EF model, hence raw SQL.
            var sql = $"""
                SELECT d.METERINGPOINT AS MeteringPoint, cp.CONTRACTNUM AS ContractNumber,
                       cp.RECID AS RecId, cp.STARTDATE AS StartDate, cp.ENDDATE AS EndDate,
                       cp.TAKEORPAYPREVDATE AS TakeOrPayPrevDate
                FROM EXU_CONTRACTPARTTABLE cp
                JOIN EXU_AGREEMENTTABLE a ON a.AGREEMENTNUM = cp.INSTAGREENUM
                    AND a.DATAAREAID = cp.DATAAREAID AND a.COMPANYID = cp.COMPANYID
                JOIN EXU_DELPOINT d ON d.ATTACHMENTNUM = a.AGREEMENTNUM
                    AND d.DATAAREAID = a.DATAAREAID AND d.COMPANYID = a.COMPANYID
                WHERE cp.DATAAREAID = @area AND d.DELIVERYCATEGORY = @category
                    AND cp.COMPANYID IN ('{ForInvoiceScope.CompanyId}', '')
                    AND d.METERINGPOINT IN ({pointNames})
                    AND cp.CONTRACTNUM IN ({contractNames})
                """;
            rows.AddRange(await context.Database
                .SqlQueryRaw<PartRow>(sql, parameters.ToArray<object>())
                .ToListAsync(ct));
        }
        return rows;
    }

    private static string AddList(List<SqlParameter> parameters, string prefix, IReadOnlyList<string> values)
    {
        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"@{prefix}{i}";
            parameters.Add(new SqlParameter(name, values[i]));
            names.Add(name);
        }
        return string.Join(", ", names);
    }

    internal static string BuildCsv(IEnumerable<Row> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine("SettlCustomer;SourceAccount;MeteringPoint;ContractNumber;ContractPartId;Cos;TakeOrPayPrevDate;Status");
        foreach (var row in rows)
        {
            csv.Append(Csv(row.Subject.SettlCustomer)).Append(';')
                .Append(Csv(row.Subject.SourceAccount)).Append(';')
                .Append(Csv(row.Subject.MeteringPoint)).Append(';')
                .Append(Csv(row.Subject.ContractNumber ?? "")).Append(';')
                .Append(Csv(row.ContractPartIds)).Append(';')
                .Append(row.Subject.Cos.ToString("yyyy-MM-dd")).Append(';')
                .Append(Csv(row.TakeOrPayPrevDates)).Append(';')
                .AppendLine(row.Status);
        }
        return csv.ToString();
    }

    internal static string ReportPath(string stateOutputPath)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-settlement-check.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }

    /// <summary>Xellent's 1900-01-01 means "never set"; the CSV shows it empty.</summary>
    private static string FormatDate(DateTime date) =>
        date.Date == OpenEnd ? "" : date.ToString("yyyy-MM-dd");

    private static string Csv(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
