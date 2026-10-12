namespace DataMigration.OpsCli.Services;

public sealed record SettlementCustomerException(string Name,string Segment,string Schedule);

/// <summary>Configured exact names or whitespace-boundary prefixes; no source reads or hidden defaults.</summary>
public static class SettlementCustomerExceptionRules
{
    public static string Normalize(string value) => string.Join(" ",value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<SettlementCustomerException>? Validate(SettlementConfig config,string source = "run options")
    {
        if (config.CustomerExceptions is null) return null;
        var segments = config.Segments.Values.Concat(config.UnresolvedSegments?.Values ?? [])
            .SelectMany(r => r.Cvr.Concat(r.Cpr).Concat(r.Blank))
            .Concat(config.TemplateProducts?.Values.SelectMany(v => v.Select(p => p.Segment)) ?? [])
            .Concat(config.SettlSegments is null ? [] : new[] { config.SettlSegments.Sme,config.SettlSegments.Business,config.SettlSegments.BusinessExempt })
            .Concat(new[] { SettlementE18Proposal.Segment,"Privat","Privat_Opladning_Eje","Privat_Opladning_Leje","Erhverv_UG" })
            .GroupBy(Normalize,StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,g => Normalize(g.First()),StringComparer.OrdinalIgnoreCase);
        var schedules = config.Schedules.Values.Concat(config.GroupSchedules?.Values ?? [])
            .GroupBy(Normalize,StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,g => Normalize(g.First()),StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<SettlementCustomerException>();
        foreach (var rule in config.CustomerExceptions)
        {
            void Invalid(string reason) => throw new InvalidOperationException($"Settlement config: customerExceptions {reason} ({source})");
            if (rule is null || string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Segment) || string.IsNullOrWhiteSpace(rule.Schedule))
            { Invalid("requires name, segment and schedule on every row"); continue; }
            var name = Normalize(rule.Name);
            var star = name.IndexOf('*');
            if (star >= 0 && (star != name.Length - 1 || name.Count(c => c == '*') != 1)) Invalid($"name '{name}' allows only one trailing *");
            if (star >= 0) name = name[..^1].TrimEnd() + "*";
            if (name == "*") Invalid("does not allow the catch-all name '*'");
            if (!names.Add(name)) Invalid($"has duplicate normalized name pattern '{name}'");
            if (!segments.TryGetValue(Normalize(rule.Segment),out var segment)) Invalid($"name '{name}' has unsupported segment '{rule.Segment}'");
            if (!schedules.TryGetValue(Normalize(rule.Schedule),out var schedule)) Invalid($"name '{name}' has unsupported schedule '{rule.Schedule}'");
            rules.Add(new(name,segment!,schedule!));
        }
        return rules;
    }

    public static SettlementCustomerException? Match(IReadOnlyList<SettlementCustomerException>? rules,string? name)
    {
        if (rules is null || string.IsNullOrWhiteSpace(name)) return null;
        var normalized = Normalize(name);
        var exact = rules.FirstOrDefault(r => !r.Name.EndsWith('*') && normalized.Equals(r.Name,StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return rules.Where(r => r.Name.EndsWith('*')).OrderByDescending(r => r.Name.Length).FirstOrDefault(r => {
            var stem = r.Name[..^1];
            return normalized.Equals(stem,StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(stem + " ",StringComparison.OrdinalIgnoreCase);
        });
    }
}
