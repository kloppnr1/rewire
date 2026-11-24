using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services.Extraction;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Services;

/// <summary>
/// Auto-creates a synthetic owner-customer in Settl for each <see cref="OwnerCluster"/>, then
/// attaches <see cref="CustomerOwnerDto"/> to every cluster member so they appear linked under
/// the owner.
///
/// <para>Owner master-data (CVR, postal code, city, country) is copied from the cluster's lowest
/// representative — it is that one member's data, not a property of the cluster, which is grouped
/// on TG4 alone and whose members may well carry different CVRs. Don't present the copied CVR as
/// the cluster's own. The owner name is the cluster's pre-resolved
/// <see cref="OwnerCluster.OwnerName"/> (EXU_GROUPTABLE GROUPNAME, falling back to the TG4 code —
/// one path, regardless of the merge rule in force). CustomerNumber is the first vacant value
/// ≥ 900000 in Settl. Idempotent: on rerun, the lowest member's existing <c>Owner.OwnerId</c> in
/// Settl is reused.</para>
/// </summary>
public class OwnerClusterResolver
{
    private readonly ISettlCustomerClient _customerClient;
    private readonly IWarningCollector _warnings;
    private readonly Action<string> _logLine;
    private readonly OwnerRunState? _runState;
    private readonly OwnerNumberAllocator _allocator;

    /// <param name="runState">
    /// Set by a batched run whose clusters span the whole scope. Two things follow from it: an
    /// owner already resolved by an earlier batch is reused instead of created again, and the
    /// two-member rule is measured on the cluster rather than on the batch — a batch holding one
    /// member of a 222-account cluster must still link it, where a run that derives its own
    /// clusters would rightly skip a one-member cluster.
    /// </param>
    public OwnerClusterResolver(
        ISettlCustomerClient customerClient,
        IWarningCollector warnings,
        Action<string> logLine,
        OwnerRunState? runState = null)
    {
        _customerClient = customerClient;
        _warnings = warnings;
        _logLine = logLine;
        _runState = runState;
        _allocator = runState is null
            ? new OwnerNumberAllocator(customerClient)
            : runState.Allocator ??= new OwnerNumberAllocator(customerClient);
    }

