using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("DIRECOMMUNICATIONADDRESS", Schema = "dbo")]
public class DirECommunicationAddress
{
    [Column("ECOMMUNICATIONTYPEID")]
    [StringLength(20)]
    public string Ecommunicationtypeid { get; set; } = null!;

    [Column("URL")]
    [StringLength(255)]
    public string Url { get; set; } = null!;

    [Column("EMAIL")]
    [StringLength(80)]
    public string Email { get; set; } = null!;

    [Column("PHONE")]
    [StringLength(20)]
    public string Phone { get; set; } = null!;

    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
