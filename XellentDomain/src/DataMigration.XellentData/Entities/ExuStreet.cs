using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Streetnum), nameof(Countynum))]
[Table("EXU_STREET", Schema = "dbo")]
public class ExuStreet
{
    [Key]
    [Column("STREETNUM")]
    [StringLength(10)]
    public string Streetnum { get; set; } = null!;

    [Key]
    [Column("COUNTYNUM")]
    [StringLength(10)]
    public string Countynum { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("STREETNAME")]
    [StringLength(40)]
    public string Streetname { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
