using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_PRICEELEMENTCHECK", Schema = "dbo")]
public class ExuPriceElementCheck
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("METERINGPOINTID")]
    [StringLength(30)]
    public string Meteringpointid { get; set; } = null!;

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("CUSTACCOUNT")]
    [StringLength(25)]
    public string Custaccount { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
