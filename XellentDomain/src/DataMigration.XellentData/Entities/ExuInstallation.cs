using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("EXU_INSTALLATION", Schema = "dbo")]
public class ExuInstallation
{
    [Column("INSTNUM")]
    [StringLength(20)]
    public string Instnum { get; set; } = null!;

    [Column("DATAAREAID")]
    [StringLength(8)]
    public string Dataareaid { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }

    [Column("ZIPCODEID")]
    [StringLength(10)]
    public string Zipcodeid { get; set; } = null!;

    [Column("INSTADDRESSFORMATTED")]
    [StringLength(250)]
    public string Instaddressformatted { get; set; } = null!;

    [Column("ADDRESSID")]
    [StringLength(36)]
    public string? Addressid { get; set; }
}
