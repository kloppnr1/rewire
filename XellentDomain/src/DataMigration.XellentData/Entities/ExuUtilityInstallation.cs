using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("EXU_UTILITYINSTALLATION", Schema = "dbo")]
public class ExuUtilityInstallation
{
    [Column("INSTNUM")]
    [StringLength(10)]
    public string Instnum { get; set; } = null!;

    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("TECHNICGROUP1")]
    [StringLength(20)]
    public string? Technicgroup1 { get; set; }
}
