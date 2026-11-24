using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Deliverycategory), nameof(Companyid), nameof(Agreementnum))]
[Table("EXU_AGREEMENTTABLE", Schema = "dbo")]
public class ExuAgreementTable
{
    [Key]
    [Column("AGREEMENTNUM")]
    [StringLength(10)]
    public string Agreementnum { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Key]
    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Column("INSTBILLINGNAME")]
    [StringLength(60)]
    public string? Instbillingname { get; set; }

    [Column("STREET")]
    [StringLength(250)]
    public string? Street { get; set; }

    [Column("STREETNUM")]
    [StringLength(10)]
    public string? Streetnum { get; set; }

    [Column("HOUSENUMSTART")]
    public int? Housenumstart { get; set; }

    [Column("HOUSELETTERSTART")]
    [StringLength(1)]
    public string? Houseletterstart { get; set; }

    [Column("FLOOR")]
    [StringLength(10)]
    public string? Floor { get; set; }

    [Column("APARTMENT")]
    [StringLength(10)]
    public string? Apartment { get; set; }

    [Column("ZIPCODEID")]
    [StringLength(10)]
    public string? Zipcodeid { get; set; }

    [Column("CITY")]
    [StringLength(60)]
    public string? City { get; set; }

    [Column("ADDRLOCATION")]
    [StringLength(35)]
    public string? Addrlocation { get; set; }

    [Column("COUNTYCODE")]
    [StringLength(10)]
    public string? Countycode { get; set; }

    [Column("COUNTRY")]
    [StringLength(10)]
    public string? Country { get; set; }

    /// <summary>
    /// Agreement status, AX base enum EMS_AgreementStatusebIX (labels in JOPED_Enums).
    /// 5 = "Leverance ophørt".
    /// </summary>
    [Column("EMS_AGREEMENTSTATUSEBIX")]
    public int EmsAgreementstatusebix { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
