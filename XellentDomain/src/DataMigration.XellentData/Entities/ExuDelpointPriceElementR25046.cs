using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Deliverycategory), nameof(Companyid), nameof(Attachmentnum), nameof(Delpointnum), nameof(Partychargetypeid))]
[Table("EXU_DELPOINTPRICEELEMENTR25046", Schema = "dbo")]
public class ExuDelpointPriceElementR25046
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
    [Column("PARTYCHARGETYPEID")]
    [StringLength(20)]
    public string Partychargetypeid { get; set; } = null!;

    [Column("CHARGETYPECODE")]
    public int Chargetypecode { get; set; }

    [Column("RELATIONVALIDFROM")]
    public DateTime Relationvalidfrom { get; set; }

    [Column("RELATIONVALIDTO")]
    public DateTime Relationvalidto { get; set; }

    [Column("VALIDINMARKETFROM")]
    public DateTime Validinmarketfrom { get; set; }

    [Column("VALIDINMARKETTOEXCL")]
    public DateTime Validinmarkettoexcl { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
