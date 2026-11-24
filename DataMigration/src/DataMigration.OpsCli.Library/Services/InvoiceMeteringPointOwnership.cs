using DataMigration.XellentData;
using DataMigration.Core;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

/// <summary>Reads the contract on each invoice and its single parent electricity MP.</summary>
internal static class InvoiceMeteringPointOwnership
{
    internal sealed record InvoiceKey(string SourceAccount, string InvoiceId);

    internal static async Task<(IReadOnlyDictionary<InvoiceKey, string[]> ContractsByInvoice,
        IReadOnlyDictionary<string, string[]> MeteringPointsByContract)> LoadAsync(
        XellentDbContext context, IReadOnlyCollection<string> sources, string dataAreaId, CancellationToken ct)
    {
        var invoiceContracts = new Dictionary<InvoiceKey, HashSet<string>>();
        foreach (var chunk in sources.Chunk(1500))
        {
            var rows = await context.CustInvoiceJours
                .Where(i => i.Dataareaid == dataAreaId && chunk.Contains(i.Invoiceaccount))
                .Select(i => new { i.Invoiceaccount, i.Invoiceid, i.ExuContractnum })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                var key = new InvoiceKey(row.Invoiceaccount.Trim(), row.Invoiceid.Trim());
                if (!invoiceContracts.TryGetValue(key, out var contracts))
                    invoiceContracts[key] = contracts = new HashSet<string>(StringComparer.Ordinal);
                contracts.Add(row.ExuContractnum?.Trim() ?? "");
            }
        }

        var contractMps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var chunk in invoiceContracts.Values.SelectMany(v => v)
            .Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var rows = await (
                from c in context.ExuContractTables
                join cp in context.ExuContractPartTables
                    on new { c.Contractnum, c.Dataareaid, c.Companyid }
                    equals new { cp.Contractnum, cp.Dataareaid, cp.Companyid }
                join ag in context.ExuAgreementTables
                    on new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid }
                    equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                join d in context.ExuDelpoints
                    on new { Attachmentnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                    equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
                where c.Dataareaid == dataAreaId
                    && (c.Companyid == ForInvoiceScope.CompanyId || c.Companyid == "")
                    && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && chunk.Contains(c.Contractnum)
                select new { c.Contractnum, d.Meteringpoint }
            ).Distinct().ToListAsync(ct);
            foreach (var row in rows)
            {
                var contract = row.Contractnum.Trim();
                if (!contractMps.TryGetValue(contract, out var mps))
                    contractMps[contract] = mps = new HashSet<string>(StringComparer.Ordinal);
                mps.Add(row.Meteringpoint.Trim());
            }
        }
        return (
            invoiceContracts.ToDictionary(kv => kv.Key, kv => kv.Value.Order(StringComparer.Ordinal).ToArray()),
            contractMps.ToDictionary(kv => kv.Key, kv => kv.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal));
    }

    internal static (string? Customer, string Reason, string Contract, string Mp) Resolve(
        InvoiceKey invoice,
        IReadOnlyDictionary<InvoiceKey, string[]> contractsByInvoice,
        IReadOnlyDictionary<string, string[]> mpsByContract,
        IReadOnlyDictionary<string, string> customerByMp)
    {
        if (!contractsByInvoice.TryGetValue(invoice, out var contracts) || contracts.Length != 1 || contracts[0].Length == 0)
            return (null, "InvoiceHasNoUniqueContract", string.Join('|', contracts ?? []), "");
        var contract = contracts[0];
        if (!mpsByContract.TryGetValue(contract, out var mps) || mps.Length != 1 || mps[0].Length == 0)
            return (null, "ContractHasNoUniqueParentMeteringPoint", contract, string.Join('|', mps ?? []));
        var mp = mps[0];
        return customerByMp.TryGetValue(mp, out var customer)
            ? (customer, "", contract, mp)
            : (null, "MeteringPointHasNoInScopeSettlCustomer", contract, mp);
    }
}
