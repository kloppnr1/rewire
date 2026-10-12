using DataMigration.OpsCli.Services;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementContractPartResolverTests
{
    private const string Mp1 = "571313179104952730";
    private const string Mp2 = "571313179104952731";
    private static readonly DateTime Open = new(1900,1,1);
    private static SettlementContractPartResolver.PartRow Row(string mp = Mp1, string account = "100", string contract = "C1",
        string product = "P1", DateTime? end = null, string source = "Live", DateTime? log = null, long record = 1) =>
        new(mp,account,contract,new(2020,1,1),new(2026,8,31),"month","private","FOR","A1","el-ekstern",
            product,"",new(2020,1,1),end ?? Open,0,"",source,log,record);

    [Fact]
    public void Log_only_part_before_its_deletion_links_the_historical_customer_and_products()
    {
        var result = SettlementContractPartResolver.Fold(new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },
            [Row(source:"ContractPartLog",log:new(2026,9,2),record:123)],new HashSet<string> { Mp1 });
        Assert.Equal((Mp1,"100"),Assert.Single(result.Resolution.Pairs));
        Assert.Equal("P1",Assert.Single(result.Resolution.ProductNumsByMpId![Mp1]));
        Assert.Equal("ContractPartLog",Assert.Single(result.Evidence).Source);
        Assert.Equal(123,Assert.Single(result.Evidence).LogRecordId);
        Assert.Empty(result.ConflictingMeteringPoints);
    }

    [Fact]
    public void Earliest_change_after_the_reference_day_restores_the_prechange_state_once()
    {
        var result = SettlementContractPartResolver.Fold(new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },
            [Row(end:new(2026,8,1)), Row(source:"ContractPartLog",log:new(2026,9,2),record:3),
             Row(source:"ContractPartLog",log:new(2026,9,2),record:4,end:new(2026,8,1)),
             Row(source:"ContractPartLog",log:new(2026,10,2),record:5,end:new(2026,8,1))],new HashSet<string> { Mp1 });
        Assert.Single(result.Resolution.Pairs);
        Assert.Single(result.Resolution.ProductNumsByMpId![Mp1]);
        Assert.Equal(3,Assert.Single(result.Evidence).LogRecordId);
    }

    [Fact]
    public void Old_deletion_log_does_not_resurrect_a_point_deleted_before_the_reference_day()
    {
        var result = SettlementContractPartResolver.Fold(new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },
            [Row(source:"ContractPartLog",log:new(2026,8,1))],new HashSet<string> { Mp1 });
        Assert.Empty(result.Resolution.Pairs);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void Multiple_owners_are_unresolved_with_both_contracts_reported()
    {
        var result = SettlementContractPartResolver.Fold(new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },
            [Row(), Row(account:"200",contract:"C2",source:"ContractPartLog",log:new(2026,9,2))],new HashSet<string> { Mp1 });
        Assert.Empty(result.Resolution.Pairs);
        Assert.Contains(Mp1,result.ConflictingMeteringPoints);
        Assert.Equal(2,result.Evidence.Count);
    }

    [Fact]
    public void Missing_log_key_columns_with_multiple_live_matches_are_reported_as_ambiguous()
    {
        var result = SettlementContractPartResolver.Fold(new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },
            [Row() with { SubReferenceId = "1" }, Row() with { SubReferenceId = "2" },
             Row(source:"ContractPartLog",log:new(2026,9,2))],new HashSet<string> { Mp1 });
        Assert.Empty(result.Resolution.Pairs);
        Assert.Contains(Mp1,result.ConflictingMeteringPoints);
    }

    [Fact]
    public void Mixed_dates_resolve_independently_and_unlisted_rows_are_ignored()
    {
        var dates = new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1), [Mp2] = new(2026,10,1) };
        var result = SettlementContractPartResolver.Fold(dates,
            [Row(), Row(mp:Mp2,account:"200",contract:"C2") with { ContractEnd = new(2026,9,30) },
             Row(mp:"571313179104952733",account:"300")],new HashSet<string> { Mp1,Mp2 });
        Assert.Equal(2,result.Resolution.Pairs.Count);
        Assert.Equal(new DateOnly(2026,8,31),result.Evidence.Single(e => e.MeteringPointIdentification == Mp1).ReferenceDate);
        Assert.Equal(new DateOnly(2026,9,30),result.Evidence.Single(e => e.MeteringPointIdentification == Mp2).ReferenceDate);
        Assert.DoesNotContain(result.Evidence,e => !dates.ContainsKey(e.MeteringPointIdentification));
    }

    [Fact]
    public void Conflicting_log_only_states_cannot_identify_the_missing_live_subreferences()
    {
        var result = SettlementContractPartResolver.Fold(new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },
            [Row(source:"ContractPartLog",log:new(2026,9,2),record:1),
             Row(source:"ContractPartLog",log:new(2026,9,3),record:2,end:new(2026,8,31))],new HashSet<string> { Mp1 });
        Assert.Empty(result.Resolution.Pairs);
        Assert.Contains(Mp1,result.ConflictingMeteringPoints);
        Assert.Equal(2,result.Evidence.Count);
    }

    [Theory]
    [InlineData("El-ekstern",true)]
    [InlineData("Charge_Abo",false)]
    public async Task Live_and_log_joins_require_the_agreements_delivery_category(string agreementCategory,bool expected)
    {
        await using var context = new XellentDbContext(new DbContextOptionsBuilder<XellentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        context.ExuContractTables.Add(Defaults(new ExuContractTable { Contractnum = "C1",Dataareaid = "hol",Companyid = "FOR",
            Custaccount = "100",Contractstartdate = new(2020,1,1),Contractenddate = new(2026,8,31),Billingplannum = "month",Contracttemplatenum = "private" }));
        context.ExuAgreementTables.Add(Defaults(new ExuAgreementTable { Agreementnum = "A1",Dataareaid = "hol",Companyid = "FOR",Deliverycategory = agreementCategory }));
        context.ExuDelpoints.Add(Defaults(new ExuDelpoint { Num = "D1",Attachmentnum = "A1",Dataareaid = "hol",Companyid = "FOR",Deliverycategory = "El-ekstern",Meteringpoint = Mp1 }));
        context.ExuContractPartTables.Add(Defaults(new ExuContractPartTable { Contractnum = "C1",Instagreenum = "A1",Dataareaid = "hol",Companyid = "FOR",
            Deliverycategory = "El-ekstern",Productnum = "P1",Startdate = new(2020,1,1),Enddate = Open,Recid = 1 }));
        context.ExuContractPartLogs.Add(Defaults(new ExuContractPartLog { Contractnum = "C1",Instagreenum = "A1",Dataareaid = "hol",Companyid = "FOR",
            Deliverycategory = "El-ekstern",Productnum = "P2",Contractpartstartdate = new(2020,1,1),Contractpartenddate = Open,Logdatetime = new(2026,9,2),Recid = 2 }));
        await context.SaveChangesAsync();
        var result = await SettlementContractPartResolver.ResolveAsync(context,new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1) },"hol");
        Assert.Equal(expected,result.Resolution.Pairs.Count == 1);
        Assert.Equal(expected ? 2 : 0,result.Evidence.Count);
        if (expected) Assert.Contains(result.Evidence,r => r.Source == "ContractPartLog");
    }

    private static T Defaults<T>(T entity) where T : class
    {
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetValue(entity) is null))
            property.SetValue(entity,"");
        return entity;
    }
}
