using System.Globalization;
using System.Text;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Assigns each <see cref="ReconcileDriftRow"/> a triage <b>category</b>. This never excludes or
/// suppresses a row — every difference is still reported; the category is purely a label so the
/// reader can group/filter (representation vs date-semantics vs actionable billing drift, etc.).
/// </summary>
public static class DriftCategorizer
{
    // Stable category labels (also handy as CSV filter values).
    public const string Missing = "Missing in Settl";
    public const string MissingXellent = "Missing in Xellent";
    public const string SettlNative = "Settl-native metering point";
    public const string PriceListPeriod = "Price list period";
    public const string PayerLink = "Payer link (metering point → payer)";
    public const string Anomaly = "Anomaly";
    public const string RepNullEmpty = "Representation: null vs empty";
    public const string RepOpenEnded = "Representation: open-ended date";
    public const string DateStart = "Date semantics (Start)";
    public const string AnnualVolume = "EstimatedAnnualVolume";
    public const string DeliveryFlag = "Delivery flag (Xellent vs Settl runtime)";
    public const string BillingReference = "Billing reference (EAN/Order/Agreement/BuyerRef)";
    public const string SettlementMethod = "Settlement method";
    public const string PrimaryPayerFlag = "Primary-payer flag";
    public const string Address = "Address value";
    public const string AddressDarNormalized = "Address: DAR-normalised (benign)";
    public const string Contact = "Contact / channel";
    public const string Other = "Other field value";

    private static readonly string[] NoEndValues =
    {
        "0001-01-01T00:00:00Z", "9999-12-31T23:59:58.999Z", "9999-12-30T23:00:00Z",
    };

    public static string Categorize(ReconcileDriftRow row)
    {
        switch (row.Kind)
        {
            case DriftKind.MissingInSettl: return Missing;
            // A Settl InDelivery MP with no Xellent match is split by reason: a Settl-native MP
            // (created directly in Settl, no migration origin) is benign and gets its own bucket so
            // it doesn't read as actionable Xellent drift; everything else stays Missing-in-Xellent.
            case DriftKind.MissingInXellent:
                return row.Field == "settl-native-meteringpoint" ? SettlNative : MissingXellent;
            case DriftKind.Anomaly: return Anomaly;
        }

        // Price-list period drift (the only FieldDiff a PriceList row carries) — its own bucket so it
        // doesn't land under "Other field value".
        if (row.SubType == "PriceList") return PriceListPeriod;
        // Payer↔MP link drift: the only FieldDiff a PayerLink row carries is a re-link (the MP is linked
        // to a different payer than intended). Own bucket so it doesn't land under "Other field value".
        // (Missing/extra links arrive as MissingInSettl/MissingInXellent, already bucketed above.)
        if (row.SubType == "PayerLink") return PayerLink;

        var f = row.Field;
        // Both sides empty ("— → —", a null-vs-"" serialisation artifact) is benign regardless of
        // field — caught FIRST so an empty-vs-empty billing reference is bucketed as RepNullEmpty
        // (suppressed by ReconcileRunner) instead of surfacing as actionable Billing-reference drift.
        if (IsEmpty(row.Intended) && IsEmpty(row.Actual)) return RepNullEmpty;
        // Billing references are categorised before the remaining buckets — kept in the
        // Billing-reference bucket even when ONE side is empty/null (a real backfill signal), so they
        // aren't hidden under the representation category. Matched on the leaf field name so it covers
        // both MP-level (PaymentConfiguration.*) and payer-level (PayerBilling.*) references, but NOT
        // the billing dates (Start/End) or other config.
        var leaf = f.Contains('.') ? f[(f.LastIndexOf('.') + 1)..] : f;
        if (leaf is "ElectronicNumber" or "OrderIdentifier" or "AgreementIdentifier"
                 or "BuyerReference" or "BuyerReferenceIdentifier")
            return BillingReference;
        if (f.EndsWith("End", StringComparison.Ordinal) && IsNoEnd(row.Intended) && IsNoEnd(row.Actual)) return RepOpenEnded;
        if (f.EndsWith("Start", StringComparison.Ordinal)) return DateStart;
        if (f == "EstimatedAnnualVolume") return AnnualVolume;
        if (f == "InDelivery") return DeliveryFlag;
        if (f == "SettlementMethod") return SettlementMethod;
        if (f == "IsPrimaryCustomerPayer") return PrimaryPayerFlag;
        if (f.Contains("PhysicalAddress", StringComparison.Ordinal)
            || f.Contains("AddressIdentifier", StringComparison.Ordinal)
            || f.Contains("PostalAddress.Name", StringComparison.Ordinal))
            // Settl canonicalises the address from the DAR registry after creation, so the stored
            // string can differ from Xellent's literal value by accent/case/whitespace/punctuation
            // only (e.g. "Vester Alle" → "Vester Allé"). When the two sides fold equal, the diff is
            // a benign DAR normalisation, not actionable drift — bucketed separately so it doesn't
            // drown structural address drift (e.g. a wrong street/number split, which does NOT fold
            // equal and stays under Address).
            return AddressFoldEqual(row.Intended, row.Actual) ? AddressDarNormalized : Address;
        if (f.Contains("Email", StringComparison.Ordinal)
            || f.Contains("Phone", StringComparison.Ordinal)
            || f.Contains("Cellphone", StringComparison.Ordinal)
            || f.Contains("Fax", StringComparison.Ordinal)
            || f.Contains("InvoiceChannel", StringComparison.Ordinal)
            || f.Contains("PaymentChannel", StringComparison.Ordinal))
            return Contact;
        return Other;
    }

    private static bool IsEmpty(string? v)
    {
        var s = (v ?? "").Trim();
        return s.Length == 0 || string.Equals(s, "null", StringComparison.Ordinal);
    }

    private static bool IsNoEnd(string? v) => IsEmpty(v) || NoEndValues.Contains((v ?? "").Trim());

    /// <summary>
    /// True when two address strings differ only by accent/case/whitespace/punctuation — i.e. the
    /// same address spelled differently (a DAR normalisation), not a structural difference. Both
    /// sides must be non-empty (an empty-vs-value diff is a real presence change, not normalisation).
    /// Mirrors the merge-key folding in <c>FingerprintCalculator</c> (NFD + drop non-spacing marks
    /// + strip whitespace/punctuation + lowercase) so "same address" means the same thing here as
    /// it does for customer merging.
    /// </summary>
    private static bool AddressFoldEqual(string? a, string? b)
    {
        if (IsEmpty(a) || IsEmpty(b)) return false;
        return string.Equals(FoldAddress(a!), FoldAddress(b!), StringComparison.Ordinal);
    }

    private static readonly HashSet<char> AddressStripChars = new()
    {
        ' ', '\t', '\n', '\r', ' ', '/', '.', ',', '-', '&', '\'', '"', '(', ')',
    };

    private static string FoldAddress(string s)
    {
        var folded = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (AddressStripChars.Contains(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
