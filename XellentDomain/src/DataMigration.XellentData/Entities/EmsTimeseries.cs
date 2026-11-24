using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Recid))]
[Table("EMS_TIMESERIES", Schema = "dbo")]
public class EmsTimeseries
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("RECID")]
    public long Recid { get; set; }

    [Column("METERINGPOINT")]
    [StringLength(30)]
    public string Meteringpoint { get; set; } = null!;

    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("QUANTITYCODEVALUE")]
    [StringLength(10)]
    public string Quantitycodevalue { get; set; } = null!;

    /// <summary>0 = None, 1 = Quarter, 2 = Hour, 3 = Month, 4 = Day, 5 = Year, 6 = Minute. See <see cref="Enums.TimeResolution"/>.</summary>
    [Column("TIMERESOLUTION")]
    public int Timeresolution { get; set; }

    [Column("NAME")]
    [StringLength(200)]
    public string Name { get; set; } = null!;
}
