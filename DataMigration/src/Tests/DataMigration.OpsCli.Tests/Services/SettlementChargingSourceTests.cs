using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementChargingSourceTests
{
    [Fact]
    public async Task Reader_is_called_for_all_private_accounts_and_dates_even_with_Settl_ownership()
    {
        SettlementConfigEntry[] entries = [
            new("mp1","month","339097",null,"V_LadePriv",SettlContracts:["LadeAbo_Eje_Variabel"],XellentAccount:"339097",ReferenceDate:new(2026,8,31)),
            new("mp2","month","339097",null,"V_Privat",XellentAccount:"339097",ReferenceDate:new(2026,7,31)),
            new("mp3","month","445400",null,"V_Privat",XellentAccount:"445400",ReferenceDate:new(2026,8,31)),
            new("mp4","month","200806",null,"V_Erhverv",XellentAccount:"200806",ReferenceDate:new(2026,8,31))];
        var called = 0;
        var result = await SettlementChargingSource.ResolveAsync(entries,
            new Dictionary<string,CustomerIdentityKind> { ["200806"] = CustomerIdentityKind.Cvr },
            (requests,_) =>
            {
                called++;
                Assert.Equal(3,requests.Count);
                Assert.Contains(("339097",new DateOnly(2026,7,31)), requests);
                IReadOnlyDictionary<(string,DateOnly),IReadOnlyList<string>> products =
                    new Dictionary<(string,DateOnly),IReadOnlyList<string>> { [("339097",new(2026,8,31))] = ["OAboLejMEL"], [("445400",new(2026,8,31))] = ["OAboLejMEL"] };
                return Task.FromResult(products);
            });
        Assert.Equal(1,called);
        Assert.Equal("Privat_Opladning_Leje",result.Answers["339097"]);
        Assert.Equal("Privat_Opladning_Leje",result.Answers["445400"]);
        Assert.Equal("NotRead",result.Entries.Single(e => e.CustomerNumber == "200806").ChargeReadStatus);
    }

    [Fact]
    public async Task Successful_empty_read_only_falls_back_for_charging_case()
    {
        var result = await SettlementChargingSource.ResolveAsync([
            new("mp1","month","106295",null,"V_LadePriv",XellentAccount:"106295",ReferenceDate:new(2026,8,31)),
            new("mp2","month","other",null,"unknown",XellentAccount:"other",ReferenceDate:new(2026,8,31))],
            new Dictionary<string,CustomerIdentityKind>(),(_,_) => Task.FromResult<IReadOnlyDictionary<(string,DateOnly),IReadOnlyList<string>>>(new Dictionary<(string,DateOnly),IReadOnlyList<string>>()));
        Assert.Equal("Privat",result.Answers["106295"]);
        Assert.False(result.Answers.ContainsKey("other"));
    }

    [Fact]
    public async Task Failed_read_never_creates_fallback_answers_and_cancellation_propagates()
    {
        SettlementConfigEntry[] entries = [new("mp","month","106295",null,"V_LadePriv",XellentAccount:"106295",ReferenceDate:new(2026,8,31))];
        var identities = new Dictionary<string,CustomerIdentityKind>();
        var result = await SettlementChargingSource.ResolveAsync(entries,identities,(_,_) => throw new InvalidOperationException("source unavailable"));
        Assert.Empty(result.Answers);
        Assert.Contains("106295",result.FailedCustomers);
        Assert.Equal("ReadFailed",Assert.Single(result.Entries).ChargeReadStatus);
        await Assert.ThrowsAsync<OperationCanceledException>(() => SettlementChargingSource.ResolveAsync(entries,identities,(_,_) => throw new OperationCanceledException()));
    }
}
