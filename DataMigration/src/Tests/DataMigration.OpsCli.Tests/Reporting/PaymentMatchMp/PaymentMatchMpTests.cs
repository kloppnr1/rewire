using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using DataMigration.Core.Reporting;
using DataMigration.OpsCli.Reporting;
using DataMigration.XellentData.Reporting;
using FluentAssertions;
using Xunit;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class PaymentMatchMpTests
{
    internal const string Mp1 = "571313100000000001", Mp2 = "571313100000000002", Mp3 = "571313100000000003";
    internal static readonly DateOnly Cutover = new(2026, 9, 1);
    internal static XellentDeliveryOnDate.Delivery Owner(string mp, string account = "100", string contract = "C1") =>
        new(mp, account, contract, new(2020, 1, 1), new(1900, 1, 1), "EL");
    internal static SettlCsvRecord Record(string? mp, string e = "E1", string? joint = null) =>
        new() { MeteringpointId = mp, JointInvoiceNumber = joint, ExternalCustomerNumber = e,
            PaymentAgreementNumber = "S1", PaymentMethod = "PBS", CustomerName = "Synthetic customer" };
    internal static MobilePayContract Mobile(string contract = "MAIN", string? parent = null, DateOnly? cancelled = null) =>
        new("100", "agr_test", parent, contract, MobilePaySourceKind.Standalone, null, 1, cancelled);
    internal static XellentPbsContract Pbs(string mp, string contract = "C1") =>
        new() { CustomerAccount = "100", ContractNumber = contract, MeteringpointId = mp,
            PbsAgreementNumber = 123, PbsAgreementSourceContract = "MAIN", Referencetomaincontract = "MAIN", ContractStartDate = new(2020, 1, 1), CandidatesOnMeteringPoint = 1 };
    internal static Task<PaymentMpResult> Run(StubMpReader reader, string ids, List<SettlCsvRecord>? records = null,
        string mode = "mb", bool includeTerminated = false) => new PaymentMatchMpEngine(reader).RunAsync(
            PaymentMatchMpList.Parse(ids), records ?? [], "production", mode, Cutover,
            includeTerminated, "points.txt", "payments.csv", (_, _, _) => { }, CancellationToken.None);

    [Fact]
    public void Parser_preserves_full_ids_reports_duplicates_and_rejects_changed_numbers()
    {
        var input = PaymentMatchMpList.Parse($"\uFEFFMeteringPointId\r\n\"{Mp1}\";'{Mp2}'\n{Mp1}\t5.713131E17\n123\n5713131000000000001");
        input.Ids.Should().Equal(Mp1, Mp2);
        input.Supplied.Should().Be(6);
        input.Duplicates.Should().Be(1);
        input.Invalid.Should().Be(3);
        input.InvalidValues.Should().Contain("5.713131E17");
        PaymentMatchMpList.Parse(" \r\n").Unique.Should().Be(0);
    }

    [Fact]
    public void Historical_ownership_ignores_later_customers_and_preserves_conflicts()
    {
        var day = Cutover.ToDateTime(TimeOnly.MinValue).AddDays(-1);
        var (owners, problems) = PaymentMatchMpEngine.ResolveOwners([Mp1, Mp2, Mp3], [
            Owner(Mp1), Owner(Mp1, "new") with { ContractStart = day.AddDays(1) },
            Owner(Mp2), Owner(Mp2, "conflicting")
        ], day);
        owners.Should().ContainSingle().Which.Value.Account.Should().Be("100");
        problems[Mp2].Should().Contain("Flere Xellent-kunder");
        problems[Mp3].Should().Contain("2026-08-31");
    }

    [Fact]
    public async Task Every_mp_is_accounted_for_without_silent_expansion()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1), Owner(Mp2, "200")] };
        reader.MobileContracts = [Mobile()];
        var result = await Run(reader, $"{Mp1}\n{Mp2}\n{Mp3}\n{Mp1}");
        result.Coverage.Select(r => r.Outcome).Should().Equal("Unmatched", "NoAgreement", "Unresolved");
        result.Coverage.Select(r => r.MeteringPointId).Should().Equal(Mp1, Mp2, Mp3);
        result.Duplicates.Should().Be(1);
        reader.OwnerDate.Should().Be(new DateTime(2026, 8, 31));
        reader.Batches.Single().Should().BeEquivalentTo(["100", "200"]);
        var missing = new StubMpReader();
        (await Run(missing, Mp3)).Coverage.Single().Outcome.Should().Be("Unresolved");
        missing.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_mps_for_one_customer_do_not_duplicate_mobilepay_handover()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1), Owner(Mp2, contract: "C2")],
            MobileContracts = [Mobile(), Mobile()] };
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [Record(Mp2)]);
        result.Agreements.Should().ContainSingle();
        result.Agreements[0].MeteringPointId.Should().Be(Mp2);
        result.Agreements[0].MatchKey.Should().Be(Mp2);
        result.Agreements[0].SettlCustomerNumber.Should().Be("E1");
        result.Agreements[0].Explanation.Should().NotContain(MobilePayRowClassifier.PinningOffRemark);
        result.Coverage.Should().OnlyContain(r => r.Outcome == "Matched" && r.Agreements == 1);
    }

    [Fact]
    public async Task Mobilepay_joint_invoice_uses_source_parent_and_retains_provenance()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1)], MobileContracts = [Mobile("MAIN", "JOINT")] };
        var result = await Run(reader, Mp1, [Record(null, joint: "JOINT")]);
        var row = result.Agreements.Single();
        row.MatchKey.Should().Be("JOINT");
        row.SourceContractNumber.Should().Be("MAIN");
        row.OwningContract.Should().Be("C1");
        row.IsOnOwningContract.Should().BeFalse();
        row.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelled_agreement_option_is_rejected()
    {
        var action = () => Run(new StubMpReader(), Mp1, includeTerminated: true);
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Conflicting_csv_customers_cannot_be_hidden_by_account_fallback()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1), Owner(Mp2)], MobileContracts = [Mobile()] };
        var result = await Run(reader, $"{Mp1}\n{Mp2}", [Record(Mp1), Record(Mp1, "E2"), Record(Mp2)]);
        result.Coverage[0].Outcome.Should().Be("Matched");
        result.Agreements.Single().SettlCustomerNumber.Should().BeNull();
        result.Agreements.Single().NeedsReview.Should().BeTrue();
        result.Coverage[1].Outcome.Should().Be("Matched");
    }

    [Fact]
    public async Task Joint_invoice_conflicts_are_visible_in_coverage_and_handover()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1)], MobileContracts = [Mobile(parent: "MAIN")] };
        var result = await Run(reader, Mp1, [Record(null, "E1", "MAIN"), Record(null, "E2", "MAIN")]);
        result.Coverage.Single().Outcome.Should().Be("Matched");
        result.Agreements.Single().NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task Pbs_reuses_main_contract_matching_and_limits_results_to_supplied_mps()
    {
        var selected = Pbs(Mp1);
        selected.Referencetomaincontract = "MAIN";
        selected.PbsAgreementSourceContract = "MAIN";
        var reader = new StubMpReader { Owners = [Owner(Mp1)], PbsContracts = [selected] };
        var result = await Run(reader, Mp1, [Record(null, joint: "MAIN"), Record(Mp2)], "pbs");
        var row = result.Agreements.Single();
        row.SourceContractNumber.Should().Be("MAIN");
        row.OwningContract.Should().Be("C1");
        row.Status.Should().Be("Match (fælles)");
        result.Coverage.Single().Outcome.Should().Be("Matched");
        reader.PaymentDate.Should().Be(Cutover.ToDateTime(TimeOnly.MinValue).AddDays(-1));

    }

    [Fact]
    public void Blank_customer_identifiers_are_unresolved()
    {
        var (owners, problems) = PaymentMatchMpEngine.ResolveOwners([Mp1], [Owner(Mp1, " ")], new(2026, 8, 31));
        owners.Should().BeEmpty();
        problems[Mp1].Should().Contain("mangler kundenummer");
    }

    [Fact]
    public async Task Pbs_missing_export_evidence_does_not_claim_mp_absent_from_settl()
    {
        var reader = new StubMpReader { Owners = [Owner(Mp1)], PbsContracts = [Pbs(Mp1)] };
        var result = await Run(reader, Mp1, mode: "pbs");
        result.Coverage.Single().Outcome.Should().Be("Unmatched");
        result.Coverage.Single().Explanation.Should().Contain("betalingsaftaleeksporten");
    }

    [Fact]
    public async Task Csv_keeps_metadata_ids_and_escapes_user_values()
    {
        var result = await Run(new StubMpReader(), Mp1);
        var csv = (result with { MpFileName = "=formula;\"quoted\"" }).CoverageCsv();
        csv.Should().Contain("'" + Mp1).And.Contain("production;2026-09-01");
        csv.Should().Contain("\"'=formula;\"\"quoted\"\"\"");
        result.FileName("coverage").Should().Contain("mb-mp-list-production-2026-09-01");
    }
}

