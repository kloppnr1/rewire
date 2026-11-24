using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Reads the charger's own Charge_Abo product at the electricity metering point's COS-1 date.
/// These parts were end-dated at migration, so a present-day Xellent load cannot answer for them.
/// Account and date are paired: pooling products across dates could turn a real ownership change
/// into a false conflict or assign the later owner to the earlier delivery.
/// </summary>
public static class XellentChargeSubscriptionReader
{
    public static async Task<IReadOnlyDictionary<(string Account, DateOnly Date), IReadOnlyList<string>>> ReadAsync(
        string connectionString,
        IReadOnlyCollection<(string Account, DateOnly Date)> requests,
        string dataAreaId,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<(string Account, DateOnly Date), IReadOnlyList<string>>();
        if (requests.Count == 0) return result;

        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseXellent(connectionString, sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(options);

        foreach (var dateGroup in requests.Distinct().GroupBy(r => r.Date))
        {
            var at = dateGroup.Key.ToDateTime(TimeOnly.MinValue);
            foreach (var chunk in dateGroup.Select(r => r.Account).Distinct(StringComparer.Ordinal).Chunk(1000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = await (
                    from contract in context.ExuContractTables
                    join part in context.ExuContractPartTables
                        on new { contract.Contractnum, contract.Dataareaid }
                        equals new { part.Contractnum, part.Dataareaid }
                    where contract.Dataareaid == dataAreaId
                        && chunk.Contains(contract.Custaccount)
                        && part.Deliverycategory == "Charge_Abo"
                        && part.Startdate <= at
                        && (part.Enddate == MigrationConstants.EndDateNull || part.Enddate >= at)
                    select new { contract.Custaccount, part.Productnum }
                ).Distinct().ToListAsync(cancellationToken);

                foreach (var group in rows.GroupBy(r => r.Custaccount, StringComparer.Ordinal))
                    result[(group.Key, dateGroup.Key)] = group
                        .Select(r => r.Productnum)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList();
            }
        }

        return result;
    }

    /// <summary>Email decision of 23 September: Eje wins over Leje; a successful read
    /// with no usable ownership returns Privat. Callers must never call this after a failed read.</summary>
    public static string SegmentFor(IEnumerable<string> products)
    {
        var codes = products.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (codes.Any(OwnedProducts.Contains)) return "Privat_Opladning_Eje";
        if (codes.Any(RentedProducts.Contains)) return "Privat_Opladning_Leje";
        return "Privat";
    }

    private static readonly IReadOnlySet<string> OwnedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OAboEjeMEL", "OAboEje0", "OAboEjeUEL", "O HCEjeML",
        "O HCEjeUL3", "O HCEjeUL4", "O HCEjeUL5"
    };

    private static readonly IReadOnlySet<string> RentedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OAboLejMEL", "Gl.CLejMEL", "NY.CLejMEL", "O HCLejML"
    };
}
