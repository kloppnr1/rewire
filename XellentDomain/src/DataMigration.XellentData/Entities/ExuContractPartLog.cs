using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("EXU_CONTRACTPARTLOG", Schema = "dbo")]
public class ExuContractPartLog
{
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("CONTRACTNUM")]
    [StringLength(10)]
    public string Contractnum { get; set; } = null!;

    [Column("INSTAGREENUM")]
    [StringLength(10)]
    public string Instagreenum { get; set; } = null!;

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("AMRCODE")]
    [StringLength(10)]
    public string Amrcode { get; set; } = null!;

    [Column("PRODUCTNUM")]
    [StringLength(10)]
    public string Productnum { get; set; } = null!;

    [Column("CONTRACTPARTSTARTDATE", TypeName = "datetime")]
    public DateTime Contractpartstartdate { get; set; }

    [Column("CONTRACTPARTENDDATE", TypeName = "datetime")]
    public DateTime Contractpartenddate { get; set; }

    [Column("DELIVERYGROUPNUM_RATECODE")]
    [StringLength(20)]
    public string? Deliverygroupnumratecode { get; set; }

    /// <summary>
    /// The change type, in Danish, written by Xellent — "Slutdato er ændret", "Kontraktdel er
    /// slettet", "Startdato er ændret", or a one-time-charge text carrying the charge's name.
    /// The column is named ERRORMSG but holds no error.
    /// </summary>
    [Column("ERRORMSG")]
    [StringLength(250)]
    public string? Errormsg { get; set; }

    /// <summary>Who made the change. Blank when an unattended job did it.</summary>
    [Column("LOGEMPLID")]
    [StringLength(25)]
    public string? Logemplid { get; set; }

    [Column("LOGDATETIME", TypeName = "datetime")]
    public DateTime Logdatetime { get; set; }
}
