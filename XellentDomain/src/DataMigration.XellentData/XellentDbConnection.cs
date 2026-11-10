using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DataMigration.XellentData;

/// <summary>
/// The one place a Xellent connection string becomes a database provider. In production it is
/// exactly <c>UseSqlServer(connectionString, sqlServerOptions)</c>.
///
/// <para>Tests can register a stand-in for a named connection string with
/// <see cref="OverrideForTests"/>, so code that only receives a connection string can run against a
/// seeded in-memory database. A connection string nobody registered always goes to SQL Server.</para>
/// </summary>
public static class XellentDbConnection
{
    private static readonly ConcurrentDictionary<string, Action<DbContextOptionsBuilder>> Overrides =
        new(StringComparer.Ordinal);

    public static DbContextOptionsBuilder<XellentDbContext> UseXellent(
        this DbContextOptionsBuilder<XellentDbContext> builder,
        string? connectionString,
        Action<SqlServerDbContextOptionsBuilder>? sqlServerOptions = null)
    {
        if (connectionString != null && Overrides.TryGetValue(connectionString, out var configure))
        {
            configure(builder);
            return builder;
        }
        return builder.UseSqlServer(connectionString, sqlServerOptions);
    }

    /// <summary>
    /// Sends every context built for <paramref name="connectionString"/> to the provider
    /// <paramref name="configure"/> sets up, until the returned handle is disposed.
    /// </summary>
    public static IDisposable OverrideForTests(string connectionString, Action<DbContextOptionsBuilder> configure)
    {
        Overrides[connectionString] = configure;
        return new Registration(connectionString);
    }

    private sealed class Registration(string connectionString) : IDisposable
    {
        public void Dispose() => Overrides.TryRemove(connectionString, out _);
    }
}
