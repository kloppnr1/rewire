using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>A contract part the batch deleted, put back with its metering point resolved.</summary>
internal sealed record RestoredContractPart(ContractPartLogKey Key, DateTime EndDate, string MeteringPoint);

/// <summary>The two things a restore does, decided but not yet applied.</summary>
internal sealed record ContractPartRestorePlan(
    IReadOnlyDictionary<ContractPartLogKey, DateTime> EndDates,
    IReadOnlyList<ContractPartLogChange> DeletedParts);

/// <summary>
/// Undoes, for the length of one extraction, what an unattended Xellent batch did to
/// <c>EXU_CONTRACTPARTTABLE</c> inside a given time window. Built once per load and handed to the
/// mappers, which ask it for a part's restored end date and for the parts it deleted.
///
/// <para>Off unless <see cref="MapperOptions.RestorePartsWindow"/> is set; <see cref="IsEmpty"/> is
/// the fast path every call site takes when it is not. Nothing here writes to Xellent — see the
/// option's documentation: this is a bridge until the data is corrected at source.</para>
/// </summary>
internal sealed class ContractPartRestore
{
    /// <summary>End date overwritten. The logged old end date goes back on the live part row.</summary>
    internal const string ChangeTypeEndDateChanged = "Slutdato er ændret";

    /// <summary>Part deleted outright. The part is synthesised back from the log row.</summary>
    internal const string ChangeTypePartDeleted = "Kontraktdel er slettet";

    /// <summary>
    /// Start date changed. Not restored: the start date is part of the key, so the logged row
    /// identifies a part that no longer exists under that identity and putting it back would be a
    /// guess about which live row it became. Warned about instead.
    /// </summary>
    internal const string ChangeTypeStartDateChanged = "Startdato er ændret";

    public static ContractPartRestore Empty { get; } = new(
        new Dictionary<ContractPartLogKey, DateTime>(),
        []);

    private readonly IReadOnlyDictionary<ContractPartLogKey, DateTime> _endDates;
    private readonly IReadOnlyList<RestoredContractPart> _deletedParts;

    private ContractPartRestore(
        IReadOnlyDictionary<ContractPartLogKey, DateTime> endDates,
        IReadOnlyList<RestoredContractPart> deletedParts)
    {
        _endDates = endDates;
        _deletedParts = deletedParts;
    }

    /// <summary>Nothing to restore — every call site short-circuits on this.</summary>
    public bool IsEmpty => _endDates.Count == 0 && _deletedParts.Count == 0;

    /// <summary>The end date this part had before the batch, or null when the batch never touched it.</summary>
    public DateTime? RestoredEndDate(ContractPartLogKey key)
        => _endDates.TryGetValue(NormalizeKey(key), out var endDate) ? endDate : null;

