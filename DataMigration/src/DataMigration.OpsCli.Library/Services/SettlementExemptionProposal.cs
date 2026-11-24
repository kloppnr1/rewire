using DataMigration.Migration.Services;

namespace DataMigration.OpsCli.Services;

public sealed record SettlementConfigCurrentExemption(Guid Id, string ExemptionType, Guid ProductElementId, double ExemptionPercentage);
public sealed record SettlementConfigExemptionRead(string MeteringPointIdentification, string? CustomerNumber,
    string ReadStatus, IReadOnlyList<SettlementConfigCurrentExemption> CurrentExemptions,
    string HistoricalValidity, string? Warning,
    string? CurrentCustomerNumber = null, string CurrentCustomerReadStatus = "NotRead",
    string AttributionStatus = "NotRequired", string? AttributionReason = null,
    IReadOnlyList<string>? CurrentCustomerNumbers = null, DateOnly? RequestedDate = null);
public sealed record SettlementConfigExemptionCustomer(string CustomerNumber, string? XellentProposedSegment,
    string? FinalProposedSegment, bool Changed, string? Warning, string? AppliedRule = null);
public sealed record SettlementConfigExemptionReview(DateOnly? RequestedDate, DateTime ReadAt,
    IReadOnlyList<SettlementConfigExemptionRead> MeteringPoints, IReadOnlyList<SettlementConfigExemptionCustomer> Customers);

