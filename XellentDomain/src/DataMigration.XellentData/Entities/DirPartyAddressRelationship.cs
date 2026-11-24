using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("DIRPARTYADDRESSRELATIONSHIP", Schema = "dbo")]
public class DirPartyAddressRelationship
{
    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("PARTYID")]
    [StringLength(20)]
    public string Partyid { get; set; } = null!;

    [Column("ISPRIMARY")]
    public int Isprimary { get; set; }

    [Column("VALIDTODATETIME")]
    public DateTime Validtodatetime { get; set; }
}
