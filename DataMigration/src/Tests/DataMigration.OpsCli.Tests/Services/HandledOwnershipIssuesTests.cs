using System.Text;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class HandledOwnershipIssuesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("handled-issues-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteList(params string[] rows) => WriteList(Encoding.UTF8, rows);

    private string WriteList(Encoding encoding, params string[] rows)
    {
        var path = Path.Combine(_dir, "handled.csv");
        File.WriteAllText(path, string.Join("\r\n", [HandledOwnershipIssues.Header, .. rows]) + "\r\n", encoding);
        return path;
    }

    private static readonly SourceAccountOwnershipIssue Mismatch = new("XellentAccountDoesNotMatchSettlCustomer",
        "293360|representative=293359", "571313114900175297", "293359", new DateOnly(2026, 6, 11));

    [Fact]
    public void Matching_issue_is_handled_others_are_new_and_unmatched_rows_are_no_longer_seen()
    {
        var path = WriteList(
            "XellentAccountDoesNotMatchSettlCustomer;293360|representative=293359;571313114900175297;293359;\"Checked; same company\";2026-11-04",
            "XellentSourceClaimedByMultipleCustomers;101254;MP9;101254|952461;Gone;2026-10-01");
        var other = Mismatch with { SettlCustomers = "514740" };

        var split = HandledOwnershipIssues.Split([Mismatch, other], HandledOwnershipIssues.Load(path));

        Assert.Equal(other, Assert.Single(split.New));
        var (issue, handled) = Assert.Single(split.Handled);
        Assert.Equal(Mismatch, issue);
        Assert.Equal("Checked; same company", handled.Note);
        Assert.Equal("101254", Assert.Single(split.NoLongerSeen).SourceAccount);
    }

    [Theory]
    [InlineData("Reason")]
    [InlineData("SourceAccount")]
    [InlineData("MeteringPoint")]
    [InlineData("SettlCustomers")]
    public void Every_key_column_must_match_exactly(string column)
    {
        var handled = new HandledOwnershipIssue(Mismatch.Reason, Mismatch.SourceAccount, Mismatch.MeteringPoint,
            Mismatch.SettlCustomers, "", new DateOnly(2026, 11, 4));
        handled = column switch
        {
            "Reason" => handled with { Reason = "xellentaccountdoesnotmatchsettlcustomer" },
            "SourceAccount" => handled with { SourceAccount = "293360" },
            "MeteringPoint" => handled with { MeteringPoint = "571313114900175297 " },
            _ => handled with { SettlCustomers = "293359|514740" }
        };

        var split = HandledOwnershipIssues.Split([Mismatch], [handled]);

        Assert.Single(split.New);
        Assert.Empty(split.Handled);
        Assert.Single(split.NoLongerSeen);
    }

    [Fact]
    public void Reports_carry_the_note_and_keep_the_list_columns()
    {
        var handled = new HandledOwnershipIssue(Mismatch.Reason, Mismatch.SourceAccount, Mismatch.MeteringPoint,
            Mismatch.SettlCustomers, "a;b", new DateOnly(2026, 11, 4));

        Assert.Equal(["Reason;SourceAccount;MeteringPoint;SettlCustomers;CosDate;Note;HandledDate",
            "XellentAccountDoesNotMatchSettlCustomer;293360|representative=293359;571313114900175297;293359;2026-06-11;\"a;b\";2026-11-04"],
            HandledOwnershipIssues.BuildHandledCsv([(Mismatch, handled)]).Trim().Split(Environment.NewLine));
        var noLongerSeen = HandledOwnershipIssues.BuildNoLongerSeenCsv([handled]).Trim().Split(Environment.NewLine);
        Assert.Equal(HandledOwnershipIssues.Header, noLongerSeen[0]);
        File.WriteAllText(Path.Combine(_dir, "handled.csv"), string.Join("\r\n", noLongerSeen));
        Assert.Equal(handled, Assert.Single(HandledOwnershipIssues.Load(Path.Combine(_dir, "handled.csv"))));
        Assert.Equal(Path.Combine("out", "state-handled-issues.csv"),
            HandledOwnershipIssues.HandledReportPath(Path.Combine("out", "state.json")));
        Assert.Equal(Path.Combine("out", "state-handled-no-longer-seen.csv"),
            HandledOwnershipIssues.NoLongerSeenReportPath(Path.Combine("out", "state.json")));
    }

    [Fact]
    public void Excel_files_in_the_windows_code_page_and_danish_dates_are_read()
    {
        var path = WriteList(Encoding.Latin1,
            "NoXellentAccountAtCos;;MP1;100001;Kunden er flyttet, sæt på listen;04-11-2026");

        var row = Assert.Single(HandledOwnershipIssues.Load(path));

        Assert.Equal("Kunden er flyttet, sæt på listen", row.Note);
        Assert.Equal(new DateOnly(2026, 11, 4), row.HandledDate);
    }

    [Theory]
    [InlineData("Reason;SourceAccount;MeteringPoint;SettlCustomers;Note", "header")]
    [InlineData(null, "expected 6 fields")]
    public void Malformed_list_stops_with_the_line_and_problem(string? header, string expected)
    {
        var path = Path.Combine(_dir, "handled.csv");
        File.WriteAllLines(path, header is null
            ? [HandledOwnershipIssues.Header, "NoXellentAccountAtCos;;MP1;100001;2026-11-04"]
            : [header]);

        var error = Assert.Throws<InvalidOperationException>(() => HandledOwnershipIssues.Load(path));

        Assert.Contains(expected, error.Message);
        Assert.Contains(path, error.Message);
    }

    [Theory]
    [InlineData("NoXellentAccountAtCos;;MP1;100001;note;soon", "HandledDate")]
    [InlineData(";;MP1;100001;note;2026-11-04", "Reason is empty")]
    [InlineData("NoXellentAccountAtCos;;5,71313E+17;100001;note;2026-11-04", "scientific notation")]
    [InlineData("NoXellentAccountAtCos;;MP1;100001;\"open;2026-11-04", "not closed")]
    public void Malformed_row_stops_with_line_two(string row, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => HandledOwnershipIssues.Load(WriteList(row)));

        Assert.Contains("line 2", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Same_issue_twice_is_malformed()
    {
        var row = "NoXellentAccountAtCos;;MP1;100001;note;2026-11-04";

        var error = Assert.Throws<InvalidOperationException>(() => HandledOwnershipIssues.Load(WriteList(row, row)));

        Assert.Contains("same issue as line 2", error.Message);
    }

    [Fact]
    public void Explicit_path_must_exist_and_the_default_is_used_only_when_present()
    {
        var missing = Path.Combine(_dir, "missing.csv");
        var present = WriteList();

        Assert.Contains(missing, Assert.Throws<InvalidOperationException>(
            () => HandledOwnershipIssues.ResolvePath(missing, present)).Message);
        Assert.Equal(present, HandledOwnershipIssues.ResolvePath(null, present));
        Assert.Null(HandledOwnershipIssues.ResolvePath(null, missing));
    }

    [Fact]
    public async Task Run_stops_before_reading_settl_when_the_list_is_missing()
    {
        var runner = new GenerateStateFromSettlRunner(null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new GenerateStateFromSettlRunOptions
            {
                OutputPath = Path.Combine(_dir, "state.json"),
                HandledIssuesPath = Path.Combine(_dir, "missing.csv")
            }));
    }
}
