namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// Whether a metering point is currently physically connected to its grid/installation.
/// </summary>
public enum ConnectionStatus
{
    Connected,
    Disconnected
}
