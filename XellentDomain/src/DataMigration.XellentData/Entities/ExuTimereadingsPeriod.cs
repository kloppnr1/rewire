using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Deliverycategory), nameof(Companyid), nameof(Attachmentnum), nameof(Delpointnum), nameof(Startdate))]
[Table("EXU_TIMEREADINGSPERIOD", Schema = "dbo")]
public class ExuTimereadingsPeriod
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Key]
    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Key]
    [Column("ATTACHMENTNUM")]
    [StringLength(10)]
    public string Attachmentnum { get; set; } = null!;

    [Key]
    [Column("DELPOINTNUM")]
    [StringLength(10)]
    public string Delpointnum { get; set; } = null!;

    [Key]
    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    /// <summary>0 = Hourly (time), 1 = Quarterly (kvarter)</summary>
    [Column("SERIESRESOLUTION")]
    public int Seriesresolution { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
