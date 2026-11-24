using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("DOCUREF", Schema = "dbo")]
public class DocuRef
{
    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("NAME")]
    [StringLength(60)]
    public string Name { get; set; } = null!;

    [Column("TYPEID")]
    [StringLength(10)]
    public string Typeid { get; set; } = null!;

    [Column("VALUERECID")]
    public long Valuerecid { get; set; }

    [Column("REFTABLEID")]
    public int Reftableid { get; set; }

    [Column("REFRECID")]
    public long Refrecid { get; set; }

    [Column("PARTYID")]
    [StringLength(20)]
    public string Partyid { get; set; } = null!;

    [Column("NOTES")]
    public string? Notes { get; set; }

    [Column("CREATEDDATETIME")]
    public DateTime Createddatetime { get; set; }

    [Column("REFCOMPANYID")]
    [StringLength(4)]
    public string? Refcompanyid { get; set; }

    [Column("ACTUALCOMPANYID")]
    [StringLength(4)]
    public string? Actualcompanyid { get; set; }

    [Column("RESTRICTION")]
    public int Restriction { get; set; }

    [Column("CREATEDBY")]
    [StringLength(5)]
    public string? Createdby { get; set; }

    [Column("MODIFIEDBY")]
    [StringLength(5)]
    public string? Modifiedby { get; set; }

    [Column("MODIFIEDDATETIME")]
    public DateTime? Modifieddatetime { get; set; }
}
