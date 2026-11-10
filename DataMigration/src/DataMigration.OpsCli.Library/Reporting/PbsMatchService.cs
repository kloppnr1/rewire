using DataMigration.Core.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting;

public class PbsMatchService
{
    /// <summary>
    /// Matches and annotates in one step — the remarks are part of the match result, not something
    /// a caller adds afterwards.
    ///
    /// <para>They used to be: the CLI runner and the dashboard each carried the same annotation
    /// block, and the two drifted the moment one of them was edited. A production export on
    /// 2026-08-07 still read "Ingen aktiv PBS i Xellent" from the dashboard while the CLI had
    /// already been reworded.</para>
    /// </summary>
    public List<PbsMatchReportRow> Match(
        PbsContractQueryResult queryResult,
        IReadOnlyList<SettlCsvRecord> settlRecords,
        bool includeTerminated,
        DateTime? changeOfSupplierDate)
    {
        var rows = Match(queryResult.Contracts, settlRecords);
        Annotate(rows, queryResult, includeTerminated, changeOfSupplierDate);
        return rows;
    }

    /// <summary>
    /// Why a row reads the way it does: a terminated agreement that nonetheless matched, and the
    /// Xellent context behind a Settl row that found no contract.
    /// </summary>
    public static void Annotate(
        List<PbsMatchReportRow> rows,
        PbsContractQueryResult queryResult,
        bool includeTerminated,
        DateTime? changeOfSupplierDate)
    {
        // Code 4 (afmeldt) matching a Settl agreement is really a mismatch — but only when
        // --include-terminated is NOT set. With the flag the user asked for terminated ones, and
        // the match stands.
        if (!includeTerminated)
        {
            foreach (var row in rows.Where(r => r.MatchStatus is PbsMatchStatus.Matched or PbsMatchStatus.MatchedJoint))
            {
                if (row.ContractNumber != null
                    && queryResult.ContractCodes.TryGetValue(row.ContractNumber, out var code)
                    && code == 4)
                {
                    row.MatchStatus = PbsMatchStatus.Mismatch;
                    row.AddNote("Afmeldt i Xellent");
                }
            }
        }

        foreach (var row in rows.Where(r => r.MatchStatus == PbsMatchStatus.UnmatchedSettl))
        {
            if (row.MeteringpointId != null && queryResult.ExcludedByMp.TryGetValue(row.MeteringpointId, out var exc))
            {
                row.CustomerNumber = exc.CustomerAccount;
                row.ContractNumber = exc.ContractNumber;
                row.AddNote(exc.Pbstransactioncode == 0
                    // Code 0 here now means neither the metering point's own contract nor its main
                    // contract carried an agreement — not merely the one.
                    ? $"Ingen aktiv PBS på kundens kontrakter ({exc.CustomerName})"
                    : includeTerminated && changeOfSupplierDate.HasValue && exc.Pbstransdate <= changeOfSupplierDate.Value
                        ? $"Afmeldt i Xellent før COS ({exc.CustomerName})"
                        : $"Afmeldt i Xellent ({exc.CustomerName})");
            }
        }
    }

