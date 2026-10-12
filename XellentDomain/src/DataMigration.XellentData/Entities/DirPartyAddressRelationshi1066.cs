using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("DIRPARTYADDRESSRELATIONSHI1066", Schema = "dbo")]
public class DirPartyAddressRelationshi1066
{
    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("ADDRESSRECID")]
    public long Addressrecid { get; set; }

    [Column("PARTYADDRESSRELATIONSHIPRECID")]
    public long Partyaddressrelationshiprecid { get; set; }
}
