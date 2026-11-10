using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataMigration.XellentData.Entities;

[Table("CUSTTABLE", Schema = "dbo")]
public class CustTable
{
    [Column("ACCOUNTNUM")]
    [StringLength(25)]
    public string Accountnum { get; set; } = null!;

    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("NAME")]
    [StringLength(60)]
    public string Name { get; set; } = null!;

    [Column("ADDRESS")]
    [StringLength(250)]
    public string Address { get; set; } = null!;

    [Column("STREET")]
    [StringLength(250)]
    public string Street { get; set; } = null!;

    [Column("CITY")]
    [StringLength(60)]
    public string City { get; set; } = null!;

    [Column("ZIPCODE")]
    [StringLength(10)]
    public string Zipcode { get; set; } = null!;

    [Column("COUNTRYREGIONID")]
    [StringLength(10)]
    public string Countryregionid { get; set; } = null!;

    [Column("PHONE")]
    [StringLength(20)]
    public string Phone { get; set; } = null!;

    [Column("CELLULARPHONE")]
    [StringLength(20)]
    public string Cellularphone { get; set; } = null!;

    [Column("TELEFAX")]
    [StringLength(20)]
    public string Telefax { get; set; } = null!;

    [Column("EMAIL")]
    [StringLength(80)]
    public string Email { get; set; } = null!;

    [Column("COMPANYREGNUM")]
    [StringLength(15)]
    public string Companyregnum { get; set; } = null!;

    [Column("IDENTIFICATIONNUMBER")]
    [StringLength(50)]
    public string Identificationnumber { get; set; } = null!;

    [Column("PARTYTYPE")]
    public int Partytype { get; set; }

    [Column("NAMEALIAS")]
    [StringLength(20)]
    public string Namealias { get; set; } = null!;

    [Column("EXU_CPR_DISPONENT1")]
    [StringLength(10)]
    public string ExuCprDisponent1 { get; set; } = null!;

    [Column("EXU_ALTERNATIVECUSTOMERNUMBER")]
    [StringLength(50)]
    public string Exualternativecustomernumber { get; set; } = null!;

    /// <summary>
    /// Tells what <see cref="Exualternativecustomernumber"/> holds: 6 = CPR, 2 = CVR.
    /// <c>int NOT NULL</c> in AXDB50; 0 when no alternative number is registered.
    /// </summary>
    [Column("EXU_ALTERNATIVECUSTOMERCODE")]
    public int Exualternativecustomercode { get; set; }

    [Column("EXU_ADDITIONALNAME")]
    [StringLength(60)]
    public string ExuAdditionalname { get; set; } = null!;

    [Column("EXU_CPR_DISPONENT2")]
    [StringLength(10)]
    public string ExuCprDisponent2 { get; set; } = null!;

    [Column("EXU_CVR_DISPONENT1")]
    [StringLength(8)]
    public string ExuCvrDisponent1 { get; set; } = null!;

    [Column("EXU_CVR_DISPONENT2")]
    [StringLength(8)]
    public string ExuCvrDisponent2 { get; set; } = null!;

    [Column("RECID")]
    public long Recid { get; set; }

    [Column("PARTYID")]
    [StringLength(20)]
    public string Partyid { get; set; } = null!;

    [Column("EXU_TECHNICGROUP4")]
    [StringLength(20)]
    public string ExuTechnicgroup4 { get; set; } = null!;
}
