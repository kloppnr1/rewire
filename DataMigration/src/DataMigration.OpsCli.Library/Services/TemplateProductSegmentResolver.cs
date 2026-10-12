namespace DataMigration.OpsCli.Services;

/// <summary>
/// The template+product segment rule: Xellent contract template plus the product on the same
/// contract decide the Settl segment and the Settl contract the customer is expected to end up on.
/// Pure — no Settl, no Xellent.
///
/// <para><b>Template and product are paired per metering point.</b> A customer holding two templates
/// and two products has two real combinations, not four; pooling the customer's templates and
/// products before matching would invent combinations that exist on no metering point and hand back
/// a segment nothing supports.</para>
///
/// <para>The source table is written for customers with a CVR, so customers without one get no
/// segment from this rule — their templates and products still appear in the output as evidence.</para>
///
/// <para>Metering points whose combination the table does not cover are reported rather than
/// dropped: an unmapped combination and a customer the rule genuinely says nothing about look
/// identical in the output otherwise. Several distinct segments across a customer's metering points
/// is a conflict, since a Settl segment is a customer-level property.</para>
/// </summary>
public static class TemplateProductSegmentResolver
{
    /// <param name="ExpectedContracts">Settl contracts the table names for the matched combinations.
    /// Rows the table leaves to the erhverv rule ("samme migreringsregel som erhverv") carry no
    /// contract and contribute nothing here — absence means "no expectation", not "no contract".</param>
    public sealed record Result(
        string? Segment,
        IReadOnlyList<string> ExpectedContracts,
        string? Note);

    /// <param name="entries">This customer's metering points, each carrying the template and the
    /// products of the same pinned contract.</param>
    public static Result Resolve(
        CustomerIdentityKind identity,
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateProductPattern>>? templateProducts)
    {
        if (templateProducts is null || templateProducts.Count == 0)
            return new Result(null, [], "templateProducts not configured");
        if (identity != CustomerIdentityKind.Cvr)
            return new Result(null, [], "no CVR — the template+product table only covers companies");

        var segments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var contracts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ContractTemplate)) continue;

            var key = SettlementConfigKeys.NormalizeTemplate(entry.ContractTemplate);
            if (!templateProducts.TryGetValue(key, out var patterns))
            {
                unmatched.Add(entry.ContractTemplate);
                continue;
            }

            var products = entry.ProductNums ?? [];
            if (products.Count == 0)
            {
                // The template is covered but the contract carried no product active at cutover, so
                // there is nothing to match the product patterns against — including a catch-all,
                // which still describes a product.
                unmatched.Add($"{entry.ContractTemplate} (no product)");
                continue;
            }

            foreach (var product in products)
            {
                // Patterns arrive ordered most-specific-first from the loader, so the first match is
                // the intended one regardless of how the file was written.
                var match = patterns.FirstOrDefault(p => p.Matches(product));
                if (match is null)
                {
                    unmatched.Add($"{entry.ContractTemplate} + {product}");
                    continue;
                }
                segments.Add(match.Segment);
                if (!string.IsNullOrWhiteSpace(match.Contract)) contracts.Add(match.Contract);
            }
        }

        var note = unmatched.Count > 0
            ? "not in templateProducts: " + string.Join(", ", unmatched)
            : null;

        return segments.Count switch
        {
            0 => new Result(null, contracts.ToList(), note ?? "no template+product combination matched"),
            1 => new Result(segments.First(), contracts.ToList(), note),
            // A customer-level property cannot hold two values; naming one would be a guess.
            _ => new Result(
                null,
                contracts.ToList(),
                Join(note, "template+product yields several segments: " + string.Join(", ", segments)))
        };
    }

    private static string Join(string? first, string second) =>
        string.IsNullOrEmpty(first) ? second : $"{first}; {second}";
}
