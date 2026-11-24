using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Companyid), nameof(Deliverycategory), nameof(Meteringpoint), nameof(Contacttype), nameof(Startdate))]
[Table("EXU_DELPOINTCONTACTINFOTABLE", Schema = "dbo")]
public class ExuDelpointContactInfoTable
{
    [Key]
    [Column("METERINGPOINT")]
    [StringLength(30)]
    public string Meteringpoint { get; set; } = null!;

    [Key]
    [Column("CONTACTTYPE")]
    public int Contacttype { get; set; }

    [Key]
    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Key]
    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Key]
    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Column("STREETNAME")]
    [StringLength(132)]
    public string Streetname { get; set; } = null!;

    [Column("HOUSENUMBER")]
    [StringLength(10)]
    public string Housenumber { get; set; } = null!;

    [Column("APARTMENT")]
    [StringLength(10)]
    public string Apartment { get; set; } = null!;

    [Column("FLOOR")]
    [StringLength(10)]
    public string Floor { get; set; } = null!;

    [Column("ZIPCODE")]
    [StringLength(10)]
    public string Zipcode { get; set; } = null!;

    [Column("ADDRESSID")]
    [StringLength(20)]
    public string Addressid { get; set; } = null!;

    [Column("NAME1")]
    [StringLength(60)]
    public string Name1 { get; set; } = null!;

    [Column("NAME2")]
    [StringLength(60)]
    public string Name2 { get; set; } = null!;

    [Column("PHONE")]
    [StringLength(132)]
    public string? Phone { get; set; }

    [Column("MOBILE")]
    [StringLength(132)]
    public string? Mobile { get; set; }

    [Column("EMAIL")]
    [StringLength(132)]
    public string? Email { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
