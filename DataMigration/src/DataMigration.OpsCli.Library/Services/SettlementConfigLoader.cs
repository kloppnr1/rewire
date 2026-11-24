using System.Text.Json;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// The two Xellent → Settl maps <c>export-settlement-config</c> groups by: schedules keyed by
/// <c>ExuContractTable.Billingplannum</c>, segments keyed by <c>Contracttemplatenum</c>.
/// </summary>
/// <param name="Segments">Keyed by <c>Contracttemplatenum</c>; the value picks the segment by the
/// customer's identification — see <see cref="SegmentRule"/>.</param>
/// <param name="GroupSchedules">Settl Settlement Group name → Settlement Schedule. A second, competing
/// source for the same schedule value as <paramref name="Schedules"/> — reported side by side rather
/// than merged, because which of the two is authoritative is not yet decided.</param>
/// <param name="SettlSegments">The Settl-driven segment rule (SME contracts / exemption). Null when
/// the section is absent, which the resolver reports rather than treating as "no customer qualifies".</param>
/// <param name="TemplateProducts">Contract template → ordered product patterns → segment + expected
/// Settl contract. Third competing segment source; see <see cref="TemplateProductPattern"/>.</param>
/// <param name="MultipleSegments">The segment a customer lands in when its metering points each point
/// at one segment, but not the same one. Null keeps them as conflicts — the decision to put them
/// somewhere is a human one, so it lives in the file rather than in the grouping code.</param>
/// <param name="UnresolvedSegments">Standing segment answer per reason a metering point produced no
/// schedule row at all (<see cref="SettlementConfigUnresolvedReason"/>). Those metering points never
/// reach the template rule — there is no Xellent contract to read a template from — so without an
/// entry here they carry a reason and nothing else. The value is a <see cref="SegmentRule"/> so the
/// answer can differ by identification, which is what separates a private consumer from a company.</param>
/// <param name="ScheduleConflicts">Standing answers for exact sets of disagreeing schedules — see
/// <see cref="ScheduleConflictRule"/>. Consulted only for customers whose settlement group named no
/// schedule; a group answer already settles the customer and is not second-guessed.</param>
public sealed record SettlementConfig(
    IReadOnlyDictionary<string, string> Schedules,
    IReadOnlyDictionary<string, SegmentRule> Segments,
    IReadOnlyDictionary<string, string>? GroupSchedules = null,
    SettlSegmentRules? SettlSegments = null,
    IReadOnlyDictionary<string, IReadOnlyList<TemplateProductPattern>>? TemplateProducts = null,
    string? MultipleSegments = null,
    IReadOnlyDictionary<SettlementConfigUnresolvedReason, SegmentRule>? UnresolvedSegments = null,
    IReadOnlyList<ScheduleConflictRule>? ScheduleConflicts = null,
    IReadOnlyList<SettlementCustomerException>? CustomerExceptions = null);

/// <summary>
/// The Settl-driven segment rule: a customer with a CVR gets <paramref name="Sme"/> when any of its
/// metering points sits on one of <paramref name="SmeContracts"/>, otherwise
/// <paramref name="BusinessExempt"/> when any metering point carries a billing exemption in Settl,
/// otherwise <paramref name="Business"/>. Customers without a CVR get no segment from this rule.
/// </summary>
public sealed record SettlSegmentRules(
    IReadOnlyList<string> SmeContracts,
    string Sme,
    string Business,
    string BusinessExempt);

/// <summary>
/// One row of the template+product table. <paramref name="Pattern"/> matches the Xellent
/// <c>Productnum</c>: an exact name, a <c>Spot*</c> prefix, or <c>*</c> for any product. Exact
/// matches win over prefix matches, which win over <c>*</c>, so the order rows appear in the file
/// cannot change the outcome.
/// </summary>
/// <param name="Contract">The Settl contract the combination is expected to map to, or null for the
/// table's "samme migreringsregel som erhverv" rows — those carry a segment but no contract
/// expectation of their own.</param>
public sealed record TemplateProductPattern(
    string Pattern,
    string Segment,
    string? Contract)
{
    /// <summary>Any product.</summary>
    public bool IsCatchAll => DataMigration.Migration.Configuration.ProductPattern.IsCatchAll(Pattern);

    /// <summary>Prefix form (<c>Spot*</c>), which matches every product starting with the stem.</summary>
    public bool IsPrefix => DataMigration.Migration.Configuration.ProductPattern.IsPrefix(Pattern);

    public bool Matches(string productNum) =>
        DataMigration.Migration.Configuration.ProductPattern.Matches(Pattern, productNum);
}

