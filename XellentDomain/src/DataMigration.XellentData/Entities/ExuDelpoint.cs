using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Deliverycategory), nameof(Companyid), nameof(Attachmentnum), nameof(Num))]
[Table("EXU_DELPOINT", Schema = "dbo")]
public class ExuDelpoint
{
    [Key]
    [Column("NUM")]
    [StringLength(10)]
    public string Num { get; set; } = null!;

    [Column("DEL_PARENTDELPOINTNUM")]
    [StringLength(10)]
    public string? DelParentdelpointnum { get; set; }

    [Column("EXTNUM")]
    [StringLength(18)]
    public string Extnum { get; set; } = null!;

    [Column("NAME")]
    [StringLength(60)]
    public string Name { get; set; } = null!;

    [Key]
    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Key]
    [Column("ATTACHMENTNUM")]
    [StringLength(10)]
    public string Attachmentnum { get; set; } = null!;

    [Key]
    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("CUSTACCOUNT")]
    [StringLength(25)]
    public string Custaccount { get; set; } = null!;

    [Column("STREETNUM")]
    [StringLength(10)]
    public string Streetnum { get; set; } = null!;

    [Column("COUNTYNUM")]
    [StringLength(10)]
    public string Countynum { get; set; } = null!;

    [Column("ZIPCODEID")]
    [StringLength(10)]
    public string Zipcodeid { get; set; } = null!;

    [Column("METERINGPOINT")]
    [StringLength(30)]
    public string Meteringpoint { get; set; } = null!;

    [Column("ADDRESSSTREET")]
    [StringLength(132)]
    public string Addressstreet { get; set; } = null!;

    [Column("GSRN")]
    [StringLength(18)]
    public string Gsrn { get; set; } = null!;

    [Column("HOUSENUMSTART")]
    public int? Housenumstart { get; set; }

    [Column("HOUSELETTERSTART")]
    [StringLength(1)]
    public string Houseletterstart { get; set; } = null!;

    [Column("APARTMENT")]
    [StringLength(10)]
    public string Apartment { get; set; } = null!;

    [Column("FLOOR")]
    [StringLength(10)]
    public string Floor { get; set; } = null!;

    [Column("ADDRLOCATION")]
    [StringLength(35)]
    public string Addrlocation { get; set; } = null!;

    [Column("ADDRESSID")]
    [StringLength(36)]
    public string Addressid { get; set; } = null!;

    [Column("EMS_NETAREA")]
    [StringLength(10)]
    public string EmsNetarea { get; set; } = null!;

    [Column("POWEREXCHANGEAREA")]
    [StringLength(10)]
    public string Powerexchangearea { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("CONNECTIONTYPE")]
    public int? Connectiontype { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
