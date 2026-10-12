namespace DataMigration.OpsCli.Services;

/// <summary>Existing AboRabat source restoration, shared without changing its rules.</summary>
internal static class ContractPartHistoryRestoration
{
    internal const string DeletedPartChangeType = "Kontraktdel er slettet";
    internal const string EndChangedChangeType = "Slutdato er ændret";
    internal const string StartChangedChangeType = "Startdato er ændret";
    private static readonly DateTime ChargerBatchFrom = new(2026, 9, 4, 12, 53, 27);
    private static readonly DateTime ChargerBatchThrough = new(2026, 9, 4, 13, 35, 43);

    internal static bool IsChargingMigrationWindow(DateTime? loggedAt) =>
        loggedAt >= ChargerBatchFrom && loggedAt <= ChargerBatchThrough;
    internal static bool IsChargingMigrationChange(DateTime? loggedAt, string? employee) =>
        IsChargingMigrationWindow(loggedAt) && employee == "Xellent";

    internal sealed record RestoredParts<T, TKey>(List<T> Parts, HashSet<TKey> Ambiguous,
        Dictionary<TKey, T> Evidence)
        where TKey : notnull;

    internal static RestoredParts<T, TKey> RestoreParts<T, TKey>(
        IReadOnlyCollection<T> live, IReadOnlyCollection<T> logs, Func<T, TKey> key,
        Func<T, string?> changeType, Func<T, DateTime?> loggedAt,
        Func<T, long?> recordId, Func<T, DateTime> end,
        Func<T, DateTime> start, Func<T, DateTime, T> withEnd,
        Func<T, bool> isMigrationChange, DateTime historyCutoff)
        where TKey : notnull
    {
        var parts = new List<T>();
        var ambiguous = new HashSet<TKey>();
        var evidence = new Dictionary<TKey, T>();
        var liveByKey = live.GroupBy(key).ToDictionary(g => g.Key, g => g.ToList());
        var logsByKey = logs.GroupBy(key).ToDictionary(g => g.Key, g => g.ToList());
        // A bounded migration deletion can restore a missing part. Other history
        // can explain a current part's end, but cannot resurrect a deletion.
        foreach (var (partKey, history) in logsByKey)
            if (history.Any(p => IsBatch(p) && changeType(p) == StartChangedChangeType))
                ambiguous.Add(partKey);
        foreach (var (partKey, current) in liveByKey)
        {
            if (current.Count != 1) ambiguous.Add(partKey);
            var firstChange = FirstChange(partKey);
            var invalidEnd = firstChange is not null
                && changeType(firstChange) == EndChangedChangeType
                && end(firstChange).Year is not (1900 or >= 9999)
                && end(firstChange) < start(firstChange);
            evidence[partKey] = firstChange is not null && changeType(firstChange) == EndChangedChangeType
                ? firstChange : current[0];
            if (firstChange is not null && changeType(firstChange) is StartChangedChangeType or DeletedPartChangeType)
                ambiguous.Add(partKey);
            if (invalidEnd) ambiguous.Add(partKey);
            parts.AddRange(current.Select(p => firstChange is not null
                    && changeType(firstChange) == EndChangedChangeType && !invalidEnd
                    ? withEnd(p, end(firstChange)) : p));
        }
        foreach (var (partKey, history) in logsByKey)
        {
            if (liveByKey.ContainsKey(partKey)) continue;
            var migrationHistory = history.Where(IsBatch)
                .OrderBy(p => loggedAt(p)).ThenBy(p => recordId(p)).ToList();
            var deletion = migrationHistory.FirstOrDefault(p => changeType(p) == DeletedPartChangeType);
            if (deletion is null) continue;
            var firstChange = migrationHistory[0];
            if (changeType(firstChange) is not (EndChangedChangeType or DeletedPartChangeType)
                || (end(firstChange).Year is not (1900 or >= 9999)
                    && end(firstChange) < start(firstChange)))
            {
                ambiguous.Add(partKey);
                continue;
            }
            parts.Add(withEnd(deletion, end(firstChange)));
            evidence[partKey] = firstChange;
        }
        return new RestoredParts<T, TKey>(parts, ambiguous, evidence);

        bool IsBatch(T row) => isMigrationChange(row);

        T? FirstChange(TKey partKey)
        {
            if (!logsByKey.TryGetValue(partKey, out var history)) return default;
            var batch = history.Where(IsBatch)
                .OrderBy(p => loggedAt(p)).ThenBy(p => recordId(p)).FirstOrDefault();
            if (batch is not null) return batch;
            return history.Where(p => loggedAt(p) >= historyCutoff
                    && !IsBatch(p)
                    && changeType(p) is EndChangedChangeType or DeletedPartChangeType or StartChangedChangeType)
                .OrderBy(p => loggedAt(p)).ThenBy(p => recordId(p)).FirstOrDefault();
        }
    }

}
