using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// Reads the shared <c>Mappings</c> section from appsettings — the mapping that applies to
/// every run, so a whole-commodity migration (<c>create --all-active</c>) needs no profile
/// at all.
///
/// <para>Bound by hand rather than via <c>IConfiguration.Get&lt;MappingOptions&gt;()</c>:
/// <see cref="ProductNumContractEntry"/> accepts either a bare string or an object keyed by
/// payment term, and that shape lives in a System.Text.Json converter the configuration
/// binder does not consult.</para>
/// </summary>
public static class BaseMappingLoader
{
    private const string WildcardKey = "*";

    public static MappingOptions? Load(IConfiguration config, string sectionName = "Mappings")
    {
        var section = config.GetSection(sectionName);
        if (!section.Exists())
            return null;

        return new MappingOptions
        {
            GroupNum = ReadStringMap(section.GetSection(nameof(MappingOptions.GroupNum))),
            AccountNum = ReadStringMap(section.GetSection(nameof(MappingOptions.AccountNum))),
            GridArea = ReadStringMap(section.GetSection(nameof(MappingOptions.GridArea))),
            SettlementGroup = ReadStringMap(section.GetSection(nameof(MappingOptions.SettlementGroup))),
            ProductNum = ReadProductNumMap(section.GetSection(nameof(MappingOptions.ProductNum))),
            TemplateProducts = ReadTemplateProducts(section.GetSection(nameof(MappingOptions.TemplateProducts))),
            ElectricityChargerContractMap = ReadNestedStringMap(
                section.GetSection(nameof(MappingOptions.ElectricityChargerContractMap))),
        };
    }

    /// <summary>
    /// Overlays a profile's mappings on the shared base: the profile wins per key, the base
    /// supplies everything the profile leaves out. Keeps existing profiles behaving exactly
    /// as before while letting profile-less runs inherit the full mapping.
    /// </summary>
    public static MappingOptions? Merge(MappingOptions? baseMappings, MappingOptions? profileMappings)
    {
        if (baseMappings is null) return profileMappings;
        if (profileMappings is null) return baseMappings;

        return new MappingOptions
        {
            GroupNum = MergeMap(baseMappings.GroupNum, profileMappings.GroupNum),
            AccountNum = MergeMap(baseMappings.AccountNum, profileMappings.AccountNum),
            GridArea = MergeMap(baseMappings.GridArea, profileMappings.GridArea),
            SettlementGroup = MergeMap(baseMappings.SettlementGroup, profileMappings.SettlementGroup),
            ProductNum = MergeMap(baseMappings.ProductNum, profileMappings.ProductNum),
            TemplateProducts = MergeMap(baseMappings.TemplateProducts, profileMappings.TemplateProducts),
            ElectricityChargerContractMap = MergeMap(
                baseMappings.ElectricityChargerContractMap, profileMappings.ElectricityChargerContractMap),
        };
    }

    private static Dictionary<string, TValue>? MergeMap<TValue>(
        Dictionary<string, TValue>? baseMap, Dictionary<string, TValue>? profileMap)
    {
        if (baseMap is null) return profileMap;
        if (profileMap is null) return baseMap;

        var merged = new Dictionary<string, TValue>(baseMap);
        foreach (var (key, value) in profileMap)
            merged[key] = value;
        return merged;
    }

    private static Dictionary<string, string>? ReadStringMap(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;

        var map = new Dictionary<string, string>();
        foreach (var child in section.GetChildren())
        {
            if (child.Value is { } value)
                map[child.Key] = value;
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Reads the template+product table by hand for the same reason <see cref="ReadProductNumMap"/>
    /// is: the entry accepts either a bare contract name or an object keyed by identification, and
    /// that shape lives in a System.Text.Json converter the configuration binder never consults.
    /// </summary>
    private static Dictionary<string, Dictionary<string, TemplateProductContractEntry>>? ReadTemplateProducts(
        IConfigurationSection section)
    {
        if (!section.Exists())
            return null;

        var map = new Dictionary<string, Dictionary<string, TemplateProductContractEntry>>();
        foreach (var template in section.GetChildren())
        {
            var products = new Dictionary<string, TemplateProductContractEntry>();
            foreach (var product in template.GetChildren())
            {
                // Scalar form: "V Variabel": "V ErhVari"
                if (product.Value is { } contractName)
                {
                    products[product.Key] = TemplateProductContractEntry.FromString(contractName);
                    continue;
                }

                // Object form: "V Variabel": { "cvr": "V ErhVari", "*": "V Variabel" }
                var entry = new TemplateProductContractEntry();
                foreach (var identification in product.GetChildren())
                {
                    if (identification.Value is not { } identityContract) continue;
                    TemplateProductContractEntryConverter.Apply(entry, identification.Key, identityContract);
                }
                products[product.Key] = entry;
            }
            if (products.Count > 0) map[template.Key] = products;
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Reads a plain two-level string map (outer key → inner key → value) — the charger table's
    /// shape, with no scalar/object duality to hand-roll.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>>? ReadNestedStringMap(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;

        var map = new Dictionary<string, Dictionary<string, string>>();
        foreach (var outer in section.GetChildren())
        {
            var inner = ReadStringMap(outer);
            if (inner is { Count: > 0 }) map[outer.Key] = inner;
        }
        return map.Count > 0 ? map : null;
    }

    private static Dictionary<string, ProductNumContractEntry>? ReadProductNumMap(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;

        var map = new Dictionary<string, ProductNumContractEntry>();
        foreach (var child in section.GetChildren())
        {
            // Scalar form: "PROD": "ContractName"
            if (child.Value is { } contractName)
            {
                map[child.Key] = ProductNumContractEntry.FromString(contractName);
                continue;
            }

            // Object form: "PROD": { "M14": "A", "*": "Default" }
            var byPaymentTerm = new Dictionary<string, string>();
            string? wildcard = null;
            foreach (var term in child.GetChildren())
            {
                if (term.Value is not { } termContract)
                    continue;

                if (term.Key == WildcardKey)
                    wildcard = termContract;
                else
                    byPaymentTerm[term.Key] = termContract;
            }

            map[child.Key] = new ProductNumContractEntry
            {
                Default = wildcard,
                ByPaymentTerm = byPaymentTerm.Count > 0 ? byPaymentTerm : null
            };
        }
        return map.Count > 0 ? map : null;
    }
}
