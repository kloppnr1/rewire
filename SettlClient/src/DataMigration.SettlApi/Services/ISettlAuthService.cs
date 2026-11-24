namespace DataMigration.SettlApi.Services;

public interface ISettlAuthService
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    void InvalidateToken();
}