    public List<PbsMatchReportRow> Match(
        IReadOnlyList<XellentPbsContract> xellentContracts,
        IReadOnlyList<SettlCsvRecord> settlRecords)
    {
        var results = new List<PbsMatchReportRow>();

        var settlByMeteringpoint = settlRecords
            .Where(r => !string.IsNullOrEmpty(r.MeteringpointId))
            .GroupBy(r => r.MeteringpointId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var settlByJointInvoice = settlRecords
            .Where(r => string.IsNullOrEmpty(r.MeteringpointId) && !string.IsNullOrEmpty(r.JointInvoiceNumber))
            .GroupBy(r => r.JointInvoiceNumber!)
            .ToDictionary(g => g.Key, g => g.First());

        var matchedSettlMeteringpoints = new HashSet<string>();
        var matchedSettlJointInvoices = new HashSet<string>();
        var matchedXellentContracts = new HashSet<string>();
        var jointGroups = new Dictionary<string, List<XellentPbsContract>>();

        foreach (var contract in xellentContracts)
        {
            if (!string.IsNullOrEmpty(contract.MeteringpointId)
                && settlByMeteringpoint.TryGetValue(contract.MeteringpointId, out var settlEntries))
            {
                matchedSettlMeteringpoints.Add(contract.MeteringpointId);
                matchedXellentContracts.Add(contract.ContractNumber);
                foreach (var settlEntry in settlEntries)
                {
                    results.Add(new PbsMatchReportRow
                    {
                        CustomerNumber = contract.CustomerAccount,
                        ContractNumber = contract.ContractNumber,
                        SourceKind = contract.SourceKind,
                        ContractStartDate = contract.ContractStartDate,
                        ContractEndDate = contract.ContractEndDate,
                        AgreementCountOnAccount = contract.AgreementCountOnAccount,
                        CandidatesOnMeteringPoint = contract.CandidatesOnMeteringPoint,
                        ExpiredCandidatesOnMeteringPoint = contract.ExpiredCandidatesOnMeteringPoint,
                        PbsAgreementSourceContract = contract.PbsAgreementSourceContract,
                        PbsAgreementSourceDeliveryCategory = contract.PbsAgreementSourceDeliveryCategory,
                        PbsAgreementNumber = contract.PbsAgreementNumber,
                        MeteringpointId = contract.MeteringpointId,
                        PaymentAgreementNumber = settlEntry.PaymentAgreementNumber,
                        ExternalCustomerNumber = settlEntry.ExternalCustomerNumber,
                        PaymentMethod = settlEntry.PaymentMethod,
                        AgreementType = settlEntry.AgreementType,
                        PayerName = settlEntry.PayerName,
                        MatchStatus = PbsMatchStatus.Matched,
                        Notes = settlEntries.Count > 1 ? $"{settlEntries.Count} Settl-rækker på samme målepunkt" : null
                    });
                }
                continue;
            }

            string? matchedJointNumber = null;
            if (settlByJointInvoice.ContainsKey(contract.ContractNumber))
                matchedJointNumber = contract.ContractNumber;
            else if (!string.IsNullOrEmpty(contract.Referencetomaincontract)
                     && settlByJointInvoice.ContainsKey(contract.Referencetomaincontract))
                matchedJointNumber = contract.Referencetomaincontract;

            if (matchedJointNumber != null)
            {
                if (!jointGroups.TryGetValue(matchedJointNumber, out var group))
                    jointGroups[matchedJointNumber] = group = [];
                group.Add(contract);
                continue;
            }
        }

        foreach (var (jointNumber, contracts) in jointGroups)
        {
            matchedSettlJointInvoices.Add(jointNumber);
            var settlEntry = settlByJointInvoice[jointNumber];

            foreach (var contract in contracts.OrderBy(c => c.ContractNumber))
            {
                matchedXellentContracts.Add(contract.ContractNumber);
                results.Add(new PbsMatchReportRow
                {
                    CustomerNumber = contract.CustomerAccount,
                    ContractNumber = contract.ContractNumber,
                    SourceKind = contract.SourceKind,
                    ContractStartDate = contract.ContractStartDate,
                    ContractEndDate = contract.ContractEndDate,
                    AgreementCountOnAccount = contract.AgreementCountOnAccount,
                    CandidatesOnMeteringPoint = contract.CandidatesOnMeteringPoint,
                    ExpiredCandidatesOnMeteringPoint = contract.ExpiredCandidatesOnMeteringPoint,
                    PbsAgreementSourceContract = contract.PbsAgreementSourceContract,
                    PbsAgreementSourceDeliveryCategory = contract.PbsAgreementSourceDeliveryCategory,
                    PbsAgreementNumber = contract.PbsAgreementNumber,
                    JointInvoiceNumber = jointNumber,
                    PaymentAgreementNumber = settlEntry.PaymentAgreementNumber,
                    ExternalCustomerNumber = settlEntry.ExternalCustomerNumber,
                    PaymentMethod = settlEntry.PaymentMethod,
                    AgreementType = settlEntry.AgreementType,
                    PayerName = settlEntry.PayerName,
                    MatchStatus = PbsMatchStatus.MatchedJoint
                });
            }
        }

        foreach (var contract in xellentContracts)
        {
            if (matchedXellentContracts.Contains(contract.ContractNumber))
                continue;

            results.Add(new PbsMatchReportRow
            {
                CustomerNumber = contract.CustomerAccount,
                ContractNumber = contract.ContractNumber,
                SourceKind = contract.SourceKind,
                ContractStartDate = contract.ContractStartDate,
                ContractEndDate = contract.ContractEndDate,
                AgreementCountOnAccount = contract.AgreementCountOnAccount,
                CandidatesOnMeteringPoint = contract.CandidatesOnMeteringPoint,
                ExpiredCandidatesOnMeteringPoint = contract.ExpiredCandidatesOnMeteringPoint,
                PbsAgreementSourceContract = contract.PbsAgreementSourceContract,
                PbsAgreementSourceDeliveryCategory = contract.PbsAgreementSourceDeliveryCategory,
                PbsAgreementNumber = contract.PbsAgreementNumber,
                MeteringpointId = contract.MeteringpointId,
                MatchStatus = PbsMatchStatus.UnmatchedXellent
            });
        }

        foreach (var settlRecord in settlRecords)
        {
            if (!string.IsNullOrEmpty(settlRecord.MeteringpointId)
                && matchedSettlMeteringpoints.Contains(settlRecord.MeteringpointId))
                continue;

            if (string.IsNullOrEmpty(settlRecord.MeteringpointId)
                && !string.IsNullOrEmpty(settlRecord.JointInvoiceNumber)
                && matchedSettlJointInvoices.Contains(settlRecord.JointInvoiceNumber))
                continue;

            results.Add(new PbsMatchReportRow
            {
                MeteringpointId = settlRecord.MeteringpointId,
                JointInvoiceNumber = settlRecord.JointInvoiceNumber,
                PaymentAgreementNumber = settlRecord.PaymentAgreementNumber,
                ExternalCustomerNumber = settlRecord.ExternalCustomerNumber,
                PaymentMethod = settlRecord.PaymentMethod,
                AgreementType = settlRecord.AgreementType,
                PayerName = settlRecord.PayerName,
                MatchStatus = PbsMatchStatus.UnmatchedSettl
            });
        }

        // Where the agreement came from another of the customer's contracts, the row names two
        // contracts — the one owning the metering point, and the one carrying the agreement. Said
        // here rather than in a column of its own, and appended so it cannot displace a note the
        // row already has.
        foreach (var row in results)
        {
            if (row.PbsAgreementSourceContract is not { } source) continue;
            var commodity = string.IsNullOrEmpty(row.PbsAgreementSourceDeliveryCategory)
                ? ""
                : $" ({row.PbsAgreementSourceDeliveryCategory})";
            row.AddNote($"PBS fra kontrakt {source}{commodity}");
        }

        return results.OrderBy(r => r.MatchStatus).ThenBy(r => r.CustomerNumber).ToList();
    }
}
