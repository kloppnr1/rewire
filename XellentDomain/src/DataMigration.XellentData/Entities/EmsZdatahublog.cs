using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

/// <summary>
/// One DataHub message exchange for a metering point. <see cref="Id"/> is what
/// <see cref="EmsZdatahublogMessages.Datahublogid"/> points back to.
/// </summary>
[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("EMS_ZDATAHUBLOG", Schema = "dbo")]
public class EmsZdatahublog
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("ID")]
    [StringLength(10)]
    public string Id { get; set; } = null!;

    /// <summary>1 = incoming (from DataHub).</summary>
    [Column("MESSAGEDIRECTION")]
    public int Messagedirection { get; set; }

    [Column("MESSAGEOUTTYPE")]
    public int Messageouttype { get; set; }

    [Column("MESSAGEGROUP")]
    public int Messagegroup { get; set; }

    [Column("MESSAGESTATUS")]
    public int Messagestatus { get; set; }

    [Column("CLIENTTYPE")]
    public int Clienttype { get; set; }

    [Column("MESSAGEINTYPE")]
    public int Messageintype { get; set; }

    [Column("ZEBIXSCENARIOID")]
    public long Zebixscenarioid { get; set; }

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("METERINGPOINT")]
    [StringLength(30)]
    public string Meteringpoint { get; set; } = null!;

    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("EANNUMBER", TypeName = "numeric(28, 12)")]
    public decimal Eannumber { get; set; }

    [Column("TIMESERIESREFRECID")]
    public long Timeseriesrefrecid { get; set; }

    [Column("CREATEDDATETIME", TypeName = "datetime")]
    public DateTime Createddatetime { get; set; }

    [Column("CREATEDBY")]
    [StringLength(5)]
    public string Createdby { get; set; } = null!;

    [Column("RECVERSION")]
    public int Recversion { get; set; }

    [Column("ORIGINALBUSINESSREFERENCEID")]
    [StringLength(40)]
    public string Originalbusinessreferenceid { get; set; } = null!;
}