/// <summary>Applies explicitly requested current exemptions to resolved business proposals.
/// Historical application requires a confirmed matching current customer; exemptions never transfer between customers.</summary>
public static class SettlementExemptionProposal
{
    public const string HistoricalWarning = "These are current exemptions. We could not verify whether they applied on the selected date.";
    private static bool SameCustomer(string selected, string current, string environment) =>
        string.Equals(selected, current, StringComparison.Ordinal)
        || CustomerNumberPolicy.AccountCandidates(selected, environment).Contains(current, StringComparer.Ordinal)
        || CustomerNumberPolicy.AccountCandidates(current, environment).Contains(selected, StringComparer.Ordinal);
    public const string ChangedHistoricalWarning = "The current exemption changed this proposal. We could not verify whether it applied on the selected date.";
    public static (CustomerSegmentGrouper.CustomerSegmentResult Segments, SettlementConfigExemptionReview? Review) Apply(
        CustomerSegmentGrouper.CustomerSegmentResult original, IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, CustomerIdentityKind> identities, IReadOnlyList<SettlementConfigExemptionRead>? reads,
        DateOnly? requestedDate, DateTime readAt,
        IReadOnlyDictionary<string, SettlementCurrentCustomerEvidence>? currentCustomers = null,
        string environment = "", IReadOnlyDictionary<string,DateOnly>? requestedDatesByMp = null)
    {
        if (reads is null) return (original, null);
        var customerByMp = entries.GroupBy(e => e.MeteringPointIdentification, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().CustomerNumber, StringComparer.Ordinal);
        var attributed = reads.Select(r =>
        {
            var selected = customerByMp.GetValueOrDefault(r.MeteringPointIdentification);
            var current = currentCustomers?.GetValueOrDefault(r.MeteringPointIdentification);
            var pointDate = requestedDate ?? (requestedDatesByMp?.TryGetValue(r.MeteringPointIdentification,out var date) == true ? date : (DateOnly?)null);
            var status = pointDate is null ? "NotRequired" : string.IsNullOrWhiteSpace(selected) ? "SelectedCustomerMissing"
                : current is null || current.ReadStatus == "ReadFailed" || current.ReadStatus == "NotRead" ? "CurrentCustomerReadFailed"
                : current.ReadStatus == "Ambiguous" ? "CurrentCustomerAmbiguous"
                : current.ReadStatus != "Read" || string.IsNullOrWhiteSpace(current.CustomerNumber) ? "CurrentCustomerMissing"
                : SameCustomer(selected, current.CustomerNumber, environment) ? "SameCustomer" : "DifferentCustomer";
            var reason = status switch {
                "DifferentCustomer" => "The metering point now belongs to a different customer. Current exemptions were not used for this proposal.",
                "CurrentCustomerAmbiguous" => "More than one current customer was found. Current exemptions were not used for this proposal.",
                "CurrentCustomerMissing" => "No current customer was found. Current exemptions were not used for this proposal.",
                "CurrentCustomerReadFailed" => "Could not verify the current customer. Current exemptions were not used for this proposal.",
                "SelectedCustomerMissing" => "The customer for this export could not be determined. Current exemptions were not used.",
                "SameCustomer" => "The current customer matches the customer selected for this export.",
                _ => null };
            return r with {
                CustomerNumber = selected, CurrentCustomerNumber = current?.CustomerNumber,
                CurrentCustomerNumbers = current?.CustomerNumbers,
                CurrentCustomerReadStatus = current?.ReadStatus ?? "NotRead",
                AttributionStatus = status, AttributionReason = reason,
                RequestedDate = pointDate,
                HistoricalValidity = pointDate is not null ? "NotVerified" : r.HistoricalValidity,
                Warning = r.ReadStatus == "ReadFailed" ? "Could not read current exemptions."
                    : pointDate is not null && status != "SameCustomer" ? reason
                    : pointDate is not null ? HistoricalWarning : null };
        }).ToArray();
        var exemptCustomers = attributed.Where(r => r.ReadStatus == "Read" && r.CurrentExemptions.Count > 0 && r.CustomerNumber is not null
            && (r.RequestedDate is null || r.AttributionStatus == "SameCustomer"))
            .Select(r => r.CustomerNumber!).ToHashSet(StringComparer.Ordinal);
        var datedCustomers = attributed.Where(r => r.RequestedDate is not null && r.CustomerNumber is not null)
            .Select(r => r.CustomerNumber!).ToHashSet(StringComparer.Ordinal);
        var changedCustomers = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<SettlementConfigExemptionCustomer>();
        var grouped = new Dictionary<(string Segment, bool Mapped), List<(SettlementConfigCustomer Customer, CustomerSegmentGroup Original)>>();
        foreach (var group in original.Segments)
        foreach (var customer in group.Customers)
        {
            // Only known mapped business assignments may be changed. SME and E18 production retain priority; source gaps and conflicts are never rescued.
            var segment = group.Segment;
            if (group.Mapped && !string.Equals(segment, "SME", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(segment, SettlementE18Proposal.Segment, StringComparison.OrdinalIgnoreCase)
                && identities.GetValueOrDefault(customer.CustomerNumber) == CustomerIdentityKind.Cvr
                && exemptCustomers.Contains(customer.CustomerNumber))
            {
                segment = "Erhverv_UG";
                if (!string.Equals(segment, group.Segment, StringComparison.OrdinalIgnoreCase))
                    changedCustomers.Add(customer.CustomerNumber);
            }
            var key = (Segment: segment.ToUpperInvariant(), group.Mapped);
            if (!grouped.TryGetValue(key, out var members)) grouped[key] = members = [];
            members.Add((customer, group));
            rows.Add(new(customer.CustomerNumber, group.Segment, segment,
                changedCustomers.Contains(customer.CustomerNumber),
                changedCustomers.Contains(customer.CustomerNumber) && datedCustomers.Contains(customer.CustomerNumber) ? ChangedHistoricalWarning : null,
                segment == SettlementE18Proposal.Segment ? "E18" : changedCustomers.Contains(customer.CustomerNumber) ? "CurrentExemption" : null));
        }
        foreach (var customer in attributed.Select(r => r.CustomerNumber).OfType<string>().Distinct(StringComparer.Ordinal)
            .Where(c => !rows.Any(r => r.CustomerNumber == c)))
            rows.Add(new(customer, null, null, false, null));
        var segments = grouped.Select(g => new CustomerSegmentGroup(g.Key.Segment == "ERHVERV_UG" ? "Erhverv_UG" : g.Value[0].Original.Segment,
            g.Key.Mapped,
            g.Value.SelectMany(m => m.Original.ContractTemplates).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            g.Value.Count, g.Value.Sum(m => m.Customer.MeteringPointCount),
            g.Value.Select(m => m.Customer).OrderBy(c => c.CustomerNumber, StringComparer.Ordinal).ToArray()))
            .OrderBy(g => g.Segment, StringComparer.Ordinal).ToArray();
        return (original with { Segments = segments }, new(requestedDate, readAt, attributed,
            rows.OrderBy(r => r.CustomerNumber, StringComparer.Ordinal).ToArray()));
    }
}
