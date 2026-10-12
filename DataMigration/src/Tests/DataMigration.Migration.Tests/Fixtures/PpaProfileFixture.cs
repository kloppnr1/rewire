namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Shared fixture that runs the full ppa-customers profile once.
/// All PPA group test classes share this via IClassFixture.
/// </summary>
public class PpaProfileFixture : IDisposable
{
    public ProfileTestRunner Runner { get; }

    public PpaProfileFixture()
    {
        Runner = new ProfileTestRunner("ppa-customers");
    }

    public void Dispose() => Runner.Dispose();
}
