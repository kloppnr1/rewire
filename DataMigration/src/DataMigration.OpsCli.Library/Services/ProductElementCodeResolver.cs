namespace DataMigration.OpsCli.Services;

/// <summary>
/// One appsettings rule mapping a Xellent <c>Product.Code</c> to a Settl
/// ProductElementCode. Exactly one of <see cref="Exact"/> or <see cref="Contains"/>
/// should be set:
/// <list type="bullet">
/// <item><c>Exact</c> — product code must equal this value (case-insensitive).</item>
/// <item><c>Contains</c> — product code must contain this substring (case-insensitive),
/// e.g. <c>"VGG"</c> matches all <c>VGGSpot*</c> products.</item>
/// </list>
/// </summary>
public sealed class ProductElementMappingRule
{
    public string? Exact { get; set; }
    public string? Contains { get; set; }
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Resolves a Xellent <c>Product.Code</c> to a Settl ProductElementCode using an
/// ordered list of rules (first match wins). Configured via the
/// <c>ProductElementMappings</c> appsettings section.
/// </summary>
public sealed class ProductElementCodeResolver
{
    private readonly IReadOnlyList<ProductElementMappingRule> _rules;

    public ProductElementCodeResolver(IReadOnlyList<ProductElementMappingRule> rules) => _rules = rules;

    /// <summary>Returns the mapped code, or null when no rule matches.</summary>
    public string? Resolve(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode)) return null;

        foreach (var rule in _rules)
        {
            if (!string.IsNullOrEmpty(rule.Exact)
                && string.Equals(productCode, rule.Exact, StringComparison.OrdinalIgnoreCase))
                return rule.Code;

            if (!string.IsNullOrEmpty(rule.Contains)
                && productCode.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase))
                return rule.Code;
        }
        return null;
    }
}
