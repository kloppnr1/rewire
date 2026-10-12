using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("EXU_DELPOINT2DELPOINTRELA25505", Schema = "dbo")]
public class ExuDelpoint2delpointrela25505
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("PRIMARYATTACHMENTNUM")]
    [StringLength(10)]
    public string Primaryattachmentnum { get; set; } = null!;

    [Column("PRIMARYDELPOINTNUM")]
    [StringLength(10)]
    public string Primarydelpointnum { get; set; } = null!;

    [Column("SECONDARYATTACHMENTNUM")]
    [StringLength(10)]
    public string Secondaryattachmentnum { get; set; } = null!;

    [Column("SECONDARYDELPOINTNUM")]
    [StringLength(10)]
    public string Secondarydelpointnum { get; set; } = null!;

    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Column("RELATIONTYPE")]
    public int Relationtype { get; set; }

    [Column("ATTACHMENT")]
    public int Attachment { get; set; }

    [Column("RECVERSION")]
    public int Recversion { get; set; }
}
