using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Computes the fan-out of <see cref="Payer"/>s a <see cref="Customer"/> should have:
/// one default (primary) payer plus one extra per unique non-empty contract-level EAN
/// that differs from the customer-level EAN. Pure function — no I/O.
///
/// <para>Single-payer case: identifier is <c>Invoicing.PayerReference</c> if set, otherwise
/// <see cref="Customer.AccountNumber"/>; EAN is <c>Billing.DefaultEan</c>.
/// Fan-out case (extras present): ALL payers use <c>{AccountNumber}-{index}</c> (1-based, default
/// first, then extras in first-occurrence EAN order) — PayerReference is ignored to keep the
/// numbered scheme consistent across the customer's payer set. Contracts with empty/null EAN
/// (or matching the default's EAN) are assigned to the default; contracts with a
/// distinct EAN are assigned to the matching extra payer. Default appears first;
/// extras follow in first-occurrence order across <paramref name="contracts"/> — same
/// order LINQ <c>Distinct</c> produced in the legacy SettlClient.PayerTranslator.</para>
///
/// <para>Each payer's <see cref="Payer.InvoiceChannel"/> and <see cref="Payer.PaymentChannel"/>
/// are computed per-payer: priority MobilePay &gt; PBS &gt; EAN (OioUbl) &gt; Email (PDF) &gt; fallback
/// (PBS_BS/FIK). This means extra payers with a distinct EAN correctly get OioUbl/OIOUBL_FIK
/// even when the customer's default channel is PDF or PBS_BS.</para>
/// </summary>
public static class PayerMapper
{
    public static IReadOnlyList<Payer> Build(Customer customer, IReadOnlyList<Contract> contracts)
    {
        var billing = customer.Billing;
        var defaultIdentifier = customer.AccountNumber;
        var defaultEan = NormalizeEan(billing.DefaultEan);

        // First pass: discover the distinct extra EANs in first-occurrence order. Mirror
        // legacy: Distinct(Ordinal) over non-empty contract EANs, then drop the one that
        // matches the default's EAN (it's already covered by the default payer).
        var extraEansInOrder = new List<string>();
        var seenEans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            var ean = NormalizeEan(contract.BuyerInvoicing?.ElectronicNumber);
            if (ean is null) continue;
            if (!seenEans.Add(ean)) continue;
            if (string.Equals(ean, defaultEan, StringComparison.Ordinal)) continue;
            extraEansInOrder.Add(ean);
        }

        // Second pass: bucket contracts by EAN. A contract with empty EAN — or EAN equal
        // to the default's EAN — routes to the default payer; everything else routes to
        // the matching extra. Preserve contract order within each bucket.
        var defaultContracts = new List<Contract>();
        var contractsByExtraEan = extraEansInOrder.ToDictionary(
            ean => ean,
            _ => new List<Contract>(),
            StringComparer.Ordinal);

        foreach (var contract in contracts)
        {
            var ean = NormalizeEan(contract.BuyerInvoicing?.ElectronicNumber);
            if (ean is null || string.Equals(ean, defaultEan, StringComparison.Ordinal))
            {
                defaultContracts.Add(contract);
                continue;
            }
            contractsByExtraEan[ean].Add(contract);
        }

        var hasExtras = extraEansInOrder.Count > 0;
        var email = customer.Contact.Email;
        var (defaultInvoiceChannel, defaultPaymentChannel) = ResolveChannels(defaultEan, billing, email);

