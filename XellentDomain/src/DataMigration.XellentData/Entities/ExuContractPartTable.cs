using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Contractnum), nameof(Instagreenum), nameof(Deliverycategory), nameof(Startdate), nameof(Companyid), nameof(Productnum), nameof(Amrcode), nameof(Contractpartsubreferencetype), nameof(Contractpartsubreferenceid))]
[Table("EXU_CONTRACTPARTTABLE", Schema = "dbo")]
public class ExuContractPartTable
{
    [Key]
    [Column("CONTRACTNUM")]
    [StringLength(10)]
    public string Contractnum { get; set; } = null!;

    [Key]
    [Column("INSTAGREENUM")]
    [StringLength(10)]
    public string Instagreenum { get; set; } = null!;

    [Key]
    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Key]
    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Key]
    [Column("PRODUCTNUM")]
    [StringLength(10)]
    public string Productnum { get; set; } = null!;

    [Key]
    [Column("STARTDATE", TypeName = "datetime")]
    public DateTime Startdate { get; set; }

    [Column("ENDDATE", TypeName = "datetime")]
    public DateTime Enddate { get; set; }

    [Key]
    [Column("AMRCODE")]
    [StringLength(10)]
    public string Amrcode { get; set; } = null!;

    [Key]
    [Column("CONTRACTPARTSUBREFERENCETYPE")]
    public int Contractpartsubreferencetype { get; set; }

    [Key]
    [Column("CONTRACTPARTSUBREFERENCEID")]
    [StringLength(35)]
    public string Contractpartsubreferenceid { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }

    [Column("DELIVERYGROUPNUM_RATECODE")]
    [StringLength(20)]
    public string? Deliverygroupnumratecode { get; set; }
}
