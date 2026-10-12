using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal static class ScopedSettlContractCapture
{
    internal static async Task<List<SettlContractLinkRow>> ReadAsync(StatePullScope scope,
        ISettlMeteringPointClient pointClient, ISettlContractClient contractClient, CancellationToken ct)
    {
        var targets = scope.MeteringPoints.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var activity = new InventoryActivity("scoped contract");
        using var timer = new CaptureStage("Scoped Settl contract links", details: () => activity.Summary);
        Console.Error.WriteLine($"  Scoped Settl contracts: selectedMPs={targets.Length}; discover contracts through each selected metering point.");
        var pointById = new Dictionary<Guid, string>();
        var contracts = new Dictionary<Guid, ContractInfoDto>();
        var expectedPointsByContract = new Dictionary<Guid, HashSet<Guid>>();
        var discovered = 0;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var (point, pointError) = await activity.CallAsync("meteringPointLookup", Guid.Empty,
                () => pointClient.SearchByIdentificationAsync(target, ct));
            if (pointError is not null || point is null || point.Id == Guid.Empty
                || !string.Equals(point.MeteringPointIdentification, target, StringComparison.Ordinal))
                throw new InvalidOperationException($"Settl scoped metering-point lookup failed or mismatched (target={target}, completed={discovered}/{targets.Length}); capture remains incomplete.");
            if (!pointById.TryAdd(point.Id, target))
                throw new InvalidOperationException($"Settl scoped metering points resolved to duplicate GUID {point.Id}; capture remains incomplete.");
            var (found, error) = await activity.CallAsync("contractsForMeteringPoint", point.Id,
                () => contractClient.FindContractsByMeteringPointAsync(point.Id, ct));
            if (error is not null || found is null)
                throw new InvalidOperationException($"Settl contracts-for-metering-point lookup failed (entityId={point.Id}, completed={discovered}/{targets.Length}); capture remains incomplete.");
            foreach (var contract in found)
            {
                if (contract.Id == Guid.Empty || string.IsNullOrWhiteSpace(contract.CommodityType))
                    throw new InvalidOperationException($"Settl scoped contract returned incomplete identity or commodity (meteringPointId={point.Id}); capture remains incomplete.");
                if (!string.Equals(contract.CommodityType, "Electricity", StringComparison.OrdinalIgnoreCase)) continue;
                if (contracts.TryGetValue(contract.Id, out var previous)
                    && (previous.ContractName != contract.ContractName || previous.CommodityType != contract.CommodityType))
                    throw new InvalidOperationException($"Settl contract metadata changed during scoped discovery (contractId={contract.Id}); capture remains incomplete.");
                contracts[contract.Id] = contract;
                if (!expectedPointsByContract.TryGetValue(contract.Id, out var points)) expectedPointsByContract[contract.Id] = points = [];
                points.Add(point.Id);
            }
            discovered++;
            activity.Finished();
            Console.Error.WriteLine($"  Scoped Settl contract discovery: meteringPoints={discovered}/{targets.Length}, relevantContracts={contracts.Count}, elapsedSeconds={timer.Elapsed.TotalSeconds:F2}.");
        }

        var rows = new List<SettlContractLinkRow>();
        var readContracts = 0;
        foreach (var contract in contracts.Values.OrderBy(c => c.Id))
        {
            ct.ThrowIfCancellationRequested();
            Console.Error.WriteLine($"  Scoped Settl contract links: reading contract {readContracts + 1}/{contracts.Count}, contractId={contract.Id}.");
            // This endpoint supplies association IDs and original periods, including ended links.
            // It paginates the selected contract's full link list; filter only after preserving identity.
            var (links, error) = await activity.CallAsync("contractLinks", contract.Id,
                () => contractClient.GetContractMeteringPointsAsync(contract.Id, ct));
            if (error is not null || links is null)
                throw new InvalidOperationException($"Settl scoped contract-link lookup failed (contractId={contract.Id}, completed={readContracts}/{contracts.Count}); capture remains incomplete.");
            var foundPoints = new HashSet<Guid>();
            var seenLinks = new Dictionary<Guid, SettlContractLinkRow>();
            foreach (var link in links)
            {
                if (!pointById.TryGetValue(link.MeteringPointId, out var target))
                {
                    if (link.MeteringPointIdentification is { } identification && targets.Contains(identification, StringComparer.Ordinal))
                        throw new InvalidOperationException($"Settl scoped contract-link GUID does not match resolved metering point (contractId={contract.Id}); capture remains incomplete.");
                    continue;
                }
                if (link.Id == Guid.Empty || !string.IsNullOrEmpty(link.MeteringPointIdentification)
                    && !string.Equals(link.MeteringPointIdentification, target, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Settl scoped contract link has incomplete or mismatched identity (contractId={contract.Id}, meteringPointId={link.MeteringPointId}); capture remains incomplete.");
                var row = new SettlContractLinkRow(target, contract.Id, contract.ContractName ?? "", link.Start, link.End, link.Id);
                if (seenLinks.TryGetValue(link.Id, out var existing) && existing != row)
                    throw new InvalidOperationException($"Settl association data changed during pagination (contractId={contract.Id}, associationId={link.Id}); capture remains incomplete.");
                seenLinks[link.Id] = row;
                foundPoints.Add(link.MeteringPointId);
            }
            if (!expectedPointsByContract[contract.Id].IsSubsetOf(foundPoints))
                throw new InvalidOperationException($"Settl contract discovery and association inventory disagree (contractId={contract.Id}); capture remains incomplete.");
            rows.AddRange(seenLinks.Values);
            readContracts++;
            Console.Error.WriteLine($"  Scoped Settl contract links: contracts={readContracts}/{contracts.Count}, returnedLinks={links.Count}, selectedLinks={seenLinks.Count}, elapsedSeconds={timer.Elapsed.TotalSeconds:F2}.");
        }
        timer.Complete($"selectedMPs={targets.Length}, relevantContracts={contracts.Count}, meteringPointLookupCalls={targets.Length}, contractDiscoveryCalls={targets.Length}, contractLinkReadCalls={readContracts}, selectedLinks={rows.Count}. {activity.Summary}");
        return rows.OrderBy(r => r.MeteringPoint, StringComparer.Ordinal).ThenBy(r => r.ContractId).ThenBy(r => r.Start).ToList();
    }
}
