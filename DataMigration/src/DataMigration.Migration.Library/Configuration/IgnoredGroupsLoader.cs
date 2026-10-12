using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// Læser den delte liste over kundegrupper (TechnicGroup4) der aldrig må merge, fra
/// appsettings-sektionen <c>Extraction:IgnoredGroups</c>.
///
/// <para>Listen er en kendsgerning om dataområdet — Medarbej er Verdos egen CVR, så uden
/// listen samles alle medarbejderkonti til én Settl-kunde uanset hvilken profil man kører.
/// Derfor hører den til i basen ved siden af <see cref="BaseMappingLoader"/> og ikke i den
/// enkelte profil: <c>create --all-active</c> kan køre helt uden profil, og
/// <c>reconcile</c> kører altid uden.</para>
/// </summary>
public static class IgnoredGroupsLoader
{
    public const string DefaultSectionName = "Extraction:IgnoredGroups";

    /// <summary>
    /// Læser basens liste. Tom eller manglende sektion giver et tomt array — kalderen kan
    /// sende det videre uden null-tjek, og <see cref="Union"/> kollapser det til null.
    /// </summary>
    public static string[] Load(IConfiguration config, string sectionName = DefaultSectionName)
    {
        var section = config.GetSection(sectionName);
        if (!section.Exists())
            return [];

        return section.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();
    }

    /// <summary>
    /// Lægger en profils egne grupper oveni basen — union, ikke erstatning.
    ///
    /// <para>Begrundelsen er fejltilstanden: med erstatning kan en profil der sætter sin egen
    /// liste tavst genåbne merge for medarbejderkonti, og det opdages først som forkerte
    /// kunder i Settl. Union kan kun fejle den anden vej, hvor konti står alene og er til at
    /// få øje på i rapporten.</para>
    ///
    /// <para>Sammenligning er case-insensitiv (Xellents GroupNum-casing er ikke stabil), og
    /// resultatet er idempotent: samme liste påført to gange giver samme sæt. Det er
    /// nødvendigt, fordi listen påføres både i options-fabrikken og i OpsCli's Xellent-loader.
    /// Tomt resultat giver null, så nuværende adfærd ("ingen grupper ignoreres") bevares.</para>
    /// </summary>
    public static string[]? Union(IEnumerable<string>? first, IEnumerable<string>? second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var group in (first ?? []).Concat(second ?? []))
        {
            if (string.IsNullOrWhiteSpace(group))
                continue;
            var trimmed = group.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result.Count > 0 ? [.. result] : null;
    }

    /// <summary>Sæt-formen, som <c>MapperOptions.IgnoredGroupNumbers</c> tager imod.</summary>
    public static IReadOnlySet<string>? ToSet(IEnumerable<string>? first, IEnumerable<string>? second = null)
    {
        var union = Union(first, second);
        return union is null ? null : new HashSet<string>(union, StringComparer.OrdinalIgnoreCase);
    }
}
