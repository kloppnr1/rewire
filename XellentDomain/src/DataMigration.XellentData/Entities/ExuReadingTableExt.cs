using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("EXU_READINGTABLEEXT")]
public class ExuReadingTableExt
{
    public long Recid { get; set; }
    public string Dataareaid { get; set; } = string.Empty;
    public string Deliverycategory { get; set; } = string.Empty;
    public string Companyid { get; set; } = string.Empty;
    public string Agreementnum { get; set; } = string.Empty;
    public string Delpointnum { get; set; } = string.Empty;
    public DateTime Readingdate { get; set; }
    public decimal Expectedconsumptionperyear { get; set; }
}
