using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementCheckTests
{
    private const string Mp = "571313114900175297";
    private static readonly DateOnly Cos = new(2026, 6, 11);
    private static readonly DateTime CosMinusOne = new(2026, 6, 10);
    private static readonly DateTime Open = new(1900, 1, 1);

    private static SettlementCheck.Subject Subject(string? contract = "K1") =>
        new("941589", "41589", Mp, contract, Cos);

    private static SettlementCheck.PartRow Part(long recId, DateTime prev, string contract = "K1",
        DateTime? start = null, DateTime? end = null, string mp = Mp) => new()
    {
        MeteringPoint = mp, ContractNumber = contract, RecId = recId,
        StartDate = start ?? new DateTime(2020, 1, 1), EndDate = end ?? Open, TakeOrPayPrevDate = prev,
    };

    [Fact]
    public void Part_settled_up_to_cos_minus_one_is_settled()
    {
        var row = SettlementCheck.Classify(Subject(), [Part(7, CosMinusOne)]);

        Assert.Equal(SettlementCheck.Settled, row.Status);
        Assert.Equal("7", row.ContractPartIds);
        Assert.Equal("2026-06-10", row.TakeOrPayPrevDates);
    }

    [Fact]
    public void Earlier_or_unset_date_is_not_settled()
    {
        Assert.Equal(SettlementCheck.NotSettled,
            SettlementCheck.Classify(Subject(), [Part(7, new DateTime(2026, 3, 31))]).Status);
        var unset = SettlementCheck.Classify(Subject(), [Part(7, Open)]);
        Assert.Equal(SettlementCheck.NotSettled, unset.Status);
        Assert.Equal("", unset.TakeOrPayPrevDates);
    }

    [Fact]
    public void Every_held_part_must_be_settled()
    {
        var row = SettlementCheck.Classify(Subject(), [Part(8, Open), Part(7, CosMinusOne)]);

        Assert.Equal(SettlementCheck.NotSettled, row.Status);
        Assert.Equal("7|8", row.ContractPartIds);
        Assert.Equal("2026-06-10|", row.TakeOrPayPrevDates);
    }

    [Fact]
    public void Parts_of_other_contracts_points_or_not_in_force_at_cos_minus_one_are_ignored()
    {
        var row = SettlementCheck.Classify(Subject(), [
            Part(1, CosMinusOne, contract: "K2"),
            Part(2, CosMinusOne, mp: "571313114900000000"),
            Part(3, CosMinusOne, end: new DateTime(2026, 6, 9)),
            Part(4, CosMinusOne, start: new DateTime(2026, 6, 11)),
            Part(5, Open, end: CosMinusOne),
        ]);

        Assert.Equal(SettlementCheck.NotSettled, row.Status);
        Assert.Equal("5", row.ContractPartIds);
    }

    [Fact]
    public void No_held_part_or_no_pinned_contract_is_missing()
    {
        Assert.Equal(SettlementCheck.Missing, SettlementCheck.Classify(Subject(), []).Status);
        Assert.Equal(SettlementCheck.Missing,
            SettlementCheck.Classify(Subject(contract: null), [Part(7, CosMinusOne)]).Status);
    }

    [Fact]
    public void Subjects_are_the_claims_the_state_kept()
    {
        var state = new MigrationStateDocument
        {
            Accounts = [new MigratedAccount { SettlAccountNumber = "941589", SourceAccounts = ["41589"] }],
        };
        GenerateStateOfflineCache.OwnershipEvidence Claim(string mp, string customer, string source) =>
            new(mp, customer, source, source, Cos, [source]);

        var subjects = SettlementCheck.Subjects(state,
            [
                Claim(Mp, "941589", "41589"),
                Claim("571313114900000001", "941589", "99999"),     // source not kept under the customer
                Claim("571313114900000002", "999999", "41589"),     // customer not in the state
                Claim("571313114900000003", "941589", "41589"),     // has an ownership issue
            ],
            new Dictionary<string, string> { [Mp] = "K1" },
            ["571313114900000003"]);

        var subject = Assert.Single(subjects);
        Assert.Equal(new SettlementCheck.Subject("941589", "41589", Mp, "K1", Cos), subject);
    }

    [Fact]
    public void Csv_has_one_row_per_metering_point_beside_the_state()
    {
        var csv = SettlementCheck.BuildCsv([SettlementCheck.Classify(Subject(), [Part(7, CosMinusOne)])]);

        Assert.Equal(
            "SettlCustomer;SourceAccount;MeteringPoint;ContractNumber;ContractPartId;Cos;TakeOrPayPrevDate;Status\r\n" +
            $"941589;41589;{Mp};K1;7;2026-06-11;2026-06-10;Settled\r\n",
            csv.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Equal(Path.Combine("out", "state-settlement-check.csv"),
            SettlementCheck.ReportPath(Path.Combine("out", "state.json")));
    }
}
