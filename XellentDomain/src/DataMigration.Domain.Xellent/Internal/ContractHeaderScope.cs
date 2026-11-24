using DataMigration.Core;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// The one definition of "which contract headers this run may see". Every query that gates on
/// <c>CONTRACTENDDATE</c> takes its contracts from here instead of from the DbSet, so the rule — and
/// the exemption in <see cref="ContractHeaderExemption"/> — cannot drift between them.
/// </summary>
internal static class ContractHeaderScope
{
    /// <summary>
    /// Contracts inside the run's date window. With no exempted contracts the predicate is exactly
    /// what every call site spelled out before, so the emitted SQL is unchanged when the restore is
    /// off — and equally when it is on but this particular chunk has nothing exempted in it.
    /// </summary>
    /// <param name="includeEndedCutoff">
    /// The header cutoff, and only ever that. It is <see cref="MapperOptions.IncludeEndedCutoff"/>,
    /// never <see cref="MapperOptions.IncludeEndedCutoffForParts"/> — see that property's note.
    /// Pass null where a call site deliberately honours no cutoff.
    /// </param>
    /// <param name="exemptedContractNums">
    /// The exempted contracts relevant to <i>this</i> query's chunk, from
    /// <see cref="ContractHeaderExemption.ForAccounts"/> or
    /// <see cref="ContractHeaderExemption.ForContracts"/>. Null when the restore is off or the chunk
    /// has none. Deliberately a list of names and not a subquery: see the exemption's remarks.
    /// </param>
    public static IQueryable<ExuContractTable> InDateWindow(
        XellentDbContext context,
        DateTime today,
        DateTime? includeEndedCutoff,
        string[]? exemptedContractNums)
    {
        var endDateNull = MigrationConstants.EndDateNull;

        if (exemptedContractNums is null || exemptedContractNums.Length == 0)
            return context.ExuContractTables.Where(c =>
                c.Contractenddate == endDateNull
                || c.Contractenddate >= today
                || (includeEndedCutoff != null && c.Contractenddate >= includeEndedCutoff));

        return context.ExuContractTables.Where(c =>
            c.Contractenddate == endDateNull
            || c.Contractenddate >= today
            || (includeEndedCutoff != null && c.Contractenddate >= includeEndedCutoff)
            || exemptedContractNums.Contains(c.Contractnum));
    }
}
