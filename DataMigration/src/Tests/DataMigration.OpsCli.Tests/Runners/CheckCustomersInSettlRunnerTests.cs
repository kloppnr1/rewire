using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Tests.Runners;

public class CheckCustomersInSettlRunnerTests
{
    private const string Area = "hol";

    // ── Representative resolution ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_member_resolves_to_its_representative_even_when_the_representative_is_not_in_the_input()
    {
        var dbName = Guid.NewGuid().ToString();
        Seed(dbName);
        var runner = new CheckCustomersInSettlRunner(() => NewContext(dbName), new FakeCustomerClient());

        await using var context = NewContext(dbName);
        var reps = await runner.ResolveRepresentativesAsync(context, Options(["200", "300", "400", "999"]), default);

        Assert.Equal("100", reps["200"]);   // merged with 100 (same CVR, name, address, email)
        Assert.Equal("300", reps["300"]);   // same CVR, other address
        Assert.Equal("400", reps["400"]);   // no CVR
        Assert.False(reps.ContainsKey("999"));
    }

    [Fact]
    public async Task Run_writes_one_row_per_input_account_in_input_order()
    {
        var dbName = Guid.NewGuid().ToString();
        Seed(dbName);
        var settl = new FakeCustomerClient { Existing = { ["100"] = Guid.Parse("11111111-1111-1111-1111-111111111111") } };
        var runner = new CheckCustomersInSettlRunner(() => NewContext(dbName), settl);
        var output = new StringWriter();

        var exit = await runner.RunAsync(Options(["300", "200", "999"]), output);

        Assert.Equal(0, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(string.Join('\t', CheckCustomersInSettlRunner.Header), lines[0]);
        Assert.Equal("300\t300\t300\tNotFound\t\t3\t(none),El-Ekstern", lines[1]);
        Assert.Equal("200\t100\t100\tFound\t11111111-1111-1111-1111-111111111111\t0", lines[2]);
        Assert.StartsWith("999\t\t\tNotInXellent", lines[3]);
        Assert.Equal(["100", "300"], settl.Searched.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Active_contracts_count_open_and_future_ends_on_the_reference_date()
    {
        var dbName = Guid.NewGuid().ToString();
        Seed(dbName);
        await using var context = NewContext(dbName);

        var active = await CheckCustomersInSettlRunner.LoadActiveContractsAsync(
            context, ["200", "300"], Area, new DateOnly(2026, 1, 1), default);

        Assert.Equal(3, active["300"].Count);
        Assert.False(active.ContainsKey("200"));

        var later = await CheckCustomersInSettlRunner.LoadActiveContractsAsync(
            context, ["300"], Area, new DateOnly(2026, 7, 1), default);
        Assert.Equal(2, later["300"].Count);
    }

    // ── Settl lookup ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_representative_is_looked_up_once()
    {
        var settl = new FakeCustomerClient();
        var runner = new CheckCustomersInSettlRunner(() => throw new InvalidOperationException(), settl);
        var reps = new Dictionary<string, string> { ["100"] = "100", ["200"] = "100", ["300"] = "100" };

        await runner.LookupAsync(["100", "200", "300"], reps, "featuretest", 4, default);

        Assert.Equal(["100"], settl.Searched.ToArray());
    }

    [Fact]
    public async Task Production_pads_to_six_digits_and_falls_back_to_the_unpadded_number()
    {
        var id = Guid.NewGuid();
        var settl = new FakeCustomerClient { Existing = { ["57923"] = id } };
        var runner = new CheckCustomersInSettlRunner(() => throw new InvalidOperationException(), settl);

        var rows = await runner.LookupAsync(["57923"], new Dictionary<string, string> { ["57923"] = "57923" }, "production", 1, default);

        var row = Assert.Single(rows);
        Assert.Equal(CustomerPresence.FoundUnpadded, row.Status);
        Assert.Equal("57923", row.SearchedCustomerNumber);
        Assert.Equal(id, row.SettlCustomerId);
        Assert.Equal(["957923", "57923"], settl.Searched.ToArray());
    }

    [Fact]
    public async Task Production_finds_the_padded_number_first()
    {
        var settl = new FakeCustomerClient { Existing = { ["957923"] = Guid.NewGuid() } };
        var runner = new CheckCustomersInSettlRunner(() => throw new InvalidOperationException(), settl);

        var rows = await runner.LookupAsync(["57923"], new Dictionary<string, string> { ["57923"] = "57923" }, "production", 1, default);

        Assert.Equal(CustomerPresence.Found, rows[0].Status);
        Assert.Equal("957923", rows[0].SearchedCustomerNumber);
        Assert.Equal(["957923"], settl.Searched.ToArray());
    }

    [Fact]
    public async Task A_Settl_error_is_reported_as_Error_not_NotFound()
    {
        var settl = new FakeCustomerClient { FailWith = "HTTP 500" };
        var runner = new CheckCustomersInSettlRunner(() => throw new InvalidOperationException(), settl);

        var rows = await runner.LookupAsync(["100"], new Dictionary<string, string> { ["100"] = "100" }, "featuretest", 1, default);

        Assert.Equal(CustomerPresence.Error, rows[0].Status);
        Assert.Equal("HTTP 500", rows[0].Detail);
    }

    [Fact]
    public void ParseAccounts_accepts_lines_commas_and_whitespace_and_drops_duplicates()
    {
        var accounts = CheckCustomersInSettlRunner.ParseAccounts("100\r\n200, 300;\t400\n\n100 500");

        Assert.Equal(["100", "200", "300", "400", "500"], accounts);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static CheckCustomersInSettlRunOptions Options(string[] accounts) => new()
    {
        Accounts = accounts,
        Environment = "featuretest",
        DataAreaId = Area,
        ReferenceDate = new DateOnly(2026, 1, 1),
        Parallelism = 1,
    };

    private static XellentDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseInMemoryDatabase(name, o => o.EnableNullChecks(false)).Options);

    private static void Seed(string name)
    {
        using var ctx = NewContext(name);
        ctx.CustTables.AddRange(
            Cust("100", "36665030", "Acme", "a@example.dk", "P100"),
            Cust("200", "36665030", "Acme", "a@example.dk", "P200"),
            Cust("300", "36665030", "Acme", "a@example.dk", "P300"),
            Cust("400", "", "Solo", "", "P400"));
        AddAddress(ctx, "P100", 1, "Fiktivvej 1", "8000", "Aarhus");
        AddAddress(ctx, "P200", 2, "Fiktivvej 1", "8000", "Aarhus");
        AddAddress(ctx, "P300", 3, "Andenvej 2", "8900", "Randers");
        AddAddress(ctx, "P400", 4, "Tredjevej 3", "8600", "Silkeborg");
        // 300: open El-Ekstern (no end date), El-Ekstern ending in the future, open umbrella
        // (no category), and one that ended. 200: ended only. 100 (the representative): open,
        // which must not leak onto member 200's row.
        ctx.ExuContractTables.AddRange(
            Contract("C1", "300", "El-Ekstern", DataMigration.Core.MigrationConstants.EndDateNull),
            Contract("C2", "300", "El-Ekstern", new DateTime(2026, 6, 30)),
            Contract("C3", "300", "", DataMigration.Core.MigrationConstants.EndDateNull),
            Contract("C4", "300", "El-Ekstern", new DateTime(2025, 12, 31)),
            Contract("C5", "200", "El-Ekstern", new DateTime(2025, 3, 31)),
            Contract("C6", "100", "El-Ekstern", DataMigration.Core.MigrationConstants.EndDateNull));
        ctx.SaveChanges();
    }

    private static ExuContractTable Contract(string num, string account, string category, DateTime end) => new()
    {
        Contractnum = num, Dataareaid = Area, Custaccount = account, Name = "", Companyid = "FOR",
        Deliverycategory = category, Contractstartdate = new DateTime(2020, 1, 1), Contractenddate = end,
    };

    private static CustTable Cust(string account, string cvr, string name, string email, string partyId) => new()
    {
        Accountnum = account, Dataareaid = Area, Name = name, Address = "", Street = "", City = "", Zipcode = "",
        Countryregionid = "", Phone = "", Cellularphone = "", Telefax = "", Email = email, Companyregnum = "",
        Identificationnumber = "", Namealias = "", ExuCprDisponent1 = "", Exualternativecustomernumber = "",
        ExuAdditionalname = "", ExuCprDisponent2 = "", ExuCvrDisponent1 = cvr, ExuCvrDisponent2 = "",
        Recid = long.Parse(account), Partyid = partyId, ExuTechnicgroup4 = "NORM",
    };

    private static void AddAddress(XellentDbContext ctx, string partyId, long recid, string street, string zip, string city)
    {
        ctx.DirPartyAddressRelationships.Add(new DirPartyAddressRelationship
        {
            Recid = recid, Dataareaid = Area, Partyid = partyId, Isprimary = 1, Validtodatetime = new DateTime(2154, 12, 31),
        });
        ctx.DirPartyAddressRelationshi1066s.Add(new DirPartyAddressRelationshi1066
        {
            Recid = recid, Dataareaid = Area, Addressrecid = recid, Partyaddressrelationshiprecid = recid,
        });
        ctx.Addresses.Add(new Address
        {
            Recid = recid, Dataareaid = Area, Street = street, FullAddress = street, Zipcode = zip, City = city,
            Countryregionid = "DK", State = "", County = "", Addrtableid = DataMigration.Core.MigrationConstants.AddressTableId,
        });
    }

    private sealed class FakeCustomerClient : ISettlCustomerClient
    {
        public Dictionary<string, Guid> Existing { get; } = new(StringComparer.Ordinal);
        public System.Collections.Concurrent.ConcurrentQueue<string> Searched { get; } = new();
        public string? FailWith { get; init; }

        public Task<(CustomerInfoDto? Customer, string? Error)> SearchByCustomerNumberAsync(
            string customerNumber, string countryCode = "DK", CancellationToken cancellationToken = default)
        {
            Searched.Enqueue(customerNumber);
            if (FailWith != null) return Task.FromResult<(CustomerInfoDto?, string?)>((null, FailWith));
            return Task.FromResult<(CustomerInfoDto?, string?)>(Existing.TryGetValue(customerNumber, out var id)
                ? (new CustomerInfoDto { Id = id, CustomerNumber = customerNumber }, null)
                : (null, null));
        }

        public Task<(bool Success, string? Error)> CreateAsync(CreateCustomersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid customerId, AddCustomerMeteringPointsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(CustomerResponse? Customer, string? Error)> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string? Json, string? Error)> GetRawByIdV3Async(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateV3RawAsync(Guid customerId, string updateRequestJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateNationalIdentificationNumberV3Async(Guid customerId, string nationalIdentificationNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> SearchCustomersAsync(string? fullText = null, string? name = null, string? vatIdentificationNumber = null, int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerOrganisationMeteringPointDto>? Items, string? Error)> GetOrganisationMeteringPointsAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(CustomerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerSegmentDto>? Segments, string? Error)> FindCustomerSegmentsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(SearchCustomerPriceListResponse? Result, string? Error)> SearchPriceListsAsync(Guid customerId, SearchCustomerPriceListRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<ExtraInvoiceLineDto> Items, int TotalCount, string? Error)> GetExtraInvoiceLinesAsync(Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
