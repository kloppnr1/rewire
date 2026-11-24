using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("DIRPARTYECOMMUNICATIONRELA2608", Schema = "dbo")]
public class DirPartyECommunicationRela2608
{
    [Column("PARTYID")]
    [StringLength(20)]
    public string Partyid { get; set; } = null!;

    [Column("STATUS")]
    public int Status { get; set; }

    [Column("PRIORITY")]
    public int Priority { get; set; }

    [Column("VALUESRECID")]
    public long Valuesrecid { get; set; }

    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