internal sealed class StubMpReader : IPaymentMatchMpReader
{
    public List<XellentDeliveryOnDate.Delivery> Owners = [];
    public List<XellentPbsContract> PbsContracts = [];
    public IReadOnlyList<MobilePayContract> MobileContracts = [];
    public PaymentMpSnapshot? Snapshot;
    public Func<IReadOnlyList<string>, CancellationToken, Task<List<XellentDeliveryOnDate.Delivery>>>? ReadOwners { get; set; }
    public List<string[]> Batches = [];
    public DateTime OwnerDate, PaymentDate;
    public async Task<List<XellentDeliveryOnDate.Delivery>> OwnersAsync(IReadOnlyList<string> ids, DateTime on,
        Action<int, int> progress, CancellationToken ct)
    {
        OwnerDate = on;
        Owners = ReadOwners is null ? Owners : await ReadOwners(ids, ct);
        progress(ids.Count, ids.Count);
        return Owners;
    }
    public Task<PaymentMpSnapshot> PaymentsAsync(IReadOnlySet<string> accounts, DateTime on, CancellationToken ct)
    {
        Batches.Add(accounts.ToArray()); PaymentDate = on;
        if (Snapshot is not null) return Task.FromResult(new PaymentMpSnapshot(
            Snapshot.Customers.Where(c => accounts.Contains(c.Account)).ToArray(),
            Snapshot.Contracts.Where(c => accounts.Contains(c.Account)).ToArray()) { PbsOwnAgreementCounts = Snapshot.PbsOwnAgreementCounts });
        var sources = Owners.Where(o => accounts.Contains(o.Account)).DistinctBy(o => (o.Account, o.ContractNumber))
            .ToDictionary(o => (o.Account, o.ContractNumber), o => new PaymentMpSource(o.Account, o.ContractNumber,
                "El-ekstern", "MAIN", o.ContractStart, o.ContractEnd, 0, 0, new(1900, 1, 1), null, new(1900, 1, 1)));
        foreach (var c in PbsContracts.Where(c => accounts.Contains(c.CustomerAccount)))
        {
            var contract = c.PbsAgreementSourceContract ?? c.ContractNumber;
            var key = (c.CustomerAccount, contract);
            var source = sources.GetValueOrDefault(key) ?? new PaymentMpSource(c.CustomerAccount, contract, "Vand", null,
                new(2020, 1, 1), new(1900, 1, 1), 0, 0, new(1900, 1, 1), null, new(1900, 1, 1));
            sources[key] = source with { PbsNumber = c.PbsAgreementNumber, PbsCode = 1 };
            if (sources.TryGetValue((c.CustomerAccount, c.ContractNumber), out var own))
                sources[(c.CustomerAccount, c.ContractNumber)] = own with { MainContract = c.Referencetomaincontract };
        }
        foreach (var c in MobileContracts.Where(c => accounts.Contains(c.Account)))
        {
            var key = (c.Account, c.SourceContractNumber);
            var source = sources.GetValueOrDefault(key) ?? new PaymentMpSource(c.Account, c.SourceContractNumber, "Vand", null,
                new(2020, 1, 1), new(1900, 1, 1), 0, 0, new(1900, 1, 1), null, new(1900, 1, 1));
            sources[key] = source with { MobileId = c.MobilePayAgreementId, MainContract = c.Referencetomaincontract,
                MobileCancel = c.CancelDate?.ToDateTime(TimeOnly.MinValue) ?? new(1900, 1, 1) };
        }
        return Task.FromResult(new PaymentMpSnapshot(accounts.Select(a => new PaymentMpCustomer(a, "Synthetic customer", false)).ToArray(), sources.Values.ToArray()));
    }
}
