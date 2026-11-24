using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_PRODUCTEXTENDTABLE", Schema = "dbo")]
public class ExuProductExtendTable
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("PRODUCTNUM")]
    [StringLength(20)]
    public string Productnum { get; set; } = null!;

    [Column("PRODUCTTYPE")]
    [StringLength(20)]
    public string Producttype { get; set; } = null!;

    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
