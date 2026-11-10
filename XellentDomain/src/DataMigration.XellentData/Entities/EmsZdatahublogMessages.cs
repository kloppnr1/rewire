using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

/// <summary>
/// Links a DataHub log (<see cref="Datahublogid"/> = <see cref="EmsZdatahublog.Id"/>) to the event that
/// carries the message (<see cref="Zeventqueuerefrecid"/> = <see cref="ExuZeventqueueDataTable.Zeventqueuerefrecid"/>).
/// </summary>
[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("EMS_ZDATAHUBLOGMESSAGES", Schema = "dbo")]
public class EmsZdatahublogMessages
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("DATAHUBLOGID")]
    [StringLength(10)]
    public string Datahublogid { get; set; } = null!;

    [Column("MESSAGEDIRECTION")]
    public int Messagedirection { get; set; }

    [Column("ZEVENTQUEUEREFRECID")]
    public long Zeventqueuerefrecid { get; set; }

    [Column("MESSAGEOUTREFRECID")]
    public long Messageoutrefrecid { get; set; }

    [Column("RECVERSION")]
    public int Recversion { get; set; }

    [Column("DCSCENARIODETAILSID")]
    [StringLength(40)]
    public string Dcscenariodetailsid { get; set; } = null!;

    [Column("DCMESSAGEID")]
    [StringLength(40)]
    public string Dcmessageid { get; set; } = null!;
}
