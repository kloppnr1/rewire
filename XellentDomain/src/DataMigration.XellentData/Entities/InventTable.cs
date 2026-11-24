using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("INVENTTABLE", Schema = "dbo")]
public class InventTable
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("ITEMID")]
    [StringLength(20)]
    public string Itemid { get; set; } = null!;

    [Column("ITEMTYPE")]
    public int Itemtype { get; set; }

    [Column("EXU_USERATEFROMFLEXPRICING")]
    public int ExuUserateFromFlexPricing { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
