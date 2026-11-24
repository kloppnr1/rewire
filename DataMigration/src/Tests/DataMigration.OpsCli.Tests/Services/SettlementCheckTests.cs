using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementCheckTests
{
    // The production case back office verified: COS 2026-05-01, latest statement 2026-04-30.
    private const string Mp = "571313115200045310";
    private const string Contract = "10461316";
    private static readonly DateOnly Cos = new(2026, 5, 1);
    private static readonly DateTime CosMinusOne = new(2026, 4, 30);
    private static readonly DateTime Open = new(1900, 1, 1);

    private static SettlementCheck.Subject Subject(string? contract = Contract) =>
        new("941589", "41589", Mp, contract, Cos);

    private static SettlementCheck.StatementRow Statement(DateTime date, string contract = Contract,
        string mp = Mp) => new() { MeteringPoint = mp, ContractNumber = contract, LatestStatementDate = date };

    private static SettlementCheck.PartRow Part(long recId, string contract = Contract,
        DateTime? start = null, DateTime? end = null, string mp = Mp) => new()
    {
        MeteringPoint = mp, ContractNumber = contract, RecId = recId,
        StartDate = start ?? new DateTime(2020, 1, 1), EndDate = end ?? Open,
    };

    [Fact]
    public void Latest_statement_on_cos_minus_one_is_settled()
    {
        var row = SettlementCheck.Classify(Subject(), [Part(7)], [Statement(CosMinusOne)]);

        Assert.Equal(SettlementCheck.Settled, row.Status);
        Assert.Equal(new DateOnly(2026, 4, 30), row.LatestStatementDate);
        Assert.Equal("7", row.ContractPartIds);
    }

    [Theory]
    [InlineData("2026-03-31")]
    [InlineData("2026-05-31")]
    [InlineData("1900-01-01")]
    public void Earlier_or_later_statement_is_not_settled_and_keeps_its_date(string date)
    {
        var row = SettlementCheck.Classify(Subject(), [], [Statement(DateTime.Parse(date))]);

        Assert.Equal(SettlementCheck.NotSettled, row.Status);
        Assert.Equal(DateOnly.Parse(date), row.LatestStatementDate);
    }

    [Fact]
    public void The_latest_of_several_billing_rows_decides()
    {
        var row = SettlementCheck.Classify(Subject(), [],
            [Statement(new DateTime(2026, 3, 31)), Statement(CosMinusOne), Statement(new DateTime(2025, 12, 31))]);

        Assert.Equal(SettlementCheck.Settled, row.Status);
    }

    [Fact]
    public void Rows_for_other_contracts_or_points_are_ignored()
    {
        var row = SettlementCheck.Classify(Subject(),
            [Part(1, contract: "10423498"), Part(2, mp: "571313115200000000")],
            [Statement(CosMinusOne, contract: "10423498"), Statement(CosMinusOne, mp: "571313115200000000")]);

        Assert.Equal(SettlementCheck.Missing, row.Status);
        Assert.Null(row.LatestStatementDate);
        Assert.Equal("", row.ContractPartIds);
    }

    [Fact]
    public void No_billing_row_or_no_pinned_contract_is_missing()
    {
        Assert.Equal(SettlementCheck.Missing, SettlementCheck.Classify(Subject(), [Part(7)], []).Status);
        Assert.Equal(SettlementCheck.Missing,
            SettlementCheck.Classify(Subject(contract: null), [Part(7)], [Statement(CosMinusOne)]).Status);
    }

    [Fact]
    public void Part_ids_are_the_parts_in_force_on_cos_minus_one()
    {
        var row = SettlementCheck.Classify(Subject(), [
            Part(9),
            Part(3, end: new DateTime(2026, 4, 29)),
            Part(4, start: new DateTime(2026, 5, 1)),
            Part(5, end: CosMinusOne),
        ], [Statement(CosMinusOne)]);

        Assert.Equal("5|9", row.ContractPartIds);
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
                Claim("571313115200000001", "941589", "99999"),     // source not kept under the customer
                Claim("571313115200000002", "999999", "41589"),     // customer not in the state
                Claim("571313115200000003", "941589", "41589"),     // has an ownership issue
            ],
            new Dictionary<string, string> { [Mp] = Contract },
            ["571313115200000003"]);

        var subject = Assert.Single(subjects);
        Assert.Equal(new SettlementCheck.Subject("941589", "41589", Mp, Contract, Cos), subject);
    }

    [Fact]
    public void Csv_has_one_row_per_metering_point_beside_the_state()
    {
        var csv = SettlementCheck.BuildCsv([
            SettlementCheck.Classify(Subject(), [Part(7)], [Statement(CosMinusOne)]),
            SettlementCheck.Classify(Subject(contract: null), [], []),
        ]);

        Assert.Equal(
            "SettlCustomer;SourceAccount;MeteringPoint;ContractNumber;ContractPartId;Cos;LatestStatementDate;Status\n" +
            $"941589;41589;{Mp};{Contract};7;2026-05-01;2026-04-30;Settled\n" +
            $"941589;41589;{Mp};;;2026-05-01;;Missing\n",
            csv.Replace("\r\n", "\n"));
        Assert.Equal(Path.Combine("out", "state-settlement-check.csv"),
            SettlementCheck.ReportPath(Path.Combine("out", "state.json")));
    }

    private static SettlementCheck.Row Row(string customer, string mp, string status, string? contract = Contract) =>
        new(new SettlementCheck.Subject(customer, customer, mp, contract, Cos), "", null, status);

    [Fact]
    public void A_customer_with_a_settled_point_comes_over_even_when_another_contract_is_not_settled()
    {
        var settled = SettlementCheck.SettledCustomers(
        [
            Row("100001", "MP1", SettlementCheck.Settled), Row("100001", "MP2", SettlementCheck.Settled),
            Row("100002", "MP3", SettlementCheck.Settled), Row("100002", "MP4", SettlementCheck.NotSettled),
            Row("100003", "MP5", SettlementCheck.Settled), Row("100003", "MP6", SettlementCheck.Missing),
            Row("100004", "MP7", SettlementCheck.NotSettled),
        ]);

        Assert.Equal(["100001", "100002", "100003"], settled.Order());
    }

    [Fact]
    public void Only_contracts_billed_up_to_another_date_are_held()
    {
        var held = SettlementCheck.HeldContracts(
        [
            Row("100001", "MP1", SettlementCheck.Settled, contract: "C1"),
            Row("100001", "MP2", SettlementCheck.NotSettled, contract: " C2 "),
            Row("100001", "MP3", SettlementCheck.Missing, contract: "C3"),
        ]);

        Assert.Equal(["C2"], held);
    }

    [Fact]
    public void A_never_billed_point_alone_does_not_settle_its_customer()
    {
        Assert.Empty(SettlementCheck.SettledCustomers([Row("100001", "MP1", SettlementCheck.Missing)]));
    }

    [Fact]
    public void A_point_without_a_cos_minus_one_contract_blocks_its_customer()
    {
        Assert.Empty(SettlementCheck.SettledCustomers(
        [
            Row("100001", "MP1", SettlementCheck.Settled),
            Row("100001", "MP2", SettlementCheck.Missing, contract: null),
        ]));
    }

    [Fact]
    public void The_empty_statement_date_is_not_settled_and_holds_its_contract()
    {
        var row = SettlementCheck.Classify(Subject(), [], [Statement(new DateTime(1900, 1, 1))]);

        Assert.Equal(SettlementCheck.NotSettled, row.Status);
        Assert.Equal([Contract], SettlementCheck.HeldContracts([row]));
    }

    [Fact]
    public void Held_invoices_csv_names_the_contract_point_and_statement_date()
    {
        var row = SettlementCheck.Classify(Subject(), [], [Statement(new DateTime(1900, 1, 1))]);
        var invoice = new DataMigration.Domain.Customers.Invoice("INV9", "41589", new DateTime(2025, 6, 1),
            new DateTime(2025, 6, 15), "", "", DataMigration.Domain.Customers.BillingCategory.Aconto,
            DataMigration.Domain.Customers.PaymentMedia.Fik, DataMigration.Domain.Customers.InvoiceStatus.Invoiced, 1m, 0m)
            { ContractNumber = Contract };

        var csv = SettlementCheck.BuildHeldInvoicesCsv([("941589", invoice)], [row]);

        Assert.Contains($"941589;41589;INV9;2025-06-01;2;2;{Contract};{Mp};2026-05-01;1900-01-01", csv);
    }

    [Fact]
    public void A_customer_without_any_checked_metering_point_is_not_settled()
    {
        Assert.Empty(SettlementCheck.SettledCustomers([]));
    }

    [Fact]
    public void ClassifyAll_matches_rows_to_each_subject_by_metering_point()
    {
        var other = new SettlementCheck.Subject("941589", "41589", "571313115200045327", Contract, Cos);
        var rows = SettlementCheck.ClassifyAll([Subject(), other],
            new([Part(7)], [Statement(CosMinusOne), Statement(new DateTime(2026, 3, 31), mp: other.MeteringPoint)]));

        Assert.Equal([SettlementCheck.Settled, SettlementCheck.NotSettled], rows.Select(r => r.Status));
    }
}
