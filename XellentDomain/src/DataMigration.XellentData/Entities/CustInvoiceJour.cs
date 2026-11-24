using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Entities;

[PrimaryKey(nameof(Dataareaid), nameof(Invoiceid))]
[Table("CUSTINVOICEJOUR", Schema = "dbo")]
public class CustInvoiceJour
{
    [Key]
    [Column("DATAAREAID")]
    [StringLength(4)]
    public string Dataareaid { get; set; } = null!;

    [Key]
    [Column("INVOICEID")]
    [StringLength(20)]
    public string Invoiceid { get; set; } = null!;

    [Column("INVOICEACCOUNT")]
    [StringLength(25)]
    public string Invoiceaccount { get; set; } = null!;

    [Column("EXU_CONTRACTNUM")]
    [StringLength(10)]
    public string ExuContractnum { get; set; } = null!;

    [Column("INVOICEDATE", TypeName = "datetime")]
    public DateTime Invoicedate { get; set; }

    [Column("INVOICEAMOUNTMST", TypeName = "numeric(28,12)")]
    public decimal Invoiceamountmst { get; set; }

    [Column("EXU_CUSTFREEINVOICESTATUS")]
    public int ExuCustfreeinvoicestatus { get; set; }

    [Column("RECID")]
    public long Recid { get; set; }

    [Column("DUEDATE", TypeName = "datetime")]
    public DateTime Duedate { get; set; }

    [Column("SUMTAX", TypeName = "numeric(28,12)")]
    public decimal Sumtax { get; set; }

    [Column("EXU_BILLINGCATEGORY")]
    public int ExuBillingcategory { get; set; }

    [Column("EXU_PAYMENTMEDIA")]
    public int ExuPaymentmedia { get; set; }

    [Column("DELIVERYADDRESS")]
    [StringLength(250)]
    public string Deliveryaddress { get; set; } = string.Empty;

    [Column("INVOICINGADDRESS")]
    [StringLength(250)]
    public string Invoicingaddress { get; set; } = string.Empty;
}
