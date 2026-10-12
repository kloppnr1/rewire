using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[Keyless]
[Table("EXU_FLEXBHISTORYLINE", Schema = "dbo")]
public class ExuFlexBhistoryLine
{
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("HISTKEYNUMBER")]
    [StringLength(10)]
    public string Histkeynumber { get; set; } = null!;

    [Column("DATETIME24HOUR", TypeName = "datetime")]
    public DateTime Datetime24hour { get; set; }

    [Column("TIMEVALUE", TypeName = "numeric(28, 12)")]
    public decimal Timevalue { get; set; }

    [Column("POWEREXCHANGEPRICE", TypeName = "numeric(28, 12)")]
    public decimal Powerexchangeprice { get; set; }

    [Column("CALCULATEDPRICE", TypeName = "numeric(28, 12)")]
    public decimal Calculatedprice { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
