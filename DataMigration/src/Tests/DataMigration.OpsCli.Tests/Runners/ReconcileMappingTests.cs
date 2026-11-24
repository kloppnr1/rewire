using System.Text.Json;
using DataMigration.Migration.Configuration;
using DataMigration.OpsCli.Services.Reconcile;
using Xunit;

namespace DataMigration.OpsCli.Tests.Runners;

public class CreateOptionsFromMappingTests
{
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 100);

    [Fact]
    public void BuildFromMapping_carries_config_and_injected_accounts()
    {
        var mapping = new ReconcileMappingOptions
        {
            DataAreaId = "HJH",
            DeliveryCategory = "El-ekstern",
            LegacyTg4Merge = true,
            GroupOwners = new() { ["G1"] = "100" },
            AccountOwners = new() { ["200"] = "100" },
            Mappings = new MappingOptions
            {
                GroupNum = new() { ["G1"] = "Contract A" },
                AccountNum = new() { ["200"] = "Contract B" },
                SettlementGroup = new() { ["BP1"] = "SG1" },
            },
        };

        var opts = CreateOptionsFactory.BuildFromMapping(
            mapping, ["200", "100", "200"], "production-hev", new DateTime(2026, 1, 14), NoExemption,
            baseIgnoredGroups: [], cos: new DateOnly(2026, 1, 14));

        Assert.Equal("HJH", opts.DataAreaId);
        Assert.Equal("El-ekstern", opts.DeliveryCategory);
        Assert.True(opts.LegacyTg4Merge);
        Assert.True(opts.DryRun); // reconcile is always read-only
        Assert.Equal("production-hev", opts.Env);
        Assert.Equal(new[] { "200", "100" }, opts.Accounts); // injected + de-duplicated
        Assert.Equal("100", opts.GroupOwners!["G1"]);
        Assert.Equal("100", opts.AccountOwners!["200"]);
        Assert.Equal("Contract A", opts.GroupNumContractMap!["G1"]);
        Assert.Equal("Contract B", opts.AccountNumContractMap!["200"]);
        Assert.Equal("SG1", opts.SettlementGroupMap!["BP1"]);
        Assert.Equal(new DateTime(2026, 1, 14), opts.ReferenceDate);
    }
}

