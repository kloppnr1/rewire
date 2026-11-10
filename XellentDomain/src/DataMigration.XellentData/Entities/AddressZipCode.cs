using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Countryregionid), nameof(Zipcode), nameof(County))]
[Table("ADDRESSZIPCODE", Schema = "dbo")]
public class AddressZipCode
{
    [Key]
    [Column("ZIPCODE")]
    [StringLength(10)]
    public string Zipcode { get; set; } = null!;

    [Key]
    [Column("COUNTY")]
    [StringLength(10)]
    public string County { get; set; } = null!;

    [Key]
    [Column("COUNTRYREGIONID")]
    [StringLength(10)]
    public string Countryregionid { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("CITY")]
    [StringLength(60)]
    public string City { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }
}
