using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Contactpersonid))]
[Table("CONTACTPERSON", Schema = "dbo")]
public class ContactPerson
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("CONTACTPERSONID")]
    [StringLength(20)]
    public string Contactpersonid { get; set; } = null!;

    [Column("NAME")]
    [StringLength(60)]
    public string Name { get; set; } = null!;

    [Column("CUSTACCOUNT")]
    [StringLength(25)]
    public string Custaccount { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