    /// <summary>The parts the batch deleted in one delivery category, with metering points resolved.</summary>
    public IReadOnlyList<RestoredContractPart> DeletedPartsFor(string deliveryCategory)
        => _deletedParts
            .Where(p => string.Equals(p.Key.DeliveryCategory, deliveryCategory, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Reads the log for the window in <paramref name="options"/> and builds the restore. Returns
    /// <see cref="Empty"/> when no window is configured, without touching the database.
    /// </summary>
    public static async Task<ContractPartRestore> CreateAsync(
        XellentDbContext context,
        IWarningCollector warnings,
        MapperOptions options,
        string dataAreaId,
        string[] accountNumbers,
        CancellationToken cancellationToken = default)
    {
        if (options.RestorePartsWindow is not { } window || accountNumbers.Length == 0)
            return Empty;

        var contractNums = await LoadContractNumbersAsync(context, dataAreaId, accountNumbers, cancellationToken);
        if (contractNums.Length == 0) return Empty;

        var changeSet = await XellentContractPartQueries.LookupContractPartLogChangesInWindowAsync(
            context, contractNums, dataAreaId, window.From, window.Before, cancellationToken);

        var plan = BuildPlan(changeSet, warnings);
        if (plan.EndDates.Count == 0 && plan.DeletedParts.Count == 0)
        {
            warnings.AddInfo(
                $"Contract-part restore: no batch changes found in [{window.From:u}, {window.Before:u}) " +
                "for the accounts in this run [Xellent: EXU_CONTRACTPARTLOG]");
            return Empty;
        }

        var meteringPoints = await ResolveMeteringPointsAsync(
            context, dataAreaId, plan.DeletedParts, cancellationToken);

        var restore = FromPlan(plan, meteringPoints, warnings);

        warnings.AddInfo(
            $"Contract-part restore: {plan.EndDates.Count} end date(s) put back and " +
            $"{restore._deletedParts.Count} deleted part(s) synthesised from the log window " +
            $"[{window.From:u}, {window.Before:u}). This corrects the extraction only — Xellent " +
            "itself is unchanged [Xellent: EXU_CONTRACTPARTLOG]");

        return restore;
    }

    /// <summary>
    /// Routes the logged changes by type. The whitelist is explicit and matched on equality: anything
    /// that is not one of the three known part-level change types is ignored, which is what keeps the
    /// one-time-charge rows ("Engangsydelse …", whose text carries the charge name) from being read as
    /// part changes.
    /// </summary>
    internal static ContractPartRestorePlan BuildPlan(ContractPartLogChangeSet changeSet, IWarningCollector warnings)
    {
        var endDates = new Dictionary<ContractPartLogKey, DateTime>();
        var deleted = new List<ContractPartLogChange>();

        foreach (var change in changeSet.Changes)
        {
            var key = change.Key;

            // Context only. A key with rows on both sides of the window is still restored: the value
            // the batch logged is by definition the state the previous writer left behind.
            if (changeSet.KeysAlsoChangedOutsideWindow.Contains(key))
                warnings.AddInfo(
                    $"Contract {key.ContractNum} part {key.ProductNum}/{key.AmrCode} " +
                    $"(agreement {key.InstAgreeNum}, start {key.PartStartDate:yyyy-MM-dd}) also has log rows " +
                    "outside the restore window — restored to the state the window's first change " +
                    "recorded [Xellent: EXU_CONTRACTPARTLOG]");

            switch (change.ChangeType)
            {
                case ChangeTypeEndDateChanged:
                    endDates[NormalizeKey(key)] = change.LoggedEndDate;
                    break;

                case ChangeTypePartDeleted:
                    deleted.Add(change);
                    break;

                case ChangeTypeStartDateChanged:
                    warnings.AddWarning(
                        $"Contract {key.ContractNum}: the batch changed the start date of part " +
                        $"{key.ProductNum}/{key.AmrCode} (agreement {key.InstAgreeNum}, logged start " +
                        $"{key.PartStartDate:yyyy-MM-dd}) — not restored, because the start date is part " +
                        "of the part's identity and the live row can no longer be matched to it " +
                        "[Xellent: EXU_CONTRACTPARTLOG ERRORMSG]");
                    break;

                default:
                    // Not a part-level change (one-time charges and anything future) — ignored.
                    break;
            }
        }

        return new ContractPartRestorePlan(endDates, deleted);
    }

    /// <summary>
    /// Turns the planned deletions into restored parts. A part whose agreement or installation cannot
    /// be resolved to a metering point is dropped with a warning — a metering point is never invented.
    /// </summary>
    internal static ContractPartRestore FromPlan(
        ContractPartRestorePlan plan,
        IReadOnlyDictionary<(string DeliveryCategory, string CompanyId, string InstAgreeNum), IReadOnlyList<string>> meteringPointsByAgreement,
        IWarningCollector warnings)
    {
        var restored = new List<RestoredContractPart>();
        foreach (var change in plan.DeletedParts)
        {
            var key = change.Key;
            var lookupKey = LookupKey(key);
            if (!meteringPointsByAgreement.TryGetValue(lookupKey, out var meteringPoints) || meteringPoints.Count == 0)
            {
                warnings.AddWarning(
                    $"Contract {key.ContractNum}: deleted part {key.ProductNum} on agreement " +
                    $"{key.InstAgreeNum} ({key.DeliveryCategory}) was not restored — the agreement " +
                    "resolves to no delivery point or installation, and a metering point is never " +
                    "invented [Xellent: EXU_CONTRACTPARTLOG]");
                continue;
            }

            foreach (var meteringPoint in meteringPoints)
                restored.Add(new RestoredContractPart(key, change.LoggedEndDate, meteringPoint));
        }

        return new ContractPartRestore(plan.EndDates, restored);
    }

    private static async Task<string[]> LoadContractNumbersAsync(
        XellentDbContext context,
        string dataAreaId,
        string[] accountNumbers,
        CancellationToken cancellationToken)
    {
        var contractNums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in accountNumbers.Chunk(ChunkSize))
        {
            var page = await context.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.Custaccount))
                .Select(c => c.Contractnum)
                .Distinct()
                .ToListAsync(cancellationToken);
            contractNums.UnionWith(page);
        }
        return [.. contractNums];
    }

    /// <summary>
    /// Resolves the metering point behind each deleted part's <c>INSTAGREENUM</c>. Electricity goes
    /// agreement → delivery point and reads the real metering point id; the installation-based
    /// commodities check that the installation exists and synthesise <c>V{Instnum}01</c>, the same
    /// identifier the mappers build. Delivery category is compared case-insensitively — the log and
    /// the live tables do not agree on the casing of "El-ekstern".
    /// </summary>
    private static async Task<IReadOnlyDictionary<(string, string, string), IReadOnlyList<string>>> ResolveMeteringPointsAsync(
        XellentDbContext context,
        string dataAreaId,
        IReadOnlyList<ContractPartLogChange> deletedParts,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string, string), IReadOnlyList<string>>();
        if (deletedParts.Count == 0) return result;

        var electricityAgreements = deletedParts
            .Where(p => IsElectricity(p.Key.DeliveryCategory))
            .Select(p => p.Key.InstAgreeNum)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var chunk in electricityAgreements.Chunk(ChunkSize))
        {
            var rows = await (
                from agreement in context.ExuAgreementTables
                join delpoint in context.ExuDelpoints
                    on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid, agreement.Deliverycategory }
                    equals new { delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid, delpoint.Deliverycategory }
                where agreement.Dataareaid == dataAreaId
                    && chunk.Contains(agreement.Agreementnum)
                    && agreement.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                select new
                {
                    agreement.Agreementnum,
                    agreement.Companyid,
                    agreement.Deliverycategory,
                    delpoint.Meteringpoint
                }
            ).Distinct().ToListAsync(cancellationToken);

            foreach (var group in rows.GroupBy(r => (MigrationConstants.DeliveryCategoryElectricity, CompanyKey(r.Companyid), r.Agreementnum)))
                result[group.Key] = group
                    .Select(r => r.Meteringpoint)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
        }

        var installationParts = deletedParts
            .Where(p => !IsElectricity(p.Key.DeliveryCategory))
            .ToList();
        var installationNums = installationParts
            .Select(p => p.Key.InstAgreeNum)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var knownInstallations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in installationNums.Chunk(ChunkSize))
        {
            var page = await context.ExuInstallations
                .Where(i => i.Dataareaid == dataAreaId && chunk.Contains(i.Instnum))
                .Select(i => i.Instnum)
                .Distinct()
                .ToListAsync(cancellationToken);
            knownInstallations.UnionWith(page);
        }

        foreach (var part in installationParts)
        {
            var key = part.Key;
            if (!knownInstallations.Contains(key.InstAgreeNum)) continue;
            result[LookupKey(key)] = [$"V{key.InstAgreeNum}01"];
        }

        return result;
    }

    /// <summary>
    /// The key both sides of the metering-point lookup agree on. The delivery category is normalised
    /// to the live constant for electricity, because the log writes "El-Ekstern" and the live tables
    /// write "El-ekstern" — a case-sensitive dictionary would never find the row.
    /// </summary>
    internal static (string DeliveryCategory, string CompanyId, string InstAgreeNum) LookupKey(ContractPartLogKey key)
    {
        var normalized = NormalizeKey(key);
        return (normalized.DeliveryCategory, normalized.CompanyId, normalized.InstAgreeNum);
    }

    /// <summary>Same normalisation for the part key itself, so a live-row key finds a logged one.</summary>
    internal static ContractPartLogKey NormalizeKey(ContractPartLogKey key)
    {
        var deliveryCategory =
            IsElectricity(key.DeliveryCategory) && key.DeliveryCategory != MigrationConstants.DeliveryCategoryElectricity
                ? MigrationConstants.DeliveryCategoryElectricity
                : key.DeliveryCategory;
        var companyId = CompanyKey(key.CompanyId);

        return deliveryCategory == key.DeliveryCategory && companyId == key.CompanyId
            ? key
            : key with { DeliveryCategory = deliveryCategory, CompanyId = companyId };
    }

    /// <summary>
    /// A company id folded to one casing, for use as a dictionary key. Xellent stores the same company
    /// in more than one casing and SQL Server does not care — dataarea 'hol' holds 371.982
    /// <c>EXU_CONTRACTPARTTABLE</c> rows with <c>COMPANYID</c> 'for' and 13.074 with 'For', and
    /// <c>EXU_CONTRACTPARTLOG</c> carries both spellings too. Every lookup that pairs a logged part
    /// with a live one is an ordinal dictionary, so without this an end date the batch overwrote is
    /// silently left overwritten whenever the two rows disagree on the case of one letter.
    /// </summary>
    internal static string CompanyKey(string? companyId) => companyId?.ToLowerInvariant() ?? string.Empty;

    private static bool IsElectricity(string deliveryCategory)
        => string.Equals(deliveryCategory, MigrationConstants.DeliveryCategoryElectricity, StringComparison.OrdinalIgnoreCase);
}