    public async Task ApplyAsync(
        List<CreateCustomerDto> customers,
        List<OwnerCluster> clusters,
        Dictionary<Guid, string> externalOwners,
        List<OwnerCustomerInfo> ownerCustomers,
        bool dryRun,
        CancellationToken cancellationToken,
        List<OwnerClusterSkipInfo>? skipped = null)
    {
        var custByNumber = customers
            .Where(c => c.CustomerNumber != null)
            .ToDictionary(c => c.CustomerNumber!, StringComparer.Ordinal);

        // En sprunget klynge stod før kun som en advarsel i loggen, hvor den drukner i en
        // all-active-kørsel. Rapporten skal kunne vise hvilke ejere der ikke blev til noget,
        // og hvorfor — medlemmerne er migreret, de hænger bare ikke under en ejer.
        void Skip(OwnerCluster cluster, string reason, string? sourceAccount = null) =>
            skipped?.Add(new OwnerClusterSkipInfo(
                cluster.ClusterKey, cluster.OwnerName, reason, sourceAccount, cluster.MemberAccountnums));

        foreach (var cluster in clusters)
        {
            // Master-data source for the owner: the cluster's lowest member, or — when the cluster
            // spans the whole run and this batch simply does not hold it — the lowest member the
            // batch does hold. Members are in ascending order, so the first present one is it.
            var sourceAccount = cluster.MemberAccountnums.FirstOrDefault(custByNumber.ContainsKey);
            if (sourceAccount == null || !custByNumber.TryGetValue(sourceAccount, out var lowest))
            {
                _warnings.AddWarning(
                    $"Owner cluster {cluster.ClusterKey}: lowest customer {cluster.LowestAccountnum} not in extracted customers — skipping owner creation");
                Skip(cluster, $"Laveste medlemskonto {cluster.LowestAccountnum} er ikke med i udtrækket");
                continue;
            }

            // A cluster that already has an owner needs no master data, so a member without a CVR
            // must not stop its siblings from being linked to it.
            var alreadyResolved = _runState is not null && _runState.TryGet(cluster.ClusterKey, out _);
            if (!alreadyResolved && string.IsNullOrEmpty(lowest.VatIdentificationNumber))
            {
                _warnings.AddError(
                    $"Owner cluster {cluster.ClusterKey}: lowest customer {sourceAccount} has no CVR — cannot synthesize owner");
                Skip(cluster, "Laveste medlemskonto har intet CVR — ejer-kunden kan ikke bygges", sourceAccount);
                continue;
            }

            // Pre-flight: count members that would actually receive an Owner-link.
            // Skip cluster entirely if < 2 — a single child doesn't justify creating an owner-customer.
            // With run-level clusters the two-member rule has already been settled on the whole
            // scope, so here it is only "is there anything in this batch to link".
            var linkableAccounts = cluster.MemberAccountnums
                .Where(a => custByNumber.TryGetValue(a, out var c) && c.Owner == null)
                .ToList();
            var presentInExtraction = cluster.MemberAccountnums.Count(a => custByNumber.ContainsKey(a));
            var manualSkipped = presentInExtraction - linkableAccounts.Count;
            var minimumLinkable = _runState is null ? 2 : 1;
            if (linkableAccounts.Count < minimumLinkable)
            {
                _logLine($"  Skip cluster {cluster.ClusterKey}: only {linkableAccounts.Count} customer(s) to link");
                Skip(cluster, $"Kun {linkableAccounts.Count} kunde(r) at koble — en ejer kræver mindst {minimumLinkable}", sourceAccount);
                continue;
            }

            var (ownerGuid, ownerNumber) = await ResolveOwnerAsync(cluster, lowest, dryRun, cancellationToken);
            if (ownerGuid == null)
            {
                // Fejlen er allerede skrevet af ResolveOwnerAsync — enten kunne der ikke tildeles
                // et kundenummer, eller oprettelsen i Settl fejlede.
                Skip(cluster, "Ejer-kunden kunne ikke oprettes i Settl — se fejllisten", sourceAccount);
                continue;
            }

            var startDate = lowest.Address?.Start ?? DateOnly.FromDateTime(DateTime.Now);
            foreach (var acct in linkableAccounts)
                custByNumber[acct].Owner = new CustomerOwnerDto { Start = startDate, OwnerId = ownerGuid.Value };

            externalOwners[ownerGuid.Value] = ownerNumber!;
            var lowestPhys = lowest.Address?.PostalAddress?.PhysicalAddress;
            ownerCustomers.Add(new OwnerCustomerInfo(
                OwnerId: ownerGuid.Value,
                CustomerNumber: ownerNumber!,
                ClusterKey: cluster.ClusterKey,
                GroupName: cluster.OwnerName,
                Cvr: lowest.VatIdentificationNumber,
                PostalCode: lowestPhys?.PostalCode,
                City: lowestPhys?.City,
                CountryCode: lowestPhys?.CountryCode ?? lowest.CountryCode,
                LinkedAccountnums: linkableAccounts));
            var skipNote = manualSkipped > 0 ? $", {manualSkipped} skipped (manual owner mapping)" : "";
            _logLine($"  Owner: {cluster.OwnerName} ({cluster.ClusterKey}) -> CustomerNumber {ownerNumber} ({linkableAccounts.Count} customers{skipNote})");
        }
    }

