using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Journalid), nameof(Linenum))]
[Table("EXU_METERJOURNALTRANS", Schema = "dbo")]
public class ExuMeterJournalTrans
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("JOURNALID")]
    [StringLength(20)]
    public string Journalid { get; set; } = null!;

    [Key]
    [Column("LINENUM")]
    public decimal Linenum { get; set; }

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("INSTALLATIONNUM")]
    [StringLength(20)]
    public string Installationnum { get; set; } = null!;

    [Column("METERNUM")]
    [StringLength(30)]
    public string Meternum { get; set; } = null!;

    [Column("TRANSDATE", TypeName = "datetime")]
    public DateTime Transdate { get; set; }

    [Column("JOURNALTYPE")]
    public int Journaltype { get; set; }

    [Column("POSTED")]
    public int Posted { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
