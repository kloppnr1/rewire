using DataMigration.Core;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Holds Xellent's MobilePay agreements up against Settl, using the same chain as <c>reconcile</c>:
///
/// <code>
/// Settl metering points in delivery (+ COS) → MeteringPointAccountResolver (pins to the contract
///     ending COS-1) → Xellent account → the account's uncancelled MobilePay agreements
/// </code>
///
/// <para>Going this way removes the guesswork the account-first version needed. It no longer has to
/// decide which contract is current (the COS-1 pinning decides), nor which of an account's metering
/// points represents the agreement (every Settl metering point resolving to the account is tried).
/// An earlier account-first attempt filtered on open contracts and thereby dropped every migrated
/// customer — migrating to Settl ends the Xellent contract — which is exactly the class of mistake
/// this direction cannot make.</para>
///
/// <para>It also answers a question the other direction could not even ask: a migrated customer
/// holding an active MobilePay agreement that Settl has no payment agreement for. Those customers
/// lose their MobilePay silently, and they are reported as
/// <see cref="MobilePayMatchStatus.MissingInSettl"/>.</para>
/// </summary>
public sealed class MobilePaySettlMatchService
{
    private readonly ISettlMeteringPointClient _mpClient;
    private readonly string _connectionString;
    private readonly Action<string> _log;

    public MobilePaySettlMatchService(
        ISettlMeteringPointClient mpClient, string connectionString, Action<string>? log = null)
    {
        _mpClient = mpClient;
        _connectionString = connectionString;
        _log = log ?? (_ => { });
    }

    /// <param name="includeTerminated">Also read cancelled MobilePay agreements. The row builder
    /// then keeps only those cancelled after the metering point change of supply — the ones the
    /// migration itself closed — on the same terms PBS applies to its terminated agreements.</param>
    public async Task<MobilePayMatchResult> MatchAsync(
        string dataAreaId,
        IReadOnlyList<SettlCsvRecord> settlRecords,
        CancellationToken ct = default,
        bool includeTerminated = false)
    {
        // The market-v3 pagination is delivery-category scoped; without this the client returns
        // nothing and every account looks unmigrated. Set here rather than at the call sites so no
        // caller can forget it — reconcile does the same before its scan.
        _mpClient.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        // 1) Every metering point Settl is currently delivering to, with the date it changed supplier.
        //
        //    Metering points out of delivery are deliberately excluded. Settl's InDelivery!=true
        //    covers three different states, and none of them belongs in a handover: delivery has
        //    ended (the customer left — pinning it would attribute their MobilePay to a metering
        //    point they no longer hold), delivery has not started yet, and created-but-never-
        //    activated. The last two are indistinguishable from each other and neither has a
        //    change-of-supplier event, so COS-1 cannot pin them at all — the account would have to
        //    be guessed from whichever contract is open today. A sheet handed to the payment
        //    service should not rest on a guess.
        _log("Henter målepunkter og COS fra Settl...");
        var settlMps = await new SettlMeteringPointCosLoader(_mpClient, _log)
            .LoadAsync(new CosPeriod(null, null), ct);
        _log($"  {settlMps.Count} målepunkter i levering i Settl");

        // 2) Pin each metering point to the account that owned it at change-of-supplier. No
        //    no-history fallback is passed: every metering point here is in delivery and therefore
        //    has a change-of-supplier event to pin against.
        var cosByMpId = settlMps.ToDictionary(kv => kv.Key, kv => kv.Value.Cos, StringComparer.Ordinal);

        _log("Oversætter målepunkter til Xellent-konti (COS-1)...");
        var resolution = await MeteringPointAccountResolver.ResolveAsync(
            _connectionString, cosByMpId, dataAreaId, noHistoryMpIds: null, ct);

        var settlMpsByAccount = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in resolution.Pairs)
        {
            if (!settlMpsByAccount.TryGetValue(pair.Account, out var list))
                settlMpsByAccount[pair.Account] = list = [];
            list.Add(pair.MpId);
        }
        _log($"  {settlMpsByAccount.Count} migrerede konti");

        // 3) The MobilePay agreements of those accounts, and only those. Settl decides the
        //    population — the same discipline reconcile follows, and the reason it never has to
        //    guess whether an unseen customer is migrated. Reading the whole data area instead
        //    produced a third bucket of accounts the scan had never looked at, which the output
        //    then labelled "not migrated" without grounds.
        var agreements = await XellentMobilePayContractReader.FetchAsync(
            _connectionString, dataAreaId, settlMpsByAccount.Keys.ToHashSet(StringComparer.Ordinal), ct,
            includeTerminated);
        _log($"  {agreements.Count} aktive MobilePay-aftaler på de konti");

        // 4) The rules themselves — pure, and tested without a Settl environment or a database.
        var rows = MobilePayRowBuilder.Build(
            agreements, settlMpsByAccount, cosByMpId, resolution.OwnerContractByMpId, settlRecords);

        // Every agreement read belongs to an account the scan resolved, so no row can come back as
        // NotMigrated any more — the status survives for callers of the builder, not for this path.
        // Nothing is filtered out here: what is in scope is decided by what Settl holds, up front.
        return MobilePayMatchResult.From(
            rows,
            new MobilePayScanFacts(
                SettlMeteringPointsScanned: settlMps.Count,
                MigratedAccountsResolved: settlMpsByAccount.Count,
                AgreementsRead: agreements.Count));
    }

}
