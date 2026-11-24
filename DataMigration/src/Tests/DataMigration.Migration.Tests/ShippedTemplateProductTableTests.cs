using DataMigration.Migration.Configuration;
using Microsoft.Extensions.Configuration;

namespace DataMigration.Migration.Tests;

/// <summary>
/// The business-customer table itself, as it ships. Every row here came off the migration rule sheet
/// ("Kunde med CVR-nr"), so every row is CVR-only: a private customer on the same template and
/// product must keep falling through to the ordinary product mapping.
///
/// <para>The file read is <c>src/DataMigration.Migration/appsettings.json</c> — the one that deploys.
/// A test that re-types the table into a string literal proves only that the literal is consistent
/// with itself.</para>
/// </summary>
public class ShippedTemplateProductTableTests
{
    /// <summary>Template, Xellent product, Settl contract. Twelve rows, all CVR-only.</summary>
    public static readonly (string Template, string Product, string Contract)[] Table =
    [
        ("V_Privat",   "V ErhKvt",   "E_ErhKvt"),
        ("V_Privat",   "V P Flex",   "V P Flex"),
        ("V_Privat",   "V P Fast",   "V P Fast"),
        ("V_Privat",   "V ErhVari",  "E_ErhVari"),
        ("V_Privat",   "V Variabel", "E_ErhVari"),
        // "V Kvartal" is a separate Xellent product from "V ErhKvt"; a company holding it belongs on
        // the same Settl contract, while the ~19.3k private customers keep the product name.
        ("V_Privat",   "V Kvartal",  "E_ErhKvt"),
        ("M_Erhverv",  "V Kvartal",  "E_ErhKvt"),
        ("V_Erhverv",  "V Kvartal",  "E_ErhKvt"),
        ("V_Partner",  "V P Fast",   "V P Fast"),
        ("V_Partner",  "V P Hybel",  "Partner_HybelStenhøj"),
        ("V_Fordel",   "V P Flex",   "V P Flex"),
        ("E. ErhProd", "V ProdEl",   "V ProdErhv"),   // the row is "*" — any product on that template
    ];

    /// <summary>
    /// Combinations the rule sheet marks "samme migreringsregel som erhverv" — the ordinary product
    /// mapping already puts them right, so the table must stay silent about them.
    /// </summary>
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

    public static TheoryData<string, string> Absent()
    {
        var data = new TheoryData<string, string>();
        foreach (var (template, product) in DeliberatelyAbsent) data.Add(template, product);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_row_of_the_table_is_in_the_shipped_appsettings(
        string template, string product, string contract)
    {
        Assert.Equal(contract, ShippedRules().Resolve(template, product, CustomerIdentity.Cvr));
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void And_none_of_it_touches_a_customer_without_a_CVR(string template, string product, string _)
    {
        var rules = ShippedRules();
        Assert.Null(rules.Resolve(template, product, CustomerIdentity.Cpr));
        Assert.Null(rules.Resolve(template, product, CustomerIdentity.Blank));
    }

    [Theory]
    [MemberData(nameof(Absent))]
    public void The_combinations_the_rule_sheet_leaves_to_the_product_mapping_stay_unmapped(
        string template, string product)
    {
        Assert.Null(ShippedRules().Resolve(template, product, CustomerIdentity.Cvr));
    }

    [Fact]
    public void V_Variabel_on_V_Privat_goes_to_E_ErhVari_because_V_ErhVari_is_not_a_Settl_contract()
    {
        // The correction. "V ErhVari" is a Xellent PRODUCT name; the Settl contract is E_ErhVari.
        // Shipping the product name meant every one of those accounts was refused at link time.
        var rules = ShippedRules();

        Assert.Equal("E_ErhVari", rules.Resolve("V_Privat", "V Variabel", CustomerIdentity.Cvr));

        // And nowhere else either — the old value must be gone, not merely overwritten in one row.
        foreach (var (template, product, _) in Table)
            Assert.NotEqual("V ErhVari", rules.Resolve(template, product, CustomerIdentity.Cvr));
    }

    [Fact]
    public void The_Danish_o_in_the_partner_contract_survives_the_file_encoding()
    {
        // A mangled ø is a contract name Settl does not know, and the account is refused. The lookup
        // in ContractNameResolver is an ordinal HashSet, so this is exact-match or nothing.
        Assert.Equal(
            "Partner_HybelStenhøj",
            ShippedRules().Resolve("V_Partner", "V P Hybel", CustomerIdentity.Cvr));
    }

    [Fact]
    public void The_catch_all_row_covers_any_product_on_its_template_and_no_other_template()
    {
        var rules = ShippedRules();

        Assert.Equal("V ProdErhv", rules.Resolve("E. ErhProd", "anything at all", CustomerIdentity.Cvr));
        Assert.Equal("V ProdErhv", rules.Resolve("E._ErhProd", "V Variabel", CustomerIdentity.Cvr));
        Assert.Null(rules.Resolve("V_Privat", "anything at all", CustomerIdentity.Cvr));
    }

    private static TemplateProductContractRules ShippedRules()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "src", "DataMigration.Migration", "appsettings.json"))
            .Build();

        var mappings = BaseMappingLoader.Load(config);
        return TemplateProductContractRules.Build(mappings!.TemplateProducts)
            ?? throw new InvalidOperationException("appsettings.json carries no TemplateProducts table.");
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DataMigration.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            "Repo root not found from " + AppContext.BaseDirectory);
    }
}
