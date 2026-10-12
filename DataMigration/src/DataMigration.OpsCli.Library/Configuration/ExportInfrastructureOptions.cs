namespace DataMigration.OpsCli.Configuration;

public class ExportInfrastructureOptions
{
    public string MigrationStatePath { get; set; } = "./data/migration-state.json";
    public string OutputBaseDirectory { get; set; } = "./output";
}
