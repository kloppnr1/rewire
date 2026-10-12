using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace TimeSeries.Xellent.IntegrationTests;

/// <summary>
/// Base class for Xellent live-DB integration tests. Builds a real SQL Server
/// <see cref="XellentDbContext"/> using the connection string in <c>testsettings.json</c>
/// (AXDB50_TEST on xel2012 via Integrated Security).
///
/// <para><b>Never runs in CI</b> — this project is intentionally not in
/// <c>NewSettlement.TimeSeriesMigration.slnx</c>, since the CI agent has no network path
/// to xel2012.</para>
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly XellentDbContext Context;
    protected readonly string ConnectionString;

    protected IntegrationTestBase()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("testsettings.json", optional: false)
            .Build();

        ConnectionString = configuration.GetConnectionString("TestConnection")
            ?? throw new InvalidOperationException("TestConnection not found in testsettings.json");

        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(ConnectionString, sqlOptions =>
            {
                sqlOptions.UseCompatibilityLevel(110);
                sqlOptions.CommandTimeout(120);
            })
            .Options;

        Context = new XellentDbContext(options);

        try
        {
            Context.Database.OpenConnection();
            Context.Database.CloseConnection();
        }
        catch (Exception ex) when (LooksLikeNetworkFailure(ex))
        {
            throw new InvalidOperationException(
                "Could not reach Xellent SQL Server (xel2012). The error pattern matches a missing VPN " +
                "connection — please connect to the corporate VPN and retry.\n\n" +
                $"Underlying error: {ex.GetBaseException().Message}", ex);
        }
    }

    private static bool LooksLikeNetworkFailure(Exception ex)
    {
        var msg = (ex.GetBaseException().Message ?? string.Empty).ToLowerInvariant();
        return msg.Contains("named pipe")
            || msg.Contains("network-related")
            || msg.Contains("netværksstien")
            || msg.Contains("network path")
            || msg.Contains("could not open a connection")
            || msg.Contains("not found or was not accessible");
    }

    public virtual void Dispose()
    {
        Context?.Dispose();
        GC.SuppressFinalize(this);
    }
}
