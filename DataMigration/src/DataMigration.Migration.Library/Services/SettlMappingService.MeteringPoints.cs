using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlApi.Models;
using DataMigration.SettlClient.Internal;

namespace DataMigration.Migration.Services;

/// <summary>
/// Metering-point extraction. Partial of <see cref="SettlMappingService"/> kept in its own file.
/// All methods project from the preloaded <see cref="DataMigration.Domain.Xellent.XellentLoadResult"/>
/// — no direct database access.
/// </summary>
public partial class SettlMappingService
{
    /// <summary>
    /// Builds a lookup of domain MPs by their identification string, for use in methods
    /// that need to join contract-assignments to MP master data.
    /// </summary>
    private Dictionary<string, MeteringPoint> GetMpsByIdentification() =>
        _loadResult.MeteringPoints
            .Where(mp => !string.IsNullOrEmpty(mp.Identification))
            .ToDictionary(mp => mp.Identification, StringComparer.Ordinal);

    /// <summary>
    /// Returns one <see cref="MeteringPointDto"/> per unique MP referenced by the loaded customers,
    /// built via <see cref="MeteringPointTranslator.Build"/>. Deduplicates MPs that appear in
    /// multiple contract parts.
    /// </summary>
    public Task<List<MeteringPointDto>> ExtractMeteringPointsAsync(string[] accountNumbers)
    {
        var mpsByIdentification = GetMpsByIdentification();
        var startDate = DateOnly.FromDateTime(_referenceDate);
        var seenMpIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MeteringPointDto>();
        foreach (var customer in _loadResult.Customers)
            foreach (var contract in customer.Contracts)
                foreach (var assignment in contract.MeteringPoints)
                {
                    if (!seenMpIds.Add(assignment.MeteringPointId)) continue;
                    if (!mpsByIdentification.TryGetValue(assignment.MeteringPointId, out var mp)) continue;
                    result.Add(MeteringPointTranslator.Build(mp, customer));
                }
        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns account-number → list of MP tuples for linking MPs to customers, payers, and
    /// contracts. <c>ContractStartDate</c> uses <c>Contractstartdate</c>; <c>IsJoined</c> is
    /// derived from <see cref="MeteringPoint.JointInvoiceGroupId"/> (groups with ≥2 members).
    /// </summary>
    public Task<Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>>> ExtractAccountMeteringPointMappingAsync(
        string[] accountNumbers)
    {
        var mpsByIdentification = GetMpsByIdentification();
        var validJointGroups = _loadResult.JointInvoiceGroups
            .Where(g => g.MemberMeteringPointIds.Count >= 2)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, List<(string, DateTime, DateTime, DateTime, bool, string, string, string, string)>>(StringComparer.Ordinal);
        foreach (var customer in _loadResult.Customers)
        {
            var tuples = new List<(string, DateTime, DateTime, DateTime, bool, string, string, string, string)>();
            foreach (var contract in customer.Contracts)
            {
                var contractStart = contract.Start.ToDateTime(TimeOnly.MinValue);
                foreach (var assignment in contract.MeteringPoints)
                {
                    if (!mpsByIdentification.TryGetValue(assignment.MeteringPointId, out var mp))
                        continue;
                    var isJoined = mp.JointInvoiceGroupId != null
                        && validJointGroups.Contains(mp.JointInvoiceGroupId);
                    tuples.Add((
                        assignment.MeteringPointId,
                        contractStart,
                        assignment.Start.ToDateTime(TimeOnly.MinValue),
                        assignment.EndDate.ToDateTime(TimeOnly.MinValue),
                        isJoined,
                        contract.Number,
                        assignment.Product.Code,
                        assignment.AmrCode,
                        contract.PaymentTerm ?? ""));
                }
            }
            if (tuples.Count > 0)
                result[customer.AccountNumber] = tuples;
        }
        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns contract-number → billing plan number for all contracts in the loaded customer set.
    /// Used by the create-flow to assign MPs to Settl Settlement Groups.
    /// </summary>
    public Task<Dictionary<string, string>> ExtractContractBillingPlansAsync(string[] accountNumbers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var customer in _loadResult.Customers)
            foreach (var contract in customer.Contracts)
                result.TryAdd(contract.Number, contract.Billing.PlanCode ?? "");
        return Task.FromResult(result);
    }
}
