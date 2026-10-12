using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_PRICEELEMENTTABLE", Schema = "dbo")]
public class ExuPriceElementTable
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("PARTYCHARGETYPEID")]
    [StringLength(10)]
    public string Partychargetypeid { get; set; } = null!;

    [Column("CHARGETYPECODE")]
    public int Chargetypecode { get; set; }

    [Column("DESCRIPTION")]
    [StringLength(50)]
    public string Description { get; set; } = null!;

    /// <summary>13-digit GLN of the party that issues this charge (typically the DSO).</summary>
    [Column("GRIDCOMPANYID", TypeName = "numeric(28, 0)")]
    public decimal Gridcompanyid { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
