using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SettlementConfigLoaderTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Loads_both_maps_from_one_file()
    {
        var path = WriteTemp("""
            {
              "schedules": { "KvartalOpg": "Kvartalsvis Opgørelse" },
              "segments": {
                "V_Privat": "Privat",
                "V_LadePriv": [ "Privat_Opladning_Eje", "Privat_Opladning_Leje" ]
              }
            }
            """);
        try
        {
            var config = SettlementConfigLoader.Load(path);

            Assert.Equal("Kvartalsvis Opgørelse", config.Schedules["KvartalOpg"]);
            // The short form applies whatever the customer carries.
            Assert.Equal(["Privat"], config.Segments["V_Privat"].For(CustomerIdentityKind.Cvr));
            Assert.Equal(["Privat"], config.Segments["V_Privat"].For(CustomerIdentityKind.Blank));
            // A list value stays ambiguous — the grouper turns that into a conflict, not a guess.
            Assert.Equal(["Privat_Opladning_Eje", "Privat_Opladning_Leje"],
                config.Segments["V_LadePriv"].For(CustomerIdentityKind.Cpr));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Keys_match_case_insensitively_so_one_entry_covers_every_xellent_spelling()
    {
        var path = WriteTemp("""
            { "schedules": { "Måned.": "Måned." }, "segments": { "V_Privat": "Privat" } }
            """);
        try
        {
            var config = SettlementConfigLoader.Load(path);

            Assert.Equal("Måned.", config.Schedules["måned."]);
            Assert.Equal(["Privat"], config.Segments["v_privat"].For(CustomerIdentityKind.Cpr));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_yields_empty_maps_so_everything_falls_back_to_the_raw_xellent_code()
    {
        var config = SettlementConfigLoader.Load(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json"));

        Assert.Empty(config.Schedules);
        Assert.Empty(config.Segments);
    }

    [Fact]
    public void Missing_section_is_the_same_as_an_empty_one()
    {
        var path = WriteTemp("""{ "segments": { "V_Privat": "Privat" } }""");
        try
        {
            var config = SettlementConfigLoader.Load(path);

            Assert.Empty(config.Schedules);
            Assert.Single(config.Segments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Space_and_underscore_spellings_are_one_segment_entry()
    {
        // Xellent holds both spellings of the same template. Whichever way the file is written, the
        // other spelling must find the entry — otherwise the customer is reported as unmapped for a
        // template that is plainly in the file.
        var path = WriteTemp("""
            { "segments": { "V Privat": "Privat", "V_SME": "SME" } }
            """);
        try
        {
            var config = SettlementConfigLoader.Load(path);

            Assert.Equal(["Privat"],
                config.Segments[SettlementConfigKeys.NormalizeTemplate("V_Privat")].For(CustomerIdentityKind.Cpr));
            Assert.Equal(["SME"],
                config.Segments[SettlementConfigKeys.NormalizeTemplate("V SME")].For(CustomerIdentityKind.Cvr));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Two_spellings_of_the_same_template_in_segments_throws()
    {
        // Silently letting the later block win would drop a rule without a word.
        var path = WriteTemp("""
            { "segments": { "V Privat": "Privat", "V_Privat": "Erhverv" } }
            """);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SettlementConfigLoader.Load(path));
            Assert.Contains("V_Privat", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Malformed_file_throws()
    {
        var path = WriteTemp("{ not json");
        try
        {
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => SettlementConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Segment_value_that_is_neither_string_nor_array_throws()
    {
        var path = WriteTemp("""{ "segments": { "V_Privat": 42 } }""");
        try
        {
            Assert.Throws<InvalidOperationException>(() => SettlementConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_values_are_treated_as_no_entry()
    {
        var path = WriteTemp("""
            {
              "schedules": { "Måned.": "" },
              "segments": { "V_Privat": [], "V_SME": { "cvr": "", "cpr": [] } }
            }
            """);
        try
        {
            var config = SettlementConfigLoader.Load(path);

            Assert.Empty(config.Schedules);
            Assert.Empty(config.Segments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Segment_can_depend_on_the_customers_identification()
    {
        var path = WriteTemp("""
            {
              "segments": {
                "V_Privat": { "cvr": "Erhverv", "cpr": "Privat", "blank": "Privat" },
                "V_LadePriv": {
                  "cvr": "Erhverv",
                  "cpr": [ "Privat_Opladning_Eje", "Privat_Opladning_Leje" ],
                  "blank": [ "Privat_Opladning_Eje", "Privat_Opladning_Leje" ]
                }
              }
            }
            """);
        try
        {
            var config = SettlementConfigLoader.Load(path);

            var privat = config.Segments["V_Privat"];
            Assert.Equal(["Erhverv"], privat.For(CustomerIdentityKind.Cvr));
            Assert.Equal(["Privat"], privat.For(CustomerIdentityKind.Cpr));
            Assert.Equal(["Privat"], privat.For(CustomerIdentityKind.Blank));

            // A CVR customer on the charging template is decided; a person is not.
            Assert.Equal(["Erhverv"], config.Segments["V_LadePriv"].For(CustomerIdentityKind.Cvr));
            Assert.Equal(2, config.Segments["V_LadePriv"].For(CustomerIdentityKind.Cpr).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScheduleConflicts_parses_rows_of_two_or_more_schedules()
    {
        var path = WriteTemp("""
            {
              "scheduleConflicts": [
                { "schedules": ["KvartalAC", "Måned."], "winner": "KvartalAC" },
                { "schedules": ["HalvårBagud", "KvartalAC", "Md+MdAC", "Måned."], "winner": "KvartalAC" }
              ]
            }
            """);
        try
        {
            var rules = SettlementConfigLoader.Load(path).ScheduleConflicts;

            Assert.NotNull(rules);
            Assert.Equal(2, rules!.Count);
            Assert.Equal(["KvartalAC", "Måned."], rules[0].Schedules);
            Assert.Equal("KvartalAC", rules[0].Winner);
            Assert.Equal(4, rules[1].Schedules.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScheduleConflicts_row_needs_at_least_two_schedules_and_a_winner()
    {
        var path = WriteTemp("""{ "scheduleConflicts": [ { "schedules": ["KvartalAC"], "winner": "KvartalAC" } ] }""");
        try
        {
            Assert.Throws<InvalidOperationException>(() => SettlementConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Identification_missing_from_an_entry_leaves_that_case_unmapped()
    {
        // Half a rule must not silently fall back to the other half: the grouper reports the
        // template as unmapped instead, which is what "we have not decided this case" looks like.
        var path = WriteTemp("""{ "segments": { "V_Privat": { "cvr": "Erhverv" } } }""");
        try
        {
            var rule = SettlementConfigLoader.Load(path).Segments["V_Privat"];

            Assert.Equal(["Erhverv"], rule.For(CustomerIdentityKind.Cvr));
            Assert.Empty(rule.For(CustomerIdentityKind.Cpr));
            Assert.Empty(rule.For(CustomerIdentityKind.Blank));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Shipped_config_parses_and_covers_the_known_templates()
    {
        // The file is copied next to the test binary only when the CLI itself is the host, so fall
        // back to the source copy when running tests.
        var path = File.Exists(SettlementConfigLoader.DefaultPath)
            ? SettlementConfigLoader.DefaultPath
            : Path.Combine(RepoRoot(), "src", "DataMigration.OpsCli", SettlementConfigLoader.FileName);

        var config = SettlementConfigLoader.Load(path);

        // Settl takes no private segment on a CVR number, so the same template splits by
        // identification — this is the whole point of the two-dimensional map.
        Assert.Equal("Privat", Assert.Single(config.Segments["V_Privat"].For(CustomerIdentityKind.Cpr)));
        Assert.Equal("Erhverv", Assert.Single(config.Segments["V_Privat"].For(CustomerIdentityKind.Cvr)));
        Assert.Equal("Erhverv_UG", Assert.Single(config.Segments["M_Erh_UG"].For(CustomerIdentityKind.Cvr)));
        Assert.Equal("Privat", Assert.Single(config.Segments["M_Erh_UG"].For(CustomerIdentityKind.Cpr)));
        // Eje vs. Leje comes from the linked Settl charger contract, not this template.
        Assert.Equal(["Privat_Opladning_Eje", "Privat_Opladning_Leje"],
            config.Segments["V_LadePriv"].For(CustomerIdentityKind.Cpr));
        // The company on the charging template gets no charging contract (Janni 2026-09-08, private
        // side only): Erhverv is not a charging segment, so it lands on the same "no mapping" outcome
        // used elsewhere, instead of being guessed into one.
        Assert.Equal("Erhverv_Uden_Kontrakt",
            Assert.Single(config.Segments["V_LadePriv"].For(CustomerIdentityKind.Cvr)));

        // Customers whose metering points each determine a segment, but not the same one, have a
        // standing answer in the file instead of landing on the conflict list.
        Assert.Equal("Erhverv", config.MultipleSegments);

        // Metering points with no Xellent contract have no template to look up, so their segment can
        // only come from the file. Both reasons are answered (decided 2026-08-11); the private
        // consumer is the one exception on the second.
        var unresolvedRules = Assert.IsAssignableFrom<
            IReadOnlyDictionary<SettlementConfigUnresolvedReason, SegmentRule>>(config.UnresolvedSegments);
        Assert.Equal("Erhverv", Assert.Single(
            unresolvedRules[SettlementConfigUnresolvedReason.NotFoundInXellent].For(CustomerIdentityKind.Cpr)));
        Assert.Equal("Erhverv", Assert.Single(
            unresolvedRules[SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier].For(CustomerIdentityKind.Cvr)));
        Assert.Equal("Privat", Assert.Single(
            unresolvedRules[SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier].For(CustomerIdentityKind.Cpr)));

        // Every other template must answer all three cases, or customers silently land in an unmapped
        // group named after the raw template.
        Assert.All(config.Segments.Where(kv => kv.Key != "V_LadePriv"), kv =>
        {
            Assert.NotEmpty(kv.Value.For(CustomerIdentityKind.Cvr));
            Assert.NotEmpty(kv.Value.For(CustomerIdentityKind.Cpr));
            Assert.NotEmpty(kv.Value.For(CustomerIdentityKind.Blank));
        });

        // Both casing variants seen in Xellent resolve to one schedule — the file carries a single
        // entry and the case-insensitive lookup covers the rest.
        Assert.Equal("Måned.", config.Schedules["Måned."]);
        Assert.Equal("Måned.", config.Schedules["måned."]);
        // Two distinct plan codes deliberately share one Settl schedule.
        Assert.Equal("HalvårBagud", config.Schedules["½År_Bagud"]);
        Assert.Equal("HelårBagud", config.Schedules["EnergiRds"]);
        Assert.Equal("KvartalOpg", config.Schedules["Kvartal"]);
        Assert.Equal("Md+3MdAC", config.Schedules["Erh_4_FU"]);

        Assert.All(config.Schedules, kv =>
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Key));
            Assert.False(string.IsNullOrWhiteSpace(kv.Value));
        });

        // Janni/Lone, 2026-09-08: the eight decided combinations of the four disputed schedules.
        Assert.NotNull(config.ScheduleConflicts);
        Assert.Equal(8, config.ScheduleConflicts!.Count);
        var winners = config.ScheduleConflicts.ToDictionary(
            r => string.Join("+", r.Schedules.OrderBy(s => s, StringComparer.Ordinal)),
            r => r.Winner);
        Assert.Equal("KvartalAC", winners["KvartalAC+Måned."]);
        Assert.Equal("HalvårBagud", winners["HalvårBagud+KvartalAC"]);
        Assert.Equal("Md+MdAC", winners["Md+MdAC+Måned."]);
        Assert.Equal("HalvårBagud", winners["HalvårBagud+Md+MdAC"]);
        Assert.Equal("HalvårBagud", winners["HalvårBagud+Måned."]);
        Assert.Equal("KvartalAC", winners["KvartalAC+Md+MdAC"]);
        Assert.Equal("KvartalAC", winners["KvartalAC+Md+MdAC+Måned."]);
        Assert.Equal("KvartalAC", winners["HalvårBagud+KvartalAC+Md+MdAC+Måned."]);
    }

    // ---- Candidate rules (groupSchedules / settlSegments / templateProducts) ----

    [Fact]
    public void Candidate_sections_are_optional_so_the_old_file_still_loads()
    {
        var path = WriteTemp("""{ "schedules": { "Kvartal": "KvartalOpg" } }""");
        try
        {
            var config = SettlementConfigLoader.Load(path);

            Assert.Empty(config.GroupSchedules!);
            Assert.Null(config.SettlSegments);
            Assert.Empty(config.TemplateProducts!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Half_filled_settlSegments_throws_rather_than_producing_a_blank_segment_name()
    {
        // A missing name would label customers with an empty string, indistinguishable in the
        // output from a real segment.
        var path = WriteTemp("""
            { "settlSegments": { "smeContracts": [ "E_SME_Spot_Lb+1d" ], "sme": "SME" } }
            """);
        try
        {
            Assert.Throws<InvalidOperationException>(() => SettlementConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Template_product_patterns_are_ordered_exact_before_prefix_before_catch_all()
    {
        // Written deliberately in the wrong order: the file's order must not decide which rule wins.
        var path = WriteTemp("""
            {
              "templateProducts": {
                "V Privat": {
                  "*":        { "segment": "Catch",  "contract": null },
                  "Spot*":    { "segment": "Prefix", "contract": null },
                  "SpotH":    { "segment": "Exact",  "contract": "E_X" }
                }
              }
            }
            """);
        try
        {
            var config = SettlementConfigLoader.Load(path);
            // The space spelling in the file is normalised to Xellent's underscore spelling.
            var patterns = config.TemplateProducts!["V_Privat"];

            Assert.Equal("Exact", patterns.First(p => p.Matches("SpotH")).Segment);
            Assert.Equal("Prefix", patterns.First(p => p.Matches("SpotXY")).Segment);
            Assert.Equal("Catch", patterns.First(p => p.Matches("noget helt andet")).Segment);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Template_product_row_without_a_segment_throws_but_a_null_contract_is_allowed()
    {
        var missingSegment = WriteTemp("""
            { "templateProducts": { "V Privat": { "V ErhKvt": { "contract": "E_ErhKvt" } } } }
            """);
        var nullContract = WriteTemp("""
            { "templateProducts": { "V Privat": { "V ErhKvt": { "segment": "SME", "contract": null } } } }
            """);
        try
        {
            Assert.Throws<InvalidOperationException>(() => SettlementConfigLoader.Load(missingSegment));

            // A null contract is the table's "samme migreringsregel som erhverv" — meaningful, not
            // an omission.
            var rule = Assert.Single(SettlementConfigLoader.Load(nullContract).TemplateProducts!["V_Privat"]);
            Assert.Equal("SME", rule.Segment);
            Assert.Null(rule.Contract);
        }
        finally
        {
            File.Delete(missingSegment);
            File.Delete(nullContract);
        }
    }

    [Fact]
    public void Shipped_config_carries_the_candidate_rules()
    {
        var path = File.Exists(SettlementConfigLoader.DefaultPath)
            ? SettlementConfigLoader.DefaultPath
            : Path.Combine(RepoRoot(), "src", "DataMigration.OpsCli", SettlementConfigLoader.FileName);

        var config = SettlementConfigLoader.Load(path);

        // All eight rows of the settlement-group table.
        Assert.Equal(8, config.GroupSchedules!.Count);
        Assert.Equal("HelårBagud", config.GroupSchedules["Årlig opgørelse"]);
        Assert.Equal("Md+3MdAC", config.GroupSchedules["Månedsopg. + 4 md a'c"]);
        Assert.Equal("Md+MdAC", config.GroupSchedules["MånedOpg+MånedAconto"]);
        // Two groups deliberately share one schedule.
        Assert.Equal("Måned.", config.GroupSchedules["PPA - afregning"]);
        Assert.Equal("Måned.", config.GroupSchedules["Månedlige opgørelser"]);

        Assert.NotNull(config.SettlSegments);
        Assert.Equal(3, config.SettlSegments!.SmeContracts.Count);
        Assert.Equal("Erhverv_UG", config.SettlSegments.BusinessExempt);

        // Template keys are stored underscore-normalised, so Xellent's spelling hits them directly.
        Assert.Contains("V_Privat", config.TemplateProducts!.Keys);
        Assert.Equal(
            "Erhverv_Uden_Kontrakt",
            config.TemplateProducts["V_Privat"].First(p => p.Matches("V ErhKvt")).Segment);
        // "Samme migreringsregel som erhverv" rows carry a segment but no contract.
        Assert.Null(config.TemplateProducts["V_Erhverv"].First(p => p.Matches("Spot Lb+1d")).Contract);

        Assert.All(config.TemplateProducts.Values.SelectMany(v => v), p =>
            Assert.False(string.IsNullOrWhiteSpace(p.Segment)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DataMigration.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root not found from " + AppContext.BaseDirectory);
    }
}