/// <summary>
/// Key normalisation shared by every lookup keyed on a Xellent contract template. Xellent spells the
/// template with an underscore (<c>V_Privat</c>) while the source tables for the new rules spell it
/// with a space (<c>V Privat</c>); they are the same template, so neither spelling may miss.
///
/// <para>The rule itself lives in
/// <see cref="DataMigration.Migration.Configuration.ContractTemplateKey"/>, because the create
/// path's own template+product contract table keys on the same thing and the two must not disagree
/// about which spellings are one template.</para>
/// </summary>
public static class SettlementConfigKeys
{
    public static string NormalizeTemplate(string template) =>
        DataMigration.Migration.Configuration.ContractTemplateKey.Normalize(template);
}

/// <summary>
/// Candidate Settl segments for one contract template, per customer identification. Settl does not
/// accept a private segment on a CVR number while Xellent holds both kinds of customer under the same
/// template, so the template alone cannot decide.
///
/// <para>Several values for one identification mean that identification still does not determine the
/// segment (V_LadePriv: Eje vs. Leje) — see <see cref="CustomerSegmentGrouper"/>, which turns that
/// into a conflict rather than a guess. An empty list means the template is unmapped for that
/// identification.</para>
/// </summary>
public sealed record SegmentRule(
    IReadOnlyList<string> Cvr,
    IReadOnlyList<string> Cpr,
    IReadOnlyList<string> Blank)
{
    /// <summary>The same candidates regardless of identification — the plain string/array form.</summary>
    public static SegmentRule Uniform(IReadOnlyList<string> segments) => new(segments, segments, segments);

    public IReadOnlyList<string> For(CustomerIdentityKind identity) => identity switch
    {
        CustomerIdentityKind.Cvr => Cvr,
        CustomerIdentityKind.Cpr => Cpr,
        _ => Blank
    };

    public bool IsEmpty => Cvr.Count == 0 && Cpr.Count == 0 && Blank.Count == 0;
}

/// <summary>
/// Loads the fixed mapping that ships with the CLI (<c>settlement-config.json</c>, copied next to the
/// executable). Both maps live in one file because they are filled in from the same run and must
/// always deploy together. The file is part of the solution rather than a command-line argument, so
/// every run — local or deployed — groups by the same names.
///
/// <para><b>Keys are matched case-insensitively.</b> Xellent holds the same plan code in several
/// spellings (<c>Måned.</c> / <c>måned.</c>), which are the same period type — so one entry covers
/// every casing variant rather than the file having to enumerate them.</para>
///
/// <para>Neither an absent file nor an absent key is an error: the raw <c>Billingplannum</c> /
/// <c>Contracttemplatenum</c> is kept as the group name and reported, so the mapping can be completed
/// over time without losing metering points. A file that exists but is malformed still throws: that
/// is a broken mapping, not a missing one.</para>
/// </summary>
public static class SettlementConfigLoader
{
    public const string FileName = "settlement-config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Path the config is loaded from: next to the running executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Reads the config from <paramref name="path"/> (default: <see cref="DefaultPath"/>).
    /// A missing file yields two empty maps.</summary>
    public static SettlementConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        var empty = new SettlementConfig(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SegmentRule>(StringComparer.OrdinalIgnoreCase));
        if (!File.Exists(path)) return empty;

        var raw = JsonSerializer.Deserialize<RawConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Settlement config is invalid JSON: {path}");

