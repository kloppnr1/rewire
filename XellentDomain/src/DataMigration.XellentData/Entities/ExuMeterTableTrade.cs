using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Deliverycategory), nameof(Companyid), nameof(Agreementnum), nameof(Delpointnum), nameof(Startdate))]
[Table("EXU_METERTABLETRADE", Schema = "dbo")]
public class ExuMeterTableTrade
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
    [Column("AGREEMENTNUM")]
    [StringLength(10)]
    public string Agreementnum { get; set; } = null!;

    [Key]
    [Column("DELPOINTNUM")]
    [StringLength(10)]
    public string Delpointnum { get; set; } = null!;

    [Key]
    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Column("METERNUM")]
    [StringLength(30)]
    public string Meternum { get; set; } = null!;

    [Column("COUNTERCONSTANT")]
    public decimal Counterconstant { get; set; }

    [Column("METERINGPOINTDHSUBTYPE")]
    public int MeteringPointDhSubType { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
