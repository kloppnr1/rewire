namespace DataMigration.Domain.Customers;

/// <summary>
/// Where on the customer's contract tree the MobilePay agreement was found. Carried so
/// reports and warnings can show <em>how</em> the subscription was located, not just that
/// it exists — a MobilePay agreement may legitimately sit on any contract under the
/// customer number, and no rule predicts which one.
/// </summary>
public enum MobilePayOrigin
{
    /// <summary>Agreement sits on a contract that is itself a main contract.</summary>
    MainContract,

    /// <summary>Agreement sits on a sub-contract (<c>Referencetomaincontract</c> set).</summary>
    SubContract
}

/// <summary>
/// Active MobilePay subscription. Only present when the subscription is active; cancelled
/// subscriptions are not included in the model.
///
/// <para>The provenance fields describe the Xellent contract the agreement was read from.
/// <see cref="CandidateCount"/> is the number of active MobilePay agreements found across
/// the customer's contracts — greater than 1 means the customer has competing agreements
/// and the choice made here needs manual review.</para>
/// </summary>
public sealed record MobilePaySubscription(
    string Id,
    string SourceContractNumber = "",
    MobilePayOrigin Origin = MobilePayOrigin.MainContract,
    string SourceDeliveryCategory = "",
    int CandidateCount = 1);
