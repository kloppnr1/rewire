using System.Text;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

internal sealed record ExcludedCompanyInvoice(
    string SourceAccount,
    string InvoiceId,
    string ContractNumber,
    string CompanyId);

/// <summary>
/// CUSTINVOICEJOUR has no company column. Its contract number identifies the company through
/// EXU_CONTRACTTABLE. Explicit non-FOR companies are excluded. Older contracts with an empty
/// Companyid predate that classification and remain in scope; a genuinely missing contract does not.
/// </summary>
internal static class ForInvoiceScope
{
    internal const string CompanyId = "for";

    internal static async Task<IReadOnlyList<ExcludedCompanyInvoice>> FindExcludedAsync(
        XellentDbContext context, IReadOnlyCollection<string> sourceAccounts, string dataAreaId,
        CancellationToken ct)
    {
        var excluded = new List<ExcludedCompanyInvoice>();
        foreach (var chunk in sourceAccounts.Chunk(1500))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await (
                from invoice in context.CustInvoiceJours
                join contract in context.ExuContractTables
                    on new { Contractnum = invoice.ExuContractnum, invoice.Dataareaid }
                    equals new { contract.Contractnum, contract.Dataareaid } into contracts
                from contract in contracts.DefaultIfEmpty()
                where invoice.Dataareaid == dataAreaId
                    && chunk.Contains(invoice.Invoiceaccount)
                    && (contract == null
                        || (contract.Companyid != CompanyId && contract.Companyid != ""))
                select new
                {
                    invoice.Invoiceaccount,
                    invoice.Invoiceid,
                    invoice.ExuContractnum,
                    CompanyId = contract == null ? "" : contract.Companyid
                }
            ).ToListAsync(ct);
            excluded.AddRange(rows.Select(r => new ExcludedCompanyInvoice(
                r.Invoiceaccount.Trim(), r.Invoiceid.Trim(), r.ExuContractnum?.Trim() ?? "",
                r.CompanyId?.Trim() ?? "")));
        }
        return excluded.Distinct().ToArray();
    }

    internal static string BuildCsv(IReadOnlyList<ExcludedCompanyInvoice> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine("SourceAccount;InvoiceId;ContractNumber;CompanyId;Reason");
        foreach (var row in rows.OrderBy(r => r.SourceAccount, StringComparer.Ordinal)
            .ThenBy(r => r.InvoiceId, StringComparer.Ordinal))
            csv.Append(row.SourceAccount).Append(';')
                .Append(row.InvoiceId).Append(';')
                .Append(row.ContractNumber).Append(';')
                .Append(row.CompanyId).Append(';')
                .AppendLine(row.CompanyId.Length == 0 ? "NoXellentContract" : "NotForCompany");
        return csv.ToString();
    }

    internal static string ReportPath(string stateOutputPath)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-excluded-invoices.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }
}