        var result = new List<Payer>(1 + extraEansInOrder.Count)
        {
            new Payer(
                Identifier: hasExtras ? $"{customer.AccountNumber}-1" : defaultIdentifier,
                IsPrimary: true,
                ContractNumbers: defaultContracts.Select(c => c.Number).ToList(),
                BuyerInvoicing: BuildPayerReferences(defaultEan, defaultContracts),
                InvoiceChannel: defaultInvoiceChannel,
                PaymentChannel: defaultPaymentChannel)
        };
        for (var i = 0; i < extraEansInOrder.Count; i++)
        {
            var ean = extraEansInOrder[i];
            var bucket = contractsByExtraEan[ean];
            var (invoiceChannel, paymentChannel) = ResolveChannels(ean, billing, email);
            result.Add(new Payer(
                Identifier: $"{customer.AccountNumber}-{i + 2}",
                IsPrimary: false,
                ContractNumbers: bucket.Select(c => c.Number).ToList(),
                BuyerInvoicing: BuildPayerReferences(ean, bucket),
                InvoiceChannel: invoiceChannel,
                PaymentChannel: paymentChannel));
        }
        return result;
    }

    /// <summary>
    /// Derives invoice and payment channel for a single payer.
    /// Priority: MobilePay &gt; PBS &gt; EAN (OioUbl) &gt; Email (PDF) &gt; fallback (PBS_BS/FIK).
    /// Mirrors <c>PaymentInformation.GetInvoiceChannel</c> / <c>GetPaymentChannel</c> but
    /// operates on the domain-level billing state rather than raw Xellent fields.
    /// </summary>
    private static (InvoiceChannel InvoiceChannel, PaymentChannel PaymentChannel) ResolveChannels(
        string? ean, CustomerBilling billing, string? email)
    {
        if (billing.MobilePay != null)
            return (InvoiceChannel.MobilePay, PaymentChannel.MobilePay);

        if (billing.Pbs != null)
            return (InvoiceChannel.PbsBs, PaymentChannel.Bs);

        if (!string.IsNullOrEmpty(ean))
            return (InvoiceChannel.OioUbl, PaymentChannel.EInvoiceFik);

        if (!string.IsNullOrEmpty(email))
            return (InvoiceChannel.Pdf, PaymentChannel.Fik);

        return (InvoiceChannel.PbsBs, PaymentChannel.Fik);
    }

    /// <summary>
    /// Builds the Payer-level reference block. The EAN (bucket key) is the gate: OrderIdentifier,
    /// AgreementIdentifier and BuyerReferenceIdentifier are OioUbl e-invoicing references that only
    /// make sense when the Payer has an electronic number. A Payer without an EAN carries no
    /// reference fields — they are ignored entirely (returns null), even if the underlying Xellent
    /// contracts populate them. When the EAN is present, only the contracts that carry the same EAN
    /// participate in the divergence check and uniformity calculation — contracts without the EAN
    /// have no OioUbl invoicing context and must not veto the payer-level refs. The three fields
    /// are treated as a set: ALL three land on the Payer only when ALL three are uniform (same
    /// non-blank value) across the EAN-matching contracts. If any single field diverges (≥2 distinct
    /// values, or blank vs. non-blank mix), none of the three are written to the Payer — they are
    /// all pushed down to individual metering points instead (see
    /// <c>XellentDomainMapper.ApplyMpBillingOverrides</c>). A field that is blank on every
    /// EAN-matching contract is considered absent (not divergent) and does not force the other
    /// fields off the Payer.
    /// </summary>
    private static BuyerInvoicingReferences? BuildPayerReferences(string? ean, IReadOnlyList<Contract> bucket)
    {
        // No EAN → no electronic invoicing → drop the reference fields entirely.
        if (ean is null) return null;

        // Only contracts that carry this payer's EAN participate in OioUbl invoicing.
        // Contracts without the EAN have no invoicing context and must not affect the refs.
        var eanContracts = bucket
            .Where(c => string.Equals(c.BuyerInvoicing?.ElectronicNumber, ean, StringComparison.Ordinal))
            .ToList();

        // Set rule: if any of the three reference fields diverges across EAN-matching contracts,
        // none go on the Payer. ApplyMpBillingOverrides will push non-blank values down to the MPs.
        if (AnyReferenceFieldDiverges(eanContracts))
            return new BuyerInvoicingReferences(ean, null, null, null);

        var orderId = UniformFieldOrNull(eanContracts, c => c.BuyerInvoicing?.OrderIdentifier);
        var agreementId = UniformFieldOrNull(eanContracts, c => c.BuyerInvoicing?.AgreementIdentifier);
        var buyerRef = UniformFieldOrNull(eanContracts, c => c.BuyerInvoicing?.BuyerReferenceIdentifier);
        return new BuyerInvoicingReferences(ean, orderId, agreementId, buyerRef);
    }

    /// <summary>Returns true when at least one of the three reference fields diverges across the
    /// bucket — i.e. has a mix of blank and non-blank values, or ≥2 distinct non-blank values.
    /// A field that is blank on every contract is absent (not divergent).</summary>
    private static bool AnyReferenceFieldDiverges(IReadOnlyList<Contract> bucket)
        => IsDivergent(bucket, c => c.BuyerInvoicing?.OrderIdentifier)
        || IsDivergent(bucket, c => c.BuyerInvoicing?.AgreementIdentifier)
        || IsDivergent(bucket, c => c.BuyerInvoicing?.BuyerReferenceIdentifier);

    private static bool IsDivergent(IReadOnlyList<Contract> contracts, Func<Contract, string?> selector)
    {
        bool hasBlank = false;
        string? seenNonBlank = null;
        foreach (var contract in contracts)
        {
            var value = selector(contract);
            if (string.IsNullOrWhiteSpace(value))
            {
                hasBlank = true;
            }
            else
            {
                if (seenNonBlank is null) seenNonBlank = value;
                else if (!string.Equals(seenNonBlank, value, StringComparison.Ordinal)) return true; // ≥2 distinct
            }
        }
        return hasBlank && seenNonBlank is not null; // mixed blank/non-blank
    }

    /// <summary>The single non-blank value shared by ALL contracts in the bucket, or null when the
    /// field is blank on any contract or has more than one distinct value (i.e. it diverges).</summary>
    private static string? UniformFieldOrNull(IReadOnlyList<Contract> contracts, Func<Contract, string?> selector)
    {
        string? agreed = null;
        foreach (var contract in contracts)
        {
            var value = selector(contract);
            if (string.IsNullOrWhiteSpace(value)) return null;       // blank anywhere → diverges
            if (agreed is null) agreed = value;
            else if (!string.Equals(agreed, value, StringComparison.Ordinal)) return null; // ≥2 distinct
        }
        return agreed;
    }

    private static string? NormalizeEan(string? ean)
    {
        if (string.IsNullOrWhiteSpace(ean)) return null;
        var trimmed = ean.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