    private async Task<(Guid? OwnerGuid, string? OwnerNumber)> ResolveOwnerAsync(
        OwnerCluster cluster,
        CreateCustomerDto lowest,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // Already resolved by an earlier batch of the same run — no Settl call needed.
        if (_runState is not null && _runState.TryGet(cluster.ClusterKey, out var known))
            return (known.OwnerId, known.CustomerNumber);

        // Idempotency: if a member already exists in Settl with an Owner set, reuse it.
        if (!dryRun)
        {
            foreach (var anchor in IdempotencyAnchors(cluster))
            {
                var (existingMember, searchErr) = await _customerClient.SearchByCustomerNumberAsync(
                    anchor, cancellationToken: cancellationToken);
                if (searchErr != null || existingMember == null) continue;

                var (full, getErr) = await _customerClient.GetByIdAsync(existingMember.Id, cancellationToken);
                if (getErr != null || full?.Owner == null) continue;

                var (ownerCust, ownerGetErr) = await _customerClient.GetByIdAsync(
                    full.Owner.OwnerId, cancellationToken);
                if (ownerGetErr != null || ownerCust == null || string.IsNullOrEmpty(ownerCust.CustomerNumber))
                    continue;

                Remember(cluster, full.Owner.OwnerId, ownerCust.CustomerNumber);
                return (full.Owner.OwnerId, ownerCust.CustomerNumber);
            }
        }

        var (allocated, allocErr) = await _allocator.AllocateAsync(dryRun, cancellationToken);
        if (allocErr != null || allocated == null)
        {
            _warnings.AddError(
                $"Owner cluster {cluster.ClusterKey}: {allocErr ?? "no CustomerNumber allocated"}");
            return (null, null);
        }

        var ownerDto = BuildOwnerDto(lowest, allocated, cluster.OwnerName);

        if (dryRun)
        {
            Remember(cluster, ownerDto.Id, allocated);
            return (ownerDto.Id, allocated);
        }

        var (ok, createErr) = await _customerClient.CreateAsync(
            new CreateCustomersRequest { Customers = [ownerDto] }, cancellationToken);
        if (!ok)
        {
            _warnings.AddError(
                $"Owner cluster {cluster.ClusterKey}: failed to create owner-customer {allocated}: {createErr}");
            return (null, null);
        }

        Remember(cluster, ownerDto.Id, allocated);
        return (ownerDto.Id, allocated);
    }

    private void Remember(OwnerCluster cluster, Guid ownerId, string customerNumber) =>
        _runState?.Record(cluster.ClusterKey, new ResolvedOwner(ownerId, customerNumber));

    /// <summary>
    /// Members to search Settl for when looking for an owner this cluster already has.
    ///
    /// <para>A run that derives its own clusters checks the lowest member only, as it always has.
    /// A batched run cannot: it may be resuming after a crash, with the run's memory gone and the
    /// lowest member sitting in a batch that never ran — and then the check would find nothing and
    /// create a second owner for a cluster that already has one. So it walks the members instead,
    /// stopping at the first one Settl knows. Capped: the walk is there to catch a resume, not to
    /// pay 222 lookups for a cluster that is genuinely new.</para>
    /// </summary>
    private IEnumerable<string> IdempotencyAnchors(OwnerCluster cluster) =>
        _runState is null
            ? [cluster.LowestAccountnum]
            : cluster.MemberAccountnums.Take(MaxIdempotencyAnchors);

    private const int MaxIdempotencyAnchors = 25;

    private static CreateCustomerDto BuildOwnerDto(CreateCustomerDto lowest, string customerNumber, string ownerName)
    {
        var lowestPhys = lowest.Address?.PostalAddress?.PhysicalAddress;
        var startDate = lowest.Address?.Start ?? DateOnly.FromDateTime(DateTime.Now);

        return new CreateCustomerDto
        {
            Id = Guid.NewGuid(),
            CountryCode = lowest.CountryCode,
            CustomerNumber = customerNumber,
            CustomerType = "Company",
            VatIdentificationNumber = lowest.VatIdentificationNumber,
            Address = new CustomerAddressDto
            {
                Start = startDate,
                PostalAddress = new PostalAddressDto
                {
                    Name1 = new ConsumerNameDto { FullName = ownerName },
                    PhysicalAddress = new PhysicalAddressDto
                    {
                        PostalCode = lowestPhys?.PostalCode,
                        City = lowestPhys?.City,
                        CountryCode = lowestPhys?.CountryCode ?? lowest.CountryCode
                    }
                }
            }
        };
    }
}
