using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_PRICEELEMENTCHECKDATA", Schema = "dbo")]
public class ExuPriceElementCheckData
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("PRICEELEMENTCHECKREFRECID")]
    public long Priceelementcheckrefrecid { get; set; }

    [Column("PARTYCHARGETYPEID")]
    [StringLength(10)]
    public string Partychargetypeid { get; set; } = null!;

    [Column("CHARGETYPECODE")]
    public int Chargetypecode { get; set; }

    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
