using System.Text;
using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Services;

/// <summary>
/// Applies GroupOwners / AccountOwners mappings from <see cref="CreateOptions"/> to extracted customers.
/// For each mapping the owner customer number is resolved to a Guid — either from Settl (live) or
/// via a deterministic hash (dry-run) — and attached as <see cref="CustomerOwnerDto"/> on the target customer(s).
/// </summary>
public class CustomerOwnerResolver
{
    private readonly ISettlCustomerClient _customerClient;
    private readonly IWarningCollector _warnings;
    private readonly Action<string> _logLine;

    public CustomerOwnerResolver(ISettlCustomerClient customerClient, IWarningCollector warnings, Action<string> logLine)
    {
        _customerClient = customerClient;
        _warnings = warnings;
        _logLine = logLine;
    }

    public async Task ApplyAsync(
        List<CreateCustomerDto> customers,
        List<AccountGrouping> accountGroupings,
        CreateOptions options,
        Dictionary<Guid, string> externalOwners,
        CancellationToken cancellationToken)
    {
        var custByNumber = customers
            .Where(c => c.CustomerNumber != null)
            .ToDictionary(c => c.CustomerNumber!);

        if (options.GroupOwners is { Count: > 0 })
            await ApplyGroupOwnersAsync(custByNumber, accountGroupings, options, externalOwners, cancellationToken);

        if (options.AccountOwners is { Count: > 0 })
            await ApplyAccountOwnersAsync(custByNumber, options, externalOwners, cancellationToken);

        if (options.GroupOwners is { Count: > 0 } || options.AccountOwners is { Count: > 0 })
        {
            foreach (var customer in customers.Where(c => c.Owner == null))
                _warnings.AddWarning($"Customer {customer.CustomerNumber}: no owner mapping");
        }
    }

    private async Task ApplyGroupOwnersAsync(
        Dictionary<string, CreateCustomerDto> custByNumber,
        List<AccountGrouping> accountGroupings,
        CreateOptions options,
        Dictionary<Guid, string> externalOwners,
        CancellationToken cancellationToken)
    {
        var groupingsByGroupNum = accountGroupings
            .Where(g => g.GroupNum != null)
            .GroupBy(g => g.GroupNum!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (groupNum, ownerAccountNum) in options.GroupOwners!)
        {
            if (!groupingsByGroupNum.TryGetValue(groupNum, out var groupings))
            {
                _warnings.AddWarning($"GroupOwners references unknown GroupNum '{groupNum}'");
                continue;
            }

            var ownerGuid = await ResolveOwnerGuidAsync(
                ownerAccountNum,
                $"GroupNum '{groupNum}'",
                options.DryRun,
                externalOwners,
                cancellationToken);
            if (ownerGuid == null) continue;

            var reps = groupings.Select(g => g.RepresentativeAccount).ToList();
            var count = 0;
            foreach (var acct in reps)
            {
                if (custByNumber.TryGetValue(acct, out var c))
                {
                    c.Owner = BuildOwner(c, ownerGuid.Value);
                    count++;
                }
            }
            _logLine($"  Owner: GroupNum {groupNum} -> {ownerAccountNum} ({count} customers)");
        }
    }

    private async Task ApplyAccountOwnersAsync(
        Dictionary<string, CreateCustomerDto> custByNumber,
        CreateOptions options,
        Dictionary<Guid, string> externalOwners,
        CancellationToken cancellationToken)
    {
        foreach (var (accountNum, ownerAccountNum) in options.AccountOwners!)
        {
            if (!custByNumber.TryGetValue(accountNum, out var customer))
            {
                _warnings.AddWarning($"AccountOwners references unknown account '{accountNum}'");
                continue;
            }

            var ownerGuid = await ResolveOwnerGuidAsync(
                ownerAccountNum,
                $"account '{accountNum}'",
                options.DryRun,
                externalOwners,
                cancellationToken);
            if (ownerGuid == null) continue;

            customer.Owner = BuildOwner(customer, ownerGuid.Value);
            _logLine($"  Owner: Account {accountNum} -> {ownerAccountNum}");
        }
    }

    /// <summary>
    /// Resolves the Settl Guid for an owner account. In dry-run the Guid is a deterministic
    /// MD5 hash so reports remain stable without hitting Settl.
    /// </summary>
    private async Task<Guid?> ResolveOwnerGuidAsync(
        string ownerAccountNum,
        string referencedBy,
        bool dryRun,
        Dictionary<Guid, string> externalOwners,
        CancellationToken cancellationToken)
    {
        Guid ownerGuid;
        if (dryRun)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(
                Encoding.UTF8.GetBytes($"external-owner:{ownerAccountNum}"));
            ownerGuid = new Guid(bytes);
        }
        else
        {
            var (existingCustomer, error) = await _customerClient.SearchByCustomerNumberAsync(
                ownerAccountNum, cancellationToken: cancellationToken);
            if (existingCustomer == null)
            {
                _warnings.AddWarning($"Owner account '{ownerAccountNum}' for {referencedBy} not found in Settl: {error}");
                return null;
            }
            ownerGuid = existingCustomer.Id;
        }
        externalOwners[ownerGuid] = ownerAccountNum;
        return ownerGuid;
    }

    private static CustomerOwnerDto BuildOwner(CreateCustomerDto customer, Guid ownerGuid) => new()
    {
        Start = customer.Address?.Start ?? DateOnly.FromDateTime(DateTime.Now),
        OwnerId = ownerGuid
    };
}
