using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementCustomerExceptionRulesTests
{
    private static string Json(string rows) => """
        {"schedules":{"q":"KvartalAC","h":"HalvårBagud"},"segments":{"p":"Partner","e":"Erhverv"},"customerExceptions":
        """ + rows + "}";
    private static SettlementConfig Load(string json)
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path,json); return SettlementConfigLoader.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Normalizes_patterns_and_targets_and_exact_then_longest_prefix_wins()
    {
        var config = Load(Json("""
            [{"name":"Company*","segment":"Partner","schedule":"KvartalAC"},
             {"name":" Company   North * ","segment":" partner ","schedule":"HALVÅRBAGUD"},
             {"name":"Company North A/S","segment":"Erhverv","schedule":"KvartalAC"}]
            """));
        Assert.Equal("Company North*",config.CustomerExceptions![1].Name);
        Assert.Equal("HalvårBagud",config.CustomerExceptions[1].Schedule);
        Assert.Equal("Partner",config.CustomerExceptions[1].Segment);
        Assert.Equal("Erhverv",SettlementCustomerExceptionRules.Match(config.CustomerExceptions," company\t north  a/s ")!.Segment);
        Assert.Equal("HalvårBagud",SettlementCustomerExceptionRules.Match(config.CustomerExceptions,"Company North branch")!.Schedule);
        Assert.Equal("KvartalAC",SettlementCustomerExceptionRules.Match(config.CustomerExceptions,"Company South")!.Schedule);
        Assert.Null(SettlementCustomerExceptionRules.Match(config.CustomerExceptions,"Companyville"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Empty_optional_section_has_no_hidden_Hybel_fallback(string rows)
    {
        Assert.Null(SettlementCustomerExceptionRules.Match(Load(Json(rows)).CustomerExceptions,"Hybel Syd A/S"));
        var old = Load("{\"schedules\":{},\"segments\":{}}");
        Assert.Null(old.CustomerExceptions);
        Assert.Null(SettlementCustomerExceptionRules.Match(old.CustomerExceptions,"Hybel Syd"));
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("{}")]
    [InlineData("[{\"name\":\"X\",\"segment\":\"Partner\"}]")]
    [InlineData("[{\"name\":\"*\",\"segment\":\"Partner\",\"schedule\":\"KvartalAC\"}]")]
    [InlineData("[{\"name\":\"X**\",\"segment\":\"Partner\",\"schedule\":\"KvartalAC\"}]")]
    [InlineData("[{\"name\":\"X*Y\",\"segment\":\"Partner\",\"schedule\":\"KvartalAC\"}]")]
    [InlineData("[{\"name\":\"X\",\"segment\":\"Other\",\"schedule\":\"KvartalAC\"}]")]
    [InlineData("[{\"name\":\"X\",\"segment\":\"Partner\",\"schedule\":\"Other\"}]")]
    [InlineData("[{\"name\":\"X *\",\"segment\":\"Partner\",\"schedule\":\"KvartalAC\"},{\"name\":\" x* \",\"segment\":\"Erhverv\",\"schedule\":\"HalvårBagud\"}]")]
    public void Rejects_invalid_configuration_with_section_named(string rows) =>
        Assert.Contains("customerExceptions",Assert.Throws<InvalidOperationException>(() => Load(Json(rows))).Message);

    [Theory]
    [InlineData("Erhverv_ProduktionE18_UG")]
    [InlineData("Privat_Opladning_Eje")]
    [InlineData("Privat_Opladning_Leje")]
    public void Accepts_builtin_dated_and_charging_targets(string segment) =>
        Assert.Equal(segment,Assert.Single(Load(Json($"[{{\"name\":\"X\",\"segment\":\"{segment}\",\"schedule\":\"KvartalAC\"}}]")).CustomerExceptions!).Segment);

    [Fact]
    public void Reload_reads_updated_rules_without_cached_defaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,Json("[{\"name\":\"X*\",\"segment\":\"Partner\",\"schedule\":\"KvartalAC\"}]"));
            Assert.Equal("Partner",SettlementCustomerExceptionRules.Match(SettlementConfigLoader.Load(path).CustomerExceptions,"X A/S")!.Segment);
            File.WriteAllText(path,Json("[{\"name\":\"X*\",\"segment\":\"Erhverv\",\"schedule\":\"HalvårBagud\"}]"));
            Assert.Equal("Erhverv",SettlementCustomerExceptionRules.Match(SettlementConfigLoader.Load(path).CustomerExceptions,"X A/S")!.Segment);
        }
        finally { File.Delete(path); }
    }
}
