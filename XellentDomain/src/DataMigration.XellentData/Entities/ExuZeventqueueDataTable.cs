using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

/// <summary>
/// The payload of a queued event. For DataHub messages <see cref="Xmldata"/> holds the DataHub XML.
/// </summary>
[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("EXU_ZEVENTQUEUEDATATABLE", Schema = "dbo")]
public class ExuZeventqueueDataTable
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("ZEVENTQUEUEREFRECID")]
    public long Zeventqueuerefrecid { get; set; }

    [Column("XMLDATA", TypeName = "ntext")]
    public string? Xmldata { get; set; }

    [Column("RECVERSION")]
    public int Recversion { get; set; }
}
