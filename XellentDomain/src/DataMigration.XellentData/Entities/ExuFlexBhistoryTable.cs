using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_FLEXBHISTORYTABLE", Schema = "dbo")]
public class ExuFlexBhistoryTable
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("METERINGPOINT")]
    [StringLength(30)]
    public string Meteringpoint { get; set; } = null!;

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("HISTKEYNUMBER")]
    [StringLength(10)]
    public string Histkeynumber { get; set; } = null!;

    [Column("REQSTARTDATE", TypeName = "datetime")]
    public DateTime Reqstartdate { get; set; }

    [Column("REQENDDATE", TypeName = "datetime")]
    public DateTime Reqenddate { get; set; }

    [Column("BILLINGLOGNUM")]
    [StringLength(10)]
    public string Billinglognum { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
