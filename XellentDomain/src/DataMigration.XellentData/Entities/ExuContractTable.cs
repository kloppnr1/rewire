using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Contractnum))]
[Table("EXU_CONTRACTTABLE", Schema = "dbo")]
public class ExuContractTable
{
    [Key]
    [Column("CONTRACTNUM")]
    [StringLength(10)]
    public string Contractnum { get; set; } = null!;

    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Column("CUSTACCOUNT")]
    [StringLength(25)]
    public string Custaccount { get; set; } = null!;

    [Column("NAME")]
    [StringLength(2000)]
    public string Name { get; set; } = null!;

    [Column("CONTRACTSTARTDATE", TypeName = "datetime")]
    public DateTime Contractstartdate { get; set; }

    [Column("CONTRACTENDDATE", TypeName = "datetime")]
    public DateTime Contractenddate { get; set; }

    [Column("COMPANYID")]
    [StringLength(4)]
    public string Companyid { get; set; } = null!;

    [Column("DELIVERYCATEGORY")]
    [StringLength(10)]
    public string Deliverycategory { get; set; } = null!;

    [Column("REFERENCETOMAINCONTRACT")]
    [StringLength(10)]
    public string? Referencetomaincontract { get; set; }

    [Column("ADDRESS_RECID")]
    public long AddressRecid { get; set; }

    [Column("PBSAGREEMENTNUM")]
    public int Pbsagreementnum { get; set; }

    /// <summary>
    /// PBS transaction code: 0=blank, 1=Tilgang aftale, 2=Aktiv aftale,
    /// 3=Afmeldt af PI, 4=Afmeldt af kreditor, 5=Afmeldt af BS, 6=Aktiv leverandørservice
    /// </summary>
    [Column("PBSTRANSACTIONCODE")]
    public int Pbstransactioncode { get; set; }

    /// <summary>Date the PBS transaction code last changed (e.g. termination date for code 4).</summary>
    [Column("PBSTRANSDATE", TypeName = "datetime")]
    public DateTime Pbstransdate { get; set; }

    [Column("MOBILEPAYSUBSCRIPTIONAGRE20135")]
    [StringLength(60)]
    public string Mobilepaysubscriptionagre20135 { get; set; } = null!;

    [Column("MOBILEPAYAGREEMENTCANCELDATE", TypeName = "datetime")]
    public DateTime? Mobilepayagreementcanceldate { get; set; }

    [Column("EINVOICEEANNUM")]
    [StringLength(14)]
    public string Einvoiceeannum { get; set; } = null!;

    [Column("CUSTPAYMCODE")]
    [StringLength(10)]
    public string Custpaymcode { get; set; } = null!;

    [Column("BILLINGPLANNUM")]
    [StringLength(10)]
    public string Billingplannum { get; set; } = null!;

    [Column("BILLINGBASIS")]
    public int Billingbasis { get; set; }

    [Column("CONTRACTTEMPLATENUM")]
    [StringLength(10)]
    public string Contracttemplatenum { get; set; } = null!;

    [Column("CUSTCONTACTPERSONID")]
    [StringLength(25)]
    public string? Custcontactpersonid { get; set; }

    [Column("EINVOICEACCOUNTCODE")]
    [StringLength(25)]
    public string? Einvoiceaccountcode { get; set; }

    [Column("CUSTPURCHASEORDER")]
    [StringLength(25)]
    public string? Custpurchaseorder { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }
}
