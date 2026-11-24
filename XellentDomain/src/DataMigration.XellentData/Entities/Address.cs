using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("ADDRESS", Schema = "dbo")]
public class Address
{
    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("STREET")]
    [StringLength(250)]
    public string Street { get; set; } = null!;

    /// <summary>
    /// Full formatted address (Dynamics AX <c>ADDRESS</c> column). Includes street name,
    /// house number, floor, apartment etc. Use this for LIKE searches against the whole
    /// address; <see cref="Street"/> contains only the street-name portion.
    /// </summary>
    [Column("ADDRESS")]
    [StringLength(250)]
    public string FullAddress { get; set; } = null!;

    [Column("ZIPCODE")]
    [StringLength(10)]
    public string Zipcode { get; set; } = null!;

    [Column("CITY")]
    [StringLength(60)]
    public string City { get; set; } = null!;

    [Column("COUNTRYREGIONID")]
    [StringLength(10)]
    public string Countryregionid { get; set; } = null!;

    [Column("STATE")]
    [StringLength(10)]
    public string State { get; set; } = null!;

    [Column("COUNTY")]
    [StringLength(10)]
    public string County { get; set; } = null!;

    [Column("ADDRTABLEID")]
    public int Addrtableid { get; set; }

    [Column("EXU_ADDRESSID")]
    [StringLength(100)]
    public string? ExuAddressid { get; set; }
}
