using System.Text;
using DataMigration.Core;
using DataMigration.OpsCli.Data;
using DataMigration.XellentData;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Read-only check, per metering point in the state, of whether Xellent settled the customer up to
/// the change of supplier. Back office rule: take the latest statement date that Xellent billed for
/// the point's COS-1 contract and agreement (<c>EXU_ContractPartTable.latestStatementDate()</c>, which
/// reads <c>EXU_LATESTBILLINGDATES.LATESTSTATEMENTDATE</c>). The customer is settled when that date is
/// COS-1. Invoice billing categories (Slutafregning) are deliberately not used.
/// <c>generate-state-from-settl</c> keeps the invoices of customers with a settled point
/// (<see cref="SettledCustomers"/>), except those of contracts not billed up to COS-1
/// (<see cref="HeldContracts"/>); <c>--settlement-check</c> writes the same rows.
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

    /// <summary>One Xellent contract part on a metering point's delpoint, for the part-id column.
    /// Settable properties because EF maps raw SQL results by column name.</summary>
    internal sealed class PartRow
    {
        public string MeteringPoint { get; set; } = "";
        public string ContractNumber { get; set; } = "";
        public long RecId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
    }

    /// <summary>The latest statement date billed for a contract on a metering point's agreement.</summary>
    internal sealed class StatementRow
    {
        public string MeteringPoint { get; set; } = "";
        public string ContractNumber { get; set; } = "";
        public DateTime LatestStatementDate { get; set; }
    }

    internal sealed record Row(Subject Subject, string ContractPartIds, DateOnly? LatestStatementDate,
        string Status);

    internal sealed record XellentRows(IReadOnlyList<PartRow> Parts, IReadOnlyList<StatementRow> Statements);

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
    /// Settled when the latest statement date of the pinned contract on this point is COS-1;
    /// NotSettled when it is earlier or later; Missing when Xellent has no billing row for it.
    /// The part ids are the pinned contract's parts in force on COS-1, for reference only.
    /// </summary>
    internal static Row Classify(Subject subject, IEnumerable<PartRow> parts, IEnumerable<StatementRow> statements)
    {
        if (subject.ContractNumber is null) return new Row(subject, "", null, Missing);
        var day = subject.Cos.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var partIds = parts.Where(p => IsPinned(p.MeteringPoint, p.ContractNumber, subject)
                && p.StartDate.Date <= day
                && (p.EndDate.Date == OpenEnd || p.EndDate.Date >= day))
            .Select(p => p.RecId).Distinct().Order();
        var dates = statements.Where(s => IsPinned(s.MeteringPoint, s.ContractNumber, subject))
            .Select(s => s.LatestStatementDate.Date).ToList();
        var ids = string.Join('|', partIds);
        if (dates.Count == 0) return new Row(subject, ids, null, Missing);
        var latest = dates.Max();
        return new Row(subject, ids, DateOnly.FromDateTime(latest), latest == day ? Settled : NotSettled);
    }

    /// <summary>Classifies every subject against rows already read for them.</summary>
    internal static IReadOnlyList<Row> ClassifyAll(IEnumerable<Subject> subjects, XellentRows xellent)
    {
        var partsByMp = xellent.Parts.ToLookup(p => p.MeteringPoint, StringComparer.Ordinal);
        var statementsByMp = xellent.Statements.ToLookup(p => p.MeteringPoint, StringComparer.Ordinal);
        return [.. subjects.Select(s => Classify(s, partsByMp[s.MeteringPoint], statementsByMp[s.MeteringPoint]))];
    }

    /// <summary>
    /// The Settl customers whose invoice history can come over: at least one metering point is
    /// <see cref="Settled"/>, and every point had a COS-1 contract to check. A point billed up to
    /// another date does not keep the customer out; only that contract's invoices are held back
    /// (<see cref="HeldContracts"/>). Having no point at all keeps the customer out.
    /// </summary>
    internal static HashSet<string> SettledCustomers(IEnumerable<Row> rows) =>
        [.. rows.GroupBy(r => r.Subject.SettlCustomer, StringComparer.Ordinal)
            .Where(g => g.Any(r => r.Status == Settled) && !g.Any(BlocksCustomer))
            .Select(g => g.Key)];

    /// <summary>A point with no COS-1 contract keeps its customer out, because nothing was checked.</summary>
    internal static bool BlocksCustomer(Row row) => row.Status == Missing && row.Subject.ContractNumber is null;

    /// <summary>
    /// The COS-1 contracts that Xellent has not billed up to COS-1: the latest statement is another
    /// date, including the empty date 1900-01-01, which production shows on contracts billed on
    /// account and never settled. Their invoices are not final and are held back. A contract with
    /// no billing row at all (<see cref="Missing"/>) is not held: in production none of those
    /// contracts (405 of 405, 2026-11-18) had a single invoice, so there is nothing to hold.
    /// </summary>
    internal static HashSet<string> HeldContracts(IEnumerable<Row> rows) =>
        [.. rows.Where(r => r.Status == NotSettled && r.Subject.ContractNumber is not null)
            .Select(r => r.Subject.ContractNumber!.Trim())];

    private static bool IsPinned(string meteringPoint, string contractNumber, Subject subject) =>
        meteringPoint == subject.MeteringPoint
        && string.Equals(contractNumber.Trim(), subject.ContractNumber?.Trim(), StringComparison.Ordinal);

    /// <summary>Reads contract parts and latest statement dates for the subjects' points and pinned
    /// contracts, in chunks.</summary>
    internal static Task<XellentRows> ReadAsync(XellentDbContext context,
        IReadOnlyList<Subject> subjects, CancellationToken ct) =>
        ReadAsync(context, subjects.Where(s => s.ContractNumber is not null)
            .Select(s => (s.MeteringPoint, s.ContractNumber!)).ToList(), ct);

    /// <summary>Reads contract parts and latest statement dates for metering points and the contract
    /// the COS-1 resolver pinned each one to, in chunks.</summary>
    internal static async Task<XellentRows> ReadAsync(XellentDbContext context,
        IReadOnlyList<(string MeteringPoint, string ContractNumber)> pinned, CancellationToken ct)
    {
        var parts = new List<PartRow>();
        var statements = new List<StatementRow>();
        foreach (var chunk in pinned.Select(s => s.MeteringPoint).Distinct(StringComparer.Ordinal)
                     .Chunk(MeteringPointChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var points = chunk.ToHashSet(StringComparer.Ordinal);
            var contracts = pinned.Where(s => points.Contains(s.MeteringPoint))
                .Select(s => s.ContractNumber).Distinct(StringComparer.Ordinal).ToArray();
            var pointList = string.Join(", ", chunk.Select((_, i) => $"@m{i}"));
            var contractList = string.Join(", ", contracts.Select((_, i) => $"@c{i}"));
            var companies = $"('{ForInvoiceScope.CompanyId}', '')";

            // Same join as MeteringPointAccountResolver: part → agreement → delpoint on agreement,
            // data area and company.
            var partSql = $"""
                SELECT d.METERINGPOINT AS MeteringPoint, cp.CONTRACTNUM AS ContractNumber,
                       cp.RECID AS RecId, cp.STARTDATE AS StartDate, cp.ENDDATE AS EndDate
                FROM EXU_CONTRACTPARTTABLE cp
                JOIN EXU_AGREEMENTTABLE a ON a.AGREEMENTNUM = cp.INSTAGREENUM
                    AND a.DATAAREAID = cp.DATAAREAID AND a.COMPANYID = cp.COMPANYID
                JOIN EXU_DELPOINT d ON d.ATTACHMENTNUM = a.AGREEMENTNUM
                    AND d.DATAAREAID = a.DATAAREAID AND d.COMPANYID = a.COMPANYID
                WHERE cp.DATAAREAID = @area AND d.DELIVERYCATEGORY = @category
                    AND cp.COMPANYID IN {companies}
                    AND d.METERINGPOINT IN ({pointList}) AND cp.CONTRACTNUM IN ({contractList})
                """;
            parts.AddRange(await context.Database.SqlQueryRaw<PartRow>(partSql, Parameters(context, chunk, contracts))
                .ToListAsync(ct));

            // One billing-dates row per billing run, keyed by contract and agreement (ATTACHMENTNUM).
            // Not in the EF model, hence raw SQL.
            var statementSql = $"""
                SELECT d.METERINGPOINT AS MeteringPoint, lb.CONTRACTNUM AS ContractNumber,
                       MAX(lb.LATESTSTATEMENTDATE) AS LatestStatementDate
                FROM EXU_LATESTBILLINGDATES lb
                JOIN EXU_DELPOINT d ON d.ATTACHMENTNUM = lb.ATTACHMENTNUM AND d.DATAAREAID = lb.DATAAREAID
                WHERE lb.DATAAREAID = @area AND d.DELIVERYCATEGORY = @category
                    AND lb.COMPANYID IN {companies} AND d.COMPANYID IN {companies}
                    AND d.METERINGPOINT IN ({pointList}) AND lb.CONTRACTNUM IN ({contractList})
                GROUP BY d.METERINGPOINT, lb.CONTRACTNUM
                """;
            statements.AddRange(await context.Database.SqlQueryRaw<StatementRow>(statementSql,
                Parameters(context, chunk, contracts)).ToListAsync(ct));
        }
        return new XellentRows(parts, statements);
    }

    /// <summary>Fresh parameter objects per query: SqlClient refuses one already used by another command.
    /// Created by the context's own provider (SqlParameter on SQL Server), so the queries also run
    /// against the in-memory database the integration tests use.</summary>
    private static object[] Parameters(XellentDbContext context, IReadOnlyList<string> points, IReadOnlyList<string> contracts) =>
    [
        Parameter(context, "@area", MigrationConstants.DataAreaId),
        Parameter(context, "@category", MigrationConstants.DeliveryCategoryElectricity),
        .. points.Select((p, i) => Parameter(context, $"@m{i}", p)),
        .. contracts.Select((c, i) => Parameter(context, $"@c{i}", c)),
    ];

    private static System.Data.Common.DbParameter Parameter(XellentDbContext context, string name, string value)
    {
        if (context.Database.IsSqlServer()) return new SqlParameter(name, value);
        using var command = context.Database.GetDbConnection().CreateCommand();
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    internal static string BuildCsv(IEnumerable<Row> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine("SettlCustomer;SourceAccount;MeteringPoint;ContractNumber;ContractPartId;Cos;LatestStatementDate;Status");
        foreach (var row in rows)
        {
            csv.Append(Csv(row.Subject.SettlCustomer)).Append(';')
                .Append(Csv(row.Subject.SourceAccount)).Append(';')
                .Append(Csv(row.Subject.MeteringPoint)).Append(';')
                .Append(Csv(row.Subject.ContractNumber ?? "")).Append(';')
                .Append(Csv(row.ContractPartIds)).Append(';')
                .Append(row.Subject.Cos.ToString("yyyy-MM-dd")).Append(';')
                .Append(row.LatestStatementDate?.ToString("yyyy-MM-dd") ?? "").Append(';')
                .AppendLine(row.Status);
        }
        return csv.ToString();
    }

    /// <summary>One row per invoice held back because its contract was not billed up to COS-1.</summary>
    internal static string BuildHeldInvoicesCsv(IEnumerable<(string Owner, DataMigration.Domain.Customers.Invoice Invoice)> held,
        IEnumerable<Row> rows)
    {
        var byContract = rows.Where(r => r.Status == NotSettled && r.Subject.ContractNumber is not null)
            .GroupBy(r => r.Subject.ContractNumber!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var csv = new StringBuilder();
        csv.AppendLine("SettlCustomer;SourceAccount;InvoiceId;InvoiceDate;BillingCategory;InvoiceStatus;ContractNumber;MeteringPoint;Cos;LatestStatementDate");
        foreach (var (owner, invoice) in held.OrderBy(h => h.Owner, StringComparer.Ordinal)
                     .ThenBy(h => h.Invoice.InvoiceDate).ThenBy(h => h.Invoice.InvoiceId, StringComparer.Ordinal))
        {
            var row = byContract.GetValueOrDefault(invoice.ContractNumber ?? "");
            csv.Append(Csv(owner)).Append(';')
                .Append(Csv(invoice.AccountNumber)).Append(';')
                .Append(Csv(invoice.InvoiceId)).Append(';')
                .Append(invoice.InvoiceDate.ToString("yyyy-MM-dd")).Append(';')
                .Append((int)invoice.BillingCategory).Append(';')
                .Append((int)invoice.InvoiceStatus).Append(';')
                .Append(Csv(invoice.ContractNumber ?? "")).Append(';')
                .Append(Csv(row?.Subject.MeteringPoint ?? "")).Append(';')
                .Append(row?.Subject.Cos.ToString("yyyy-MM-dd") ?? "").Append(';')
                .AppendLine(row?.LatestStatementDate?.ToString("yyyy-MM-dd") ?? "");
        }
        return csv.ToString();
    }

    internal static string HeldInvoicesReportPath(string stateOutputPath)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-held-invoices.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }

    internal static string ReportPath(string stateOutputPath)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-settlement-check.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
