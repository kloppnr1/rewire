using DataMigration.OpsCli.Services;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementChargingHistoryReaderTests
{
    private static SettlementContractPartResolver.PartRow Part(string account,string product,string source = "Live",
        DateTime? log = null,DateTime? end = null,long record = 1,string change = "Slutdato er ændret",string? employee = "") =>
        new("",account,"C1",new(2020,1,1),new(1900,1,1),"","","FOR","A1","Charge_Abo",product,"",
            new(2020,1,1),end ?? new(1900,1,1),0,"",source,log,record)
        { ChangeType = source == "ContractPartLog" ? change : null,LogEmployeeId = source == "ContractPartLog" ? employee : null };

    [Fact]
    public void Logged_deleted_rental_subscription_decides_ownership_on_the_selected_date_only()
    {
        (string Account,DateOnly Date)[] dates = [("100",new(2026,8,31)),("100",new(2026,10,1)),("200",new(2026,8,31))];
        var result = SettlementChargingHistoryReader.Fold(dates,
            [Part("100","OAboLejMEL","ContractPartLog",new(2026,9,4,13,0,0),new(2026,9,30),
                change:"Kontraktdel er slettet",employee:"Xellent"),Part("unselected","OAboEjeMEL")]);
        Assert.Equal("Privat_Opladning_Leje",XellentChargeSubscriptionReader.SegmentFor(result.Products[dates[0]]));
        Assert.Empty(result.Products[dates[1]]);
        Assert.Empty(result.Products[dates[2]]);
        Assert.Equal("ContractPartLog",Assert.Single(result.Evidence,e => e.ActiveAtReferenceDate == true).Source);
        Assert.DoesNotContain(result.Evidence,r => r.Account == "unselected");
    }

    [Fact]
    public void Duplicate_live_and_logged_products_are_counted_once_and_keep_Eje_priority()
    {
        var date = (Account:"100",Date:new DateOnly(2026,8,31));
        var result = SettlementChargingHistoryReader.Fold([date],
            [Part("100","OAboLejMEL"),Part("100","OAboLejMEL","ContractPartLog",new(2026,9,2)),Part("100","OAboEjeMEL")]);
        Assert.Equal(2,result.Products[date].Count);
        Assert.Equal("Privat_Opladning_Eje",XellentChargeSubscriptionReader.SegmentFor(result.Products[date]));
        Assert.Equal(3,result.Evidence.Count);
    }

    [Fact]
    public async Task Ambiguous_logged_charging_is_not_interpreted_as_no_charger_or_applied_to_other_customers()
    {
        var failed = (Account:"100",Date:new DateOnly(2026,8,31));
        var good = (Account:"200",Date:new DateOnly(2026,8,31));
        var result = SettlementChargingHistoryReader.Fold([failed,good],
            [Part("100","OAboLejMEL","ContractPartLog",new(2026,9,2)),
             Part("100","OAboLejMEL","ContractPartLog",new(2026,9,3),new(2026,8,31),2),Part("200","OAboEjeMEL")]);
        Assert.False(result.Products.ContainsKey(failed));
        Assert.True(result.Products.ContainsKey(good));
        var source = await SettlementChargingSource.ResolveAsync(
            [new("selected1","month","100",null,"V_LadePriv",XellentAccount:"100",ReferenceDate:failed.Date),
             new("selected2","month","200",null,"V_LadePriv",XellentAccount:"200",ReferenceDate:good.Date)],
            new Dictionary<string,CustomerIdentityKind>(),(_,_) => Task.FromResult(result.Products),requireCompleteRead:true);
        Assert.Contains("100",source.FailedCustomers);
        Assert.DoesNotContain("200",source.FailedCustomers);
        Assert.Equal("Ambiguous",source.Entries[0].ChargeReadStatus);
        Assert.Equal("Privat_Opladning_Eje",source.Answers["200"]);
        Assert.False(source.Answers.ContainsKey("100"));
    }

    [Fact]
    public async Task Live_and_logged_charging_parts_keep_the_existing_cross_company_header_join()
    {
        await using var context = new XellentDbContext(new DbContextOptionsBuilder<XellentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        context.ExuContractTables.Add(Defaults(new ExuContractTable { Contractnum = "C1",Dataareaid = "hol",Companyid = "FOR",
            Custaccount = "100",Contractstartdate = new(2020,1,1),Contractenddate = new(1900,1,1) }));
        context.ExuContractPartTables.Add(Defaults(new ExuContractPartTable { Contractnum = "C1",Dataareaid = "hol",Companyid = "AAA",
            Instagreenum = "A1",Deliverycategory = "Charge_Abo",Productnum = "OAboEjeMEL",Startdate = new(2020,1,1),Enddate = new(1900,1,1),Recid = 1 }));
        context.ExuContractPartLogs.Add(Defaults(new ExuContractPartLog { Contractnum = "C1",Dataareaid = "hol",Companyid = "AAA",
            Instagreenum = "A1",Deliverycategory = "Charge_Abo",Productnum = "OAboLejMEL",Contractpartstartdate = new(2020,1,1),
            Contractpartenddate = new(1900,1,1),Logdatetime = new(2026,9,4,13,0,0),Recid = 2,
            Errormsg = "Kontraktdel er slettet",Logemplid = "Xellent" }));
        await context.SaveChangesAsync();
        var request = (Account:"100",Date:new DateOnly(2026,8,31));
        var result = await SettlementChargingHistoryReader.ReadAsync(context,[request],"hol");
        Assert.Equal(2,result.Products[request].Count);
        Assert.Contains(result.Evidence,r => r.Source == "ContractPartLog");
        Assert.Contains(result.Evidence,r => r.ChangeType == "Kontraktdel er slettet" && r.LogEmployeeId == "Xellent"
            && r.RestorationStatus == "RestoredDeletion");
        Assert.All(result.Evidence,r => Assert.Equal("AAA",r.Company));
    }

    [Fact]
    public void Known_migration_restores_Eje0_and_future_changes_do_not_block_account_347772()
    {
        // Saved JSON supplies dates/IDs, but not type/employee or live state. These are explicit
        // representative assumptions, not a claim that the real customer's logs were verified.
        var request = (Account:"347772",Date:new DateOnly(2026,8,31));
        var start = new DateTime(2026,7,1);
        var future = new DateTime(2027,1,1);
        SettlementContractPartResolver.PartRow Row(string product,DateTime begins,DateTime ends,string source,
            DateTime? logged,long id,string? type) => new("","347772","10581270",new(2020,1,1),new(1900,1,1),"","",
                "for","1089370","Charge_Abo",product,"",begins,ends,0,"",source,logged,id)
                { ChangeType = type,LogEmployeeId = source == "ContractPartLog" ? "Xellent" : null };
        var rows = new[] {
            Row("OAboEje0",start,new(2026,8,31),"Live",null,1,null),
            Row("OAboEje0",start,new(2026,12,31),"ContractPartLog",new(2026,9,4,12,53,54),25638084825,"Slutdato er ændret"),
            Row("OAboEjeMEL",future,new(1900,1,1),"ContractPartLog",new(2026,9,4,12,53,54),25638084826,"Slutdato er ændret"),
            Row("OAboEjeMEL",future,new(2026,8,31),"ContractPartLog",new(2026,9,4,13,34,32),25638085060,"Kontraktdel er slettet") };
        var result = SettlementChargingHistoryReader.Fold([request],rows);
        Assert.Equal(new[] { "OAboEje0" },result.Products[request]);
        Assert.Equal("Privat_Opladning_Eje",XellentChargeSubscriptionReader.SegmentFor(result.Products[request]));
        Assert.All(result.Evidence,e => Assert.False(e.Ambiguous));
        Assert.Contains(result.Evidence,e => e.ProductNum == "OAboEje0" && e.RestoredPartEnd == new DateTime(2026,12,31)
            && e.RestorationLogRecordId == 25638084825 && e.RestorationStatus == "RestoredEnd");
        Assert.All(result.Evidence.Where(e => e.ProductNum == "OAboEjeMEL"),e => {
            Assert.Equal("ExcludedFuture",e.RestorationStatus);Assert.False(e.ActiveAtReferenceDate);
            Assert.Equal(new DateTime(1900,1,1),e.RestoredPartEnd);Assert.Equal(25638084826,e.RestorationLogRecordId); });
        // Input ordering cannot choose a different snapshot.
        var reversed = SettlementChargingHistoryReader.Fold([request],rows.Reverse().ToArray());
        Assert.Equal(result.Products[request],reversed.Products[request]);
        var repair = AborabatRepair.RestoreCustomerPayerSources([],[],rows.Select(r => new XellentChargeSubscriptionRow(
            r.Account,r.ContractNumber,r.Product,r.PartStart,r.PartEnd,r.Source == "ContractPartLog" ? "Log" : "Live",
            r.LoggedAt,r.ChangeType) { AgreementNumber = r.Agreement,CompanyId = r.Company,AmrCode = r.AmrCode,
                LogEmployeeId = r.LogEmployeeId,LogRecordId = r.Source == "ContractPartLog" ? r.RecordId : null }).ToArray(),request.Date);
        Assert.Empty(repair.AmbiguousChargerAccounts);
        Assert.Equal(2,repair.Chargers.Count);
        Assert.Equal(new DateTime(2026,12,31),repair.Chargers.Single(r => r.ProductNumber == "OAboEje0").End);
        Assert.Equal(new DateTime(1900,1,1),repair.Chargers.Single(r => r.ProductNumber == "OAboEjeMEL").End);
        Assert.Contains(repair.ChargerEvidence,e => e.Product == "OAboEjeMEL" && e.EndRecord.Contains("25638084826"));
    }

    [Fact]
    public void Repeated_end_changes_and_deletion_use_the_first_known_migration_snapshot()
    {
        var request = (Account:"100",Date:new DateOnly(2026,8,31));
        var result = SettlementChargingHistoryReader.Fold([request],[
            Part("100","OAboLejMEL","ContractPartLog",new(2026,9,4,12,54,0),new(2026,12,31),10,employee:"Xellent"),
            Part("100","OAboLejMEL","ContractPartLog",new(2026,9,4,13,0,0),new(2026,9,30),11,employee:"Xellent"),
            Part("100","OAboLejMEL","ContractPartLog",new(2026,9,4,13,30,0),new(2026,8,31),12,
                "Kontraktdel er slettet","Xellent")]);
        Assert.Equal(new[] { "OAboLejMEL" },result.Products[request]);
        Assert.All(result.Evidence,e => { Assert.Equal("RestoredDeletion",e.RestorationStatus);
            Assert.Equal(new DateTime(2026,12,31),e.RestoredPartEnd);Assert.Equal(10,e.RestorationLogRecordId); });
        Assert.Equal(12,Assert.Single(result.Evidence,e => e.ActiveAtReferenceDate == true).LogRecordId);
    }

    [Theory]
    [InlineData("Kontraktdel er slettet","Xellent","2026-09-05T12:00:00")]
    [InlineData("Kontraktdel er slettet","other","2026-09-04T13:00:00")]
    [InlineData("Slutdato er ændret","Xellent","2026-09-04T13:00:00")]
    [InlineData("","Xellent","2026-09-04T13:00:00")]
    [InlineData("Kontraktdel er slettet",null,"2026-09-04T13:00:00")]
    public void Unsupported_or_incomplete_date_relevant_history_blocks_instead_of_guessing_empty(
        string type,string? employee,string time)
    {
        var request = (Account:"100",Date:new DateOnly(2026,8,31));
        var result = SettlementChargingHistoryReader.Fold([request],[Part("100","OAboLejMEL","ContractPartLog",
            DateTime.Parse(time),change:type,employee:employee)]);
        Assert.False(result.Products.ContainsKey(request));
        Assert.True(Assert.Single(result.Evidence).Ambiguous);
        Assert.Equal("Uncertain",result.Evidence[0].RestorationStatus);
        Assert.NotNull(result.Evidence[0].RestorationProblem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Genuine_conflicting_current_parts_or_start_changes_still_block(bool changedStart)
    {
        var request = (Account:"100",Date:new DateOnly(2026,8,31));
        var current = Part("100","OAboLejMEL");
        var conflicting = changedStart ? Part("100","OAboLejMEL","ContractPartLog",new(2026,9,4,13,0,0),
            record:2,change:"Startdato er ændret",employee:"Xellent") : current with { RecordId = 2,SubReferenceId = "another" };
        var result = SettlementChargingHistoryReader.Fold([request],[current,conflicting]);
        Assert.False(result.Products.ContainsKey(request));
        Assert.All(result.Evidence,e => Assert.True(e.Ambiguous));
    }

    [Fact]
    public void Old_deletions_and_expired_parts_are_not_restored_and_cannot_block_another_date()
    {
        var request = (Account:"100",Date:new DateOnly(2026,10,1));
        var result = SettlementChargingHistoryReader.Fold([request],[Part("100","OAboLejMEL","ContractPartLog",
            new(2026,9,5),new(2026,9,30),change:"Kontraktdel er slettet"),
            Part("100","OAboEje0",end:new(2026,8,31)),Part("100","OAboEje0",end:new(2026,8,30),record:2)]);
        Assert.Empty(result.Products[request]);
        Assert.All(result.Evidence,e => Assert.False(e.Ambiguous));
    }

    private static T Defaults<T>(T entity) where T : class
    {
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetValue(entity) is null))
            property.SetValue(entity,"");
        return entity;
    }
}