public class ReconcileMappingLoaderTests
{
    [Fact]
    public void Loads_mapping_file_by_dataarea()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reconcile-mapping-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "hol.json"), """
            {
              "dataAreaId": "hol",
              "deliveryCategory": "El-ekstern",
              "legacyTg4Merge": true,
              "groupOwners": { "G1": "100" },
              "mappings": { "settlementGroup": { "BP1": "SG1" } }
            }
            """);

            var mapping = new ReconcileMappingLoader(dir).Load("hol");

            Assert.Equal("hol", mapping.DataAreaId);
            Assert.True(mapping.LegacyTg4Merge);
            Assert.Equal("100", mapping.GroupOwners!["G1"]);
            Assert.Equal("SG1", mapping.Mappings!.SettlementGroup!["BP1"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_throws_FileNotFound()
    {
        var loader = new ReconcileMappingLoader(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));
        Assert.Throws<FileNotFoundException>(() => loader.Load("hol"));
    }

    [Fact]
    public void Loads_historical_suppressions_from_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reconcile-mapping-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "hol.json"), """
            {
              "dataAreaId": "hol",
              "deliveryCategory": "El-ekstern",
              "historicalSuppressions": [
                {
                  "cosFrom": "2020-01-01",
                  "cosTo": "2026-04-30",
                  "types": ["MeteringPoint"],
                  "kinds": ["FieldDiff"],
                  "categories": ["Billing reference (EAN/Order/Agreement/BuyerRef)", "Contact / channel"]
                }
              ]
            }
            """);

            var mapping = new ReconcileMappingLoader(dir).Load("hol");

            Assert.NotNull(mapping.HistoricalSuppressions);
            Assert.Single(mapping.HistoricalSuppressions);
            var hs = mapping.HistoricalSuppressions[0];
            Assert.Equal(new DateOnly(2020, 1, 1), hs.CosFrom);
            Assert.Equal(new DateOnly(2026, 4, 30), hs.CosTo);
            Assert.Equal(["MeteringPoint"], hs.Types);
            Assert.Equal(["FieldDiff"], hs.Kinds);
            Assert.Equal(["Billing reference (EAN/Order/Agreement/BuyerRef)", "Contact / channel"], hs.Categories);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class HistoricalSuppressionTests
{
    private static bool Matches(HistoricalSuppression hs, ReconcileDriftRow r)
    {
        var effectiveCos = r.Cos ?? r.CosFrom;
        if (effectiveCos is null) return false;
        if (hs.CosFrom.HasValue && effectiveCos.Value < hs.CosFrom.Value) return false;
        if (hs.CosTo.HasValue && effectiveCos.Value > hs.CosTo.Value) return false;
        var category = DriftCategorizer.Categorize(r);
        if (hs.Types.Length > 0 && Array.IndexOf(hs.Types, r.Entity) < 0) return false;
        if (hs.SubTypes.Length > 0 && Array.IndexOf(hs.SubTypes, r.SubType) < 0
            && !(r.SubType == "" && Array.IndexOf(hs.SubTypes, "null") >= 0)) return false;
        if (hs.Kinds.Length > 0 && Array.IndexOf(hs.Kinds, r.Kind.ToString()) < 0) return false;
        if (hs.Categories.Length > 0 && Array.IndexOf(hs.Categories, category) < 0) return false;
        if (hs.EntityKeys.Length > 0 && Array.IndexOf(hs.EntityKeys, r.EntityKey) < 0) return false;
        return true;
    }

    private static ReconcileDriftRow MakeMpRow(DriftKind kind, string field, DateOnly? cos, string subType = "") =>
        new("000001", "MeteringPoint", "571300000000000001", field, "a", "b", kind, subType, Cos: cos);

    private static HistoricalSuppression MakeHs(DateOnly? cosFrom = null, DateOnly? cosTo = null,
        string[]? types = null, string[]? subTypes = null, string[]? kinds = null, string[]? categories = null,
        string[]? entityKeys = null) =>
        new() { CosFrom = cosFrom, CosTo = cosTo,
            Types = types ?? [], SubTypes = subTypes ?? [], Kinds = kinds ?? [], Categories = categories ?? [],
            EntityKeys = entityKeys ?? [] };

    [Theory]
    [InlineData("2026-04-30", true)]   // on upper bound (inclusive) → suppressed
    [InlineData("2026-05-01", false)]  // one day past upper bound → NOT suppressed
    [InlineData("2020-01-01", true)]   // on lower bound (inclusive) → suppressed
    [InlineData("2019-12-31", false)]  // one day before lower bound → NOT suppressed
    public void Period_bounds_are_inclusive(string cosStr, bool expectSuppressed)
    {
        var hs = MakeHs(cosFrom: new DateOnly(2020, 1, 1), cosTo: new DateOnly(2026, 4, 30));
        var row = MakeMpRow(DriftKind.FieldDiff, "Email", DateOnly.Parse(cosStr));
        Assert.Equal(expectSuppressed, Matches(hs, row));
    }

    [Theory]
    [InlineData("2019-12-31", true)]   // any COS (no lower bound) → suppressed
    [InlineData("2026-05-01", false)]  // past upper bound → NOT suppressed
    public void Null_cos_from_means_no_lower_bound(string cosStr, bool expectSuppressed)
    {
        var hs = MakeHs(cosTo: new DateOnly(2026, 4, 30));
        var row = MakeMpRow(DriftKind.FieldDiff, "Email", DateOnly.Parse(cosStr));
        Assert.Equal(expectSuppressed, Matches(hs, row));
    }

    [Theory]
    [InlineData("2026-05-01", true)]   // any COS (no upper bound) → suppressed
    [InlineData("2019-12-31", false)]  // before lower bound → NOT suppressed
    public void Null_cos_to_means_no_upper_bound(string cosStr, bool expectSuppressed)
    {
        var hs = MakeHs(cosFrom: new DateOnly(2020, 1, 1));
        var row = MakeMpRow(DriftKind.FieldDiff, "Email", DateOnly.Parse(cosStr));
        Assert.Equal(expectSuppressed, Matches(hs, row));
    }

    [Fact]
    public void Customer_row_uses_cos_from_for_effective_cos()
    {
        var row = new ReconcileDriftRow("000001", "Customer", "000001", "Email", "uba@dn.example.dk", "ubb@do.example.dk",
            DriftKind.FieldDiff, CosFrom: new DateOnly(2026, 3, 15));
        Assert.True(Matches(MakeHs(), row));
    }

    [Fact]
    public void Row_with_no_cos_is_never_suppressed()
    {
        var row = MakeMpRow(DriftKind.Anomaly, "settl-extra-contract", cos: null);
        Assert.False(Matches(MakeHs(), row));
    }

    [Fact]
    public void Type_filter_matches_entity()
    {
        var mpRow = MakeMpRow(DriftKind.FieldDiff, "Email", new DateOnly(2026, 1, 1));
        var customerRow = new ReconcileDriftRow("000001", "Customer", "000001", "Email", "a", "b",
            DriftKind.FieldDiff, CosFrom: new DateOnly(2026, 1, 1));
        var hs = MakeHs(types: ["MeteringPoint"]);
        Assert.True(Matches(hs, mpRow));
        Assert.False(Matches(hs, customerRow));
    }

    [Fact]
    public void SubType_filter_matches_row_subtype()
    {
        var billingRow = MakeMpRow(DriftKind.FieldDiff, "ElectronicNumber", new DateOnly(2026, 1, 1), "Billing");
        var coreRow = MakeMpRow(DriftKind.FieldDiff, "ElectronicNumber", new DateOnly(2026, 1, 1), "");
        var hs = MakeHs(subTypes: ["Billing"]);
        Assert.True(Matches(hs, billingRow));
        Assert.False(Matches(hs, coreRow));
    }

    [Fact]
    public void SubType_null_sentinel_matches_core_rows()
    {
        // "null" in SubTypes filter is an alias for SubType="" (core entity fields with no subtype facet).
        var coreRow = MakeMpRow(DriftKind.FieldDiff, "Email", new DateOnly(2026, 1, 1), "");
        var payerLinkRow = MakeMpRow(DriftKind.MissingInSettl, "linked", new DateOnly(2026, 1, 1), "PayerLink");

        var hsNullOnly = MakeHs(subTypes: ["null"]);
        Assert.True(Matches(hsNullOnly, coreRow));       // "null" matches SubType=""
        Assert.False(Matches(hsNullOnly, payerLinkRow)); // "null" does NOT match "PayerLink"

        var hsMixed = MakeHs(subTypes: ["null", "PayerLink"]);
        Assert.True(Matches(hsMixed, coreRow));          // core matched via "null" sentinel
        Assert.True(Matches(hsMixed, payerLinkRow));     // PayerLink matched directly
    }

    [Fact]
    public void Kind_filter_matches_drift_kind()
    {
        var fieldDiff = MakeMpRow(DriftKind.FieldDiff, "Email", new DateOnly(2026, 1, 1));
        var missing = MakeMpRow(DriftKind.MissingInSettl, "Email", new DateOnly(2026, 1, 1));
        var hs = MakeHs(kinds: ["FieldDiff"]);
        Assert.True(Matches(hs, fieldDiff));
        Assert.False(Matches(hs, missing));
    }

    [Fact]
    public void Category_filter_matches_categorizer_output()
    {
        var contactRow = MakeMpRow(DriftKind.FieldDiff, "Email", new DateOnly(2026, 1, 1));
        var settlementRow = MakeMpRow(DriftKind.FieldDiff, "SettlementMethod", new DateOnly(2026, 1, 1));
        var hs = MakeHs(categories: [DriftCategorizer.Contact]);
        Assert.True(Matches(hs, contactRow));
        Assert.False(Matches(hs, settlementRow));
    }

    [Fact]
    public void All_filters_must_match()
    {
        // Types matches but Kinds does not → not suppressed
        var row = MakeMpRow(DriftKind.MissingInSettl, "Email", new DateOnly(2026, 1, 1));
        var hs = MakeHs(types: ["MeteringPoint"], kinds: ["FieldDiff"]);
        Assert.False(Matches(hs, row));
    }

    [Fact]
    public void Empty_filters_match_any_value()
    {
        // All filters empty → any row in the period is suppressed
        var row = MakeMpRow(DriftKind.Anomaly, "settl-extra-contract", new DateOnly(2026, 1, 1));
        Assert.True(Matches(MakeHs(), row));
    }

    [Fact]
    public void EntityKey_filter_matches_specific_entity()
    {
        var matching = MakeMpRow(DriftKind.FieldDiff, "Email", new DateOnly(2026, 1, 1));    // EntityKey = "571300000000000001"
        var other = new ReconcileDriftRow("000001", "MeteringPoint", "571300000000000099", "Email", "a", "b",
            DriftKind.FieldDiff, "", Cos: new DateOnly(2026, 1, 1));
        var hs = MakeHs(entityKeys: ["571300000000000001"]);
        Assert.True(Matches(hs, matching));
        Assert.False(Matches(hs, other));
    }

    [Fact]
    public void EntityKey_filter_empty_matches_all_entities()
    {
        var row1 = MakeMpRow(DriftKind.FieldDiff, "Email", new DateOnly(2026, 1, 1));
        var row2 = new ReconcileDriftRow("000001", "MeteringPoint", "571300000000000099", "Email", "a", "b",
            DriftKind.FieldDiff, "", Cos: new DateOnly(2026, 1, 1));
        var hs = MakeHs();
        Assert.True(Matches(hs, row1));
        Assert.True(Matches(hs, row2));
    }
}

