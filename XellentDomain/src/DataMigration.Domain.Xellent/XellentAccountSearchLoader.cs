using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// One-call entry point for <see cref="XellentAccountSearch"/>. Owns the
/// <see cref="XellentDbContext"/> lifecycle and SQL Server connection setup —
/// consumers pass a connection string and receive a fully materialized
/// <see cref="XellentAccountSearchResult"/>. Mirrors <see cref="XellentDomainLoader"/>.
/// </summary>
public static class XellentAccountSearchLoader
{
    private const int SqlCompatibilityLevel = 110;

    public static async Task<XellentAccountSearchResult> SearchAsync(
        string connectionString,
        XellentAccountSearchCriteria criteria,
        XellentAccountSearchOptions options,
        IWarningCollector warnings,
        CancellationToken cancellationToken = default)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                sql.CommandTimeout(options.CommandTimeoutSeconds);
            })
            .Options;

        await using var context = new XellentDbContext(dbOptions);

        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Cannot connect to Xellent database. Check VPN connection and that the " +
                "connection string is reachable from this machine.");
        }

        var search = new XellentAccountSearch(context, warnings, options);
        return await search.SearchAsync(criteria, cancellationToken);
    }
}
