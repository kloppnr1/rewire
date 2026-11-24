using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("EMS_TIMESERIESVALUES", Schema = "dbo")]
public class EmsTimeseriesValues
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("TIMESERIESREFRECID")]
    public long Timeseriesrefrecid { get; set; }

    [Column("TIMEOFVALUE", TypeName = "datetime")]
    public DateTime Timeofvalue { get; set; }

    [Column("VALUE", TypeName = "numeric(28, 12)")]
    public decimal Value { get; set; }

    [Column("QUALITYOFVALUE")]
    public int Qualityofvalue { get; set; }

    [Column("VERSIONTOEXCL", TypeName = "datetime")]
    public DateTime Versiontoexcl { get; set; }
}