/// <summary>
/// Reconcile computes what create WOULD have produced and calls anything else drift — and since the
/// newest apply path can delete a contract link it believes is wrong, a rule create knows and
/// reconcile does not is not a reporting nuisance: it would take the companies straight back off
/// <c>V ErhVari</c>. The two tables live in two files (appsettings for create, the per-dataarea
/// reconcile mapping here), so nothing but a test keeps them in step.
///
/// <para>The file read is the one under <c>src/profiles/reconcile</c> — the tracked copy the
/// pipeline puts in the artifact. A second, gitignored copy lives at the repo root for local runs;
/// reading that one would pass here and prove nothing about what deploys.</para>
/// </summary>
public class ReconcileMappingMatchesCreateTests
{
    private static readonly BillingExemptionConfig NoExemption = new(null, null, null, 100);

    /// <summary>Template, Xellent product, Settl contract. Nine rows, all CVR-only.</summary>
    public static readonly (string Template, string Product, string Contract)[] Table =
    [
        ("V_Privat",   "V ErhKvt",   "E_ErhKvt"),
        ("V_Privat",   "V P Flex",   "V P Flex"),
        ("V_Privat",   "V P Fast",   "V P Fast"),
        ("V_Privat",   "V ErhVari",  "E_ErhVari"),
        ("V_Privat",   "V Variabel", "E_ErhVari"),
        ("V_Partner",  "V P Fast",   "V P Fast"),
        ("V_Partner",  "V P Hybel",  "Partner_HybelStenhøj"),
        ("V_Fordel",   "V P Flex",   "V P Flex"),
        ("E. ErhProd", "V ProdEl",   "V ProdErhv"),   // the row is "*" — any product on that template
    ];

