using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Groupnum))]
[Table("EXU_GROUPTABLE", Schema = "dbo")]
public class ExuGroupTable
{
    [Key]
    [Column("GROUPNUM")]
    [StringLength(20)]
    public string Groupnum { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("GROUPNAME")]
    [StringLength(60)]
    public string? Groupname { get; set; }
}
