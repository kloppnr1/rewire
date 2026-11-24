using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_PRICEELEMENTRATES", Schema = "dbo")]
public class ExuPriceElementRates
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("PARTYCHARGETYPEID")]
    [StringLength(10)]
    public string Partychargetypeid { get; set; } = null!;

    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("PRICE", TypeName = "numeric(28, 12)")]
    public decimal Price { get; set; }

    [Column("PRICE2_", TypeName = "numeric(28, 12)")] public decimal Price2 { get; set; }
    [Column("PRICE3_", TypeName = "numeric(28, 12)")] public decimal Price3 { get; set; }
    [Column("PRICE4_", TypeName = "numeric(28, 12)")] public decimal Price4 { get; set; }
    [Column("PRICE5_", TypeName = "numeric(28, 12)")] public decimal Price5 { get; set; }
    [Column("PRICE6_", TypeName = "numeric(28, 12)")] public decimal Price6 { get; set; }
    [Column("PRICE7_", TypeName = "numeric(28, 12)")] public decimal Price7 { get; set; }
    [Column("PRICE8_", TypeName = "numeric(28, 12)")] public decimal Price8 { get; set; }
    [Column("PRICE9_", TypeName = "numeric(28, 12)")] public decimal Price9 { get; set; }
    [Column("PRICE10_", TypeName = "numeric(28, 12)")] public decimal Price10 { get; set; }
    [Column("PRICE11_", TypeName = "numeric(28, 12)")] public decimal Price11 { get; set; }
    [Column("PRICE12_", TypeName = "numeric(28, 12)")] public decimal Price12 { get; set; }
    [Column("PRICE13_", TypeName = "numeric(28, 12)")] public decimal Price13 { get; set; }
    [Column("PRICE14_", TypeName = "numeric(28, 12)")] public decimal Price14 { get; set; }
    [Column("PRICE15_", TypeName = "numeric(28, 12)")] public decimal Price15 { get; set; }
    [Column("PRICE16_", TypeName = "numeric(28, 12)")] public decimal Price16 { get; set; }
    [Column("PRICE17_", TypeName = "numeric(28, 12)")] public decimal Price17 { get; set; }
    [Column("PRICE18_", TypeName = "numeric(28, 12)")] public decimal Price18 { get; set; }
    [Column("PRICE19_", TypeName = "numeric(28, 12)")] public decimal Price19 { get; set; }
    [Column("PRICE20_", TypeName = "numeric(28, 12)")] public decimal Price20 { get; set; }
    [Column("PRICE21_", TypeName = "numeric(28, 12)")] public decimal Price21 { get; set; }
    [Column("PRICE22_", TypeName = "numeric(28, 12)")] public decimal Price22 { get; set; }
    [Column("PRICE23_", TypeName = "numeric(28, 12)")] public decimal Price23 { get; set; }
    [Column("PRICE24_", TypeName = "numeric(28, 12)")] public decimal Price24 { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }

    /// <summary>Returns the rate for a 1-based hour (1 = 00:00–01:00, 24 = 23:00–24:00).</summary>
    public decimal GetPriceForHour(int hour) => hour switch
    {
        1 => Price, 2 => Price2, 3 => Price3, 4 => Price4, 5 => Price5, 6 => Price6,
        7 => Price7, 8 => Price8, 9 => Price9, 10 => Price10, 11 => Price11, 12 => Price12,
        13 => Price13, 14 => Price14, 15 => Price15, 16 => Price16, 17 => Price17, 18 => Price18,
        19 => Price19, 20 => Price20, 21 => Price21, 22 => Price22, 23 => Price23, 24 => Price24,
        _ => Price
    };
}