    /// <summary>Combinations the rule sheet leaves to the ordinary product mapping.</summary>
    public static readonly (string Template, string Product)[] DeliberatelyAbsent =
    [
        ("V_Privat",  "V SMESPot"),
        ("V_Erhverv", "Spot"),
        ("V_Erhverv", "SpotH"),
        ("V_SME",     "SME spot"),
        ("V_Partner", "Spot"),
        ("V_Partner", "SpotH"),
    ];

    public static TheoryData<string, string, string> Rows()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (template, product, contract) in Table) data.Add(template, product, contract);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void The_shipped_reconcile_mapping_carries_the_same_template_rules_as_create(
        string template, string product, string contract)
    {
        var rules = ShippedReconcileRules();

        Assert.Equal(contract, rules.Resolve(template, product, CustomerIdentity.Cvr));
        // The private customers must fall through here exactly as they do in create.
        Assert.Null(rules.Resolve(template, product, CustomerIdentity.Cpr));
        Assert.Null(rules.Resolve(template, product, CustomerIdentity.Blank));
    }

    [Fact]
    public void The_two_files_answer_identically_for_every_probe_that_matters()
    {
        // The real guarantee: reconcile's copy and create's copy are asked the same questions and
        // must not differ on one of them. Anything they disagree about is a link reconcile-apply
        // would delete.
        var reconcile = ShippedReconcileRules();
        var create = ShippedCreateRules();

        var probes = Table.Select(r => (r.Template, r.Product))
            .Concat(DeliberatelyAbsent)
            .Concat([("E. ErhProd", "anything at all"), ("V Privat", "V Variabel")]);

        foreach (var (template, product) in probes)
        foreach (var identity in Enum.GetValues<CustomerIdentity>())
        {
            Assert.Equal(
                create.Resolve(template, product, identity),
                reconcile.Resolve(template, product, identity));
        }
    }

    [Fact]
    public void V_Variabel_on_V_Privat_goes_to_E_ErhVari_here_too()
    {
        // The correction. "V ErhVari" is a Xellent product name, not a Settl contract — a reconcile
        // that still expects it would take every one of those accounts back off E_ErhVari.
        Assert.Equal(
            "E_ErhVari",
            ShippedReconcileRules().Resolve("V_Privat", "V Variabel", CustomerIdentity.Cvr));
    }

    [Fact]
    public void And_reconcile_run_options_actually_carry_it()
    {
        // Binding the file is half of it — a mapping nobody passes on is read and then dropped.
        var mapping = LoadShippedMapping();

        var opts = CreateOptionsFactory.BuildFromMapping(
            mapping, ["102560"], "featuretest", new DateTime(2026, 1, 1), NoExemption,
            baseIgnoredGroups: [], cos: new DateOnly(2026, 1, 1));

        var rules = TemplateProductContractRules.Build(opts.TemplateProductContractMap)!;
        foreach (var (template, product, contract) in Table)
            Assert.Equal(contract, rules.Resolve(template, product, CustomerIdentity.Cvr));
    }

    private static ReconcileMappingOptions LoadShippedMapping() =>
        new ReconcileMappingLoader(Path.Combine(RepoRoot(), "src", "profiles", "reconcile")).Load("hol");

    private static TemplateProductContractRules ShippedReconcileRules() =>
        TemplateProductContractRules.Build(LoadShippedMapping().Mappings?.TemplateProducts)
        ?? throw new InvalidOperationException("hol.json carries no templateProducts table.");

    /// <summary>
    /// Create's own copy, read straight out of the appsettings that deploys — parsed with
    /// System.Text.Json so this test needs nothing from the configuration stack.
    /// </summary>
    private static TemplateProductContractRules ShippedCreateRules()
    {
        var path = Path.Combine(RepoRoot(), "src", "DataMigration.Migration", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var table = doc.RootElement.GetProperty("Mappings").GetProperty("TemplateProducts");

        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, TemplateProductContractEntry>>>(
            table.GetRawText())!;

        return TemplateProductContractRules.Build(raw)
            ?? throw new InvalidOperationException("appsettings.json carries no TemplateProducts table.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DataMigration.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            "Repo root not found from " + AppContext.BaseDirectory);
    }
}
