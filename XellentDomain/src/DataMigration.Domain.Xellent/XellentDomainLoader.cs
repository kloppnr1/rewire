using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// One-call entry point for loading domain data from Xellent. Owns the
/// <see cref="XellentDbContext"/> lifecycle and SQL Server connection setup — consumers
/// pass a connection string and receive a fully materialized <see cref="XellentLoadResult"/>.
///
/// <para>Use this instead of constructing <see cref="XellentDomainMapper"/> directly;
/// the mapper is exposed for integration tests that already manage their own
/// <see cref="XellentDbContext"/>.</para>
/// </summary>
public static class XellentDomainLoader
{
    private const int SqlCompatibilityLevel = 110;
    private const int CommandTimeoutSeconds = 120;

    /// <summary>
    /// Connects to Xellent, loads the requested accounts via <see cref="XellentDomainMapper"/>,
    /// and disposes the DbContext. Throws with a VPN-friendly hint when the database is
    /// unreachable.
    ///
    /// <para><paramref name="accounts"/> may be null to load everything the mapper's commodity
    /// and reference date select in the data area — see <see cref="XellentDomainMapper.LoadAsync"/>.</para>
    /// </summary>
    public static async Task<XellentLoadResult> LoadAsync(
        string connectionString,
        MapperOptions options,
        IWarningCollector warnings,
        IEnumerable<string>? accounts,
        string dataAreaId = "hol",
        CancellationToken cancellationToken = default)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                sql.CommandTimeout(CommandTimeoutSeconds);
            })
            .Options;

        await using var context = new XellentDbContext(dbOptions);

        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Cannot connect to Xellent database. Check VPN connection and that the " +
                "connection string is reachable from this machine.");
        }

        var mapper = new XellentDomainMapper(context, warnings, options, dataAreaId);
        return await mapper.LoadAsync(accounts, cancellationToken);
    }
}