        var config = new SettlementConfig(
            ReadSchedules(raw.Schedules),
            ReadSegments(raw.Segments, path),
            ReadSchedules(raw.GroupSchedules),
            ReadSettlSegments(raw.SettlSegments, path),
            ReadTemplateProducts(raw.TemplateProducts, path),
            string.IsNullOrWhiteSpace(raw.MultipleSegments) ? null : raw.MultipleSegments,
            ReadUnresolvedSegments(raw.UnresolvedSegments, path),
            ReadScheduleConflicts(raw.ScheduleConflicts, path));
        if (raw.CustomerExceptions is { ValueKind: not JsonValueKind.Null } exceptions)
        {
            try
            {
                if (exceptions.ValueKind != JsonValueKind.Array) throw new JsonException("Expected an array");
                config = config with { CustomerExceptions = exceptions.Deserialize<List<SettlementCustomerException>>(JsonOptions) };
            }
            catch (JsonException ex) { throw new InvalidOperationException($"Settlement config: customerExceptions must be an array of name/segment/schedule rows ({path})",ex); }
        }
        return config with { CustomerExceptions = SettlementCustomerExceptionRules.Validate(config,path) };
    }

    /// <summary>
    /// The standing answers for exact sets of disagreeing schedules. Each row needs at least two
    /// schedules (a set of one is not a conflict) and a winner; a malformed row throws rather than
    /// being skipped, same as every other decided rule in this file.
    /// </summary>
    private static IReadOnlyList<ScheduleConflictRule>? ReadScheduleConflicts(
        List<JsonElement>? scheduleConflicts, string path)
    {
        if (scheduleConflicts is null || scheduleConflicts.Count == 0) return null;

        var rules = new List<ScheduleConflictRule>();
        foreach (var row in scheduleConflicts)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Settlement config: scheduleConflicts entries must be objects with 'schedules' "
                    + $"and 'winner' ({path})");
            }

            var schedules = ReadNames(row, "schedules", "scheduleConflicts", path);
            var winner = ReadName(row, "winner");

            if (schedules.Count < 2 || string.IsNullOrWhiteSpace(winner))
            {
                throw new InvalidOperationException(
                    $"Settlement config: scheduleConflicts row needs at least two 'schedules' and a "
                    + $"'winner' ({path})");
            }

            rules.Add(new ScheduleConflictRule(schedules, winner));
        }
        return rules.Count > 0 ? rules : null;
    }

    /// <summary>
    /// The standing answers for metering points that produced no schedule row, keyed by the reason
    /// they produced none. An unknown key throws rather than being skipped: a misspelled reason would
    /// otherwise leave those metering points silently unanswered, which looks exactly like a reason
    /// nobody has decided on yet.
    /// </summary>
    private static IReadOnlyDictionary<SettlementConfigUnresolvedReason, SegmentRule>? ReadUnresolvedSegments(
        Dictionary<string, JsonElement>? unresolvedSegments, string path)
    {
        if (unresolvedSegments is null || unresolvedSegments.Count == 0) return null;

        var map = new Dictionary<SettlementConfigUnresolvedReason, SegmentRule>();
        foreach (var (reasonName, value) in unresolvedSegments)
        {
            if (!Enum.TryParse<SettlementConfigUnresolvedReason>(reasonName, ignoreCase: true, out var reason))
            {
                throw new InvalidOperationException(
                    $"Settlement config: unresolvedSegments['{reasonName}'] is not a known reason "
                    + $"({string.Join(", ", Enum.GetNames<SettlementConfigUnresolvedReason>())}) ({path})");
            }

            var rule = value.ValueKind == JsonValueKind.Object
                ? new SegmentRule(
                    ReadNames(value, "cvr", reasonName, path),
                    ReadNames(value, "cpr", reasonName, path),
                    ReadNames(value, "blank", reasonName, path))
                : SegmentRule.Uniform(ReadNameList(value, reasonName, path));

            if (!rule.IsEmpty) map[reason] = rule;
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// The Settl-driven segment rule. Absent section → null: "not configured" and "no customer
    /// matched" are different answers, and only the caller can say which one to report.
    /// </summary>
    private static SettlSegmentRules? ReadSettlSegments(JsonElement? settlSegments, string path)
    {
        if (settlSegments is not { ValueKind: JsonValueKind.Object } value) return null;

        var contracts = ReadNames(value, "smeContracts", "settlSegments", path);
        var sme = ReadName(value, "sme");
        var business = ReadName(value, "business");
        var businessExempt = ReadName(value, "businessExempt");

        // A half-filled rule would silently label customers with an empty segment name, which is
        // indistinguishable in the output from a real one. Better to say what is missing.
        if (contracts.Count == 0 || sme is null || business is null || businessExempt is null)
        {
            throw new InvalidOperationException(
                "Settlement config: settlSegments needs smeContracts, sme, business and businessExempt "
                + $"({path})");
        }

        return new SettlSegmentRules(contracts, sme, business, businessExempt);
    }

    /// <summary>Template → its product patterns, sorted so exact beats prefix beats catch-all.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<TemplateProductPattern>> ReadTemplateProducts(
        Dictionary<string, JsonElement>? templateProducts, string path)
    {
        var map = new Dictionary<string, IReadOnlyList<TemplateProductPattern>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (template, products) in templateProducts ?? [])
        {
            if (products.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Settlement config: templateProducts['{template}'] must be an object of "
                    + $"product → {{ segment, contract }} ({path})");
            }

            var patterns = new List<TemplateProductPattern>();
            foreach (var product in products.EnumerateObject())
            {
                var key = $"{template}.{product.Name}";
                if (product.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"Settlement config: templateProducts['{key}'] must be an object with a "
                        + $"segment and a contract ({path})");
                }

                var segment = ReadName(product.Value, "segment")
                    ?? throw new InvalidOperationException(
                        $"Settlement config: templateProducts['{key}'] needs a segment ({path})");

                // contract: null is meaningful ("follows the erhverv rule"), so a missing or null
                // contract is not an error — only a missing segment is.
                patterns.Add(new TemplateProductPattern(product.Name, segment, ReadName(product.Value, "contract")));
            }

            if (patterns.Count == 0) continue;

            // "V Privat" and "V_Privat" normalise to one key. The file's whole premise is that they
            // are the same template, so having both is a mistake — and letting the later block win
            // silently would drop a set of product rules without a word.
            var templateKey = SettlementConfigKeys.NormalizeTemplate(template);
            if (map.ContainsKey(templateKey))
            {
                throw new InvalidOperationException(
                    $"Settlement config: templateProducts has two entries for the same template "
                    + $"('{template}' collides with an earlier spelling of '{templateKey}') ({path})");
            }

            // Specificity order is fixed here rather than at match time, so a reordered file cannot
            // change which rule wins.
            map[templateKey] = patterns
                .OrderBy(p => DataMigration.Migration.Configuration.ProductPattern.SpecificityRank(p.Pattern))
                .ThenByDescending(p => p.Pattern.Length)
                .ToList();
        }
        return map;
    }

    /// <summary>A single optional string property, case-insensitive on the property name.</summary>
    private static string? ReadName(JsonElement obj, string property)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())
                ? p.Value.GetString()
                : null;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadSchedules(Dictionary<string, string>? schedules)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plan, schedule) in schedules ?? [])
        {
            // An entry mapping to nothing is the same as no entry at all — fall back to the raw code
            // rather than inventing an empty schedule name.
            if (!string.IsNullOrWhiteSpace(schedule)) map[plan] = schedule;
        }
        return map;
    }

    private static IReadOnlyDictionary<string, SegmentRule> ReadSegments(
        Dictionary<string, JsonElement>? segments, string path)
    {
        var map = new Dictionary<string, SegmentRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (template, value) in segments ?? [])
        {
            // Object form = one entry per identification (cvr/cpr/blank); string/array form = the
            // same candidates whatever the customer carries. The short form is kept so a template
            // whose segment genuinely does not depend on the identification needs no ceremony.
            var rule = value.ValueKind == JsonValueKind.Object
                ? new SegmentRule(
                    ReadNames(value, "cvr", template, path),
                    ReadNames(value, "cpr", template, path),
                    ReadNames(value, "blank", template, path))
                : SegmentRule.Uniform(ReadNameList(value, template, path));

            if (rule.IsEmpty) continue;

            // Same premise as templateProducts: "V Privat" and "V_Privat" are one template, so the
            // key is normalised on both sides and having both spellings in the file is a mistake —
            // letting the later block win silently would drop a rule without a word.
            var templateKey = SettlementConfigKeys.NormalizeTemplate(template);
            if (map.ContainsKey(templateKey))
            {
                throw new InvalidOperationException(
                    $"Settlement config: segments has two entries for the same template "
                    + $"('{template}' collides with an earlier spelling of '{templateKey}') ({path})");
            }

            map[templateKey] = rule;
        }
        return map;
    }

    private static IReadOnlyList<string> ReadNames(
        JsonElement rule, string identification, string template, string path)
    {
        // Case-insensitive on the identification key, like every other key in this file.
        foreach (var property in rule.EnumerateObject())
        {
            if (property.NameEquals(identification)
                || string.Equals(property.Name, identification, StringComparison.OrdinalIgnoreCase))
            {
                return ReadNameList(property.Value, $"{template}.{identification}", path);
            }
        }
        return [];
    }

    private static IReadOnlyList<string> ReadNameList(JsonElement value, string key, string path) =>
        value.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => [value.GetString()!],
            JsonValueKind.String => [],
            JsonValueKind.Array => value.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : throw new InvalidOperationException(
                        $"Settlement config: segments['{key}'] must contain only strings ({path})"))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray(),
            _ => throw new InvalidOperationException(
                $"Settlement config: segments['{key}'] must be a string, an array of strings, "
                + $"or an object with cvr/cpr/blank ({path})")
        };

    private sealed class RawConfig
    {
        public Dictionary<string, string>? Schedules { get; set; }
        public Dictionary<string, JsonElement>? Segments { get; set; }
        public Dictionary<string, string>? GroupSchedules { get; set; }
        public JsonElement? SettlSegments { get; set; }
        public Dictionary<string, JsonElement>? TemplateProducts { get; set; }
        public string? MultipleSegments { get; set; }
        public Dictionary<string, JsonElement>? UnresolvedSegments { get; set; }
        public List<JsonElement>? ScheduleConflicts { get; set; }
        public JsonElement? CustomerExceptions { get; set; }
    }
}
