// Regression tests for account 405013 — Navnl Navnc.
// Migrated to Settl production ~October 2024.
//
// What this tests:
//   SettlMappingService.ExtractCustomersAsync is the core migration extraction logic
//   that drives CreateRunner. These tests verify it produces the same output as when
//   the migration originally ran, using seed data captured from Xellent production.
//   Tests use SQLite in-memory seeded with exact Xellent production data — no live DB
//   connections.
//
// ExpectedDiffs (ExtractionMatchesSettl_WithKnownDifferences):
//   Differences declared here are known manual changes made in Settl AFTER migration.
//   They are NOT bugs in the extraction logic.
//   A new unexpected diff signals potential regression in extraction logic or Settl data drift.
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.Migration.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Tests.ProfileRuns;

public class Profile405013Tests : IDisposable
{
    private const string Account = "405013";

    // Known manual changes made in Settl after migration (not extraction logic bugs).
    private static readonly ExpectedDiff[] KnownSettlDiffs =
    [
        // Email changed manually in Settl by the user after migration.
        new ExpectedDiff("Email", "ud@dc.example.dk"),
    ];

    private readonly XellentDbSeeder _seeder;

    public Profile405013Tests()
    {
        _seeder = new XellentDbSeeder(Account);
    }

    public void Dispose() => _seeder.Dispose();

    // DeliveryCategory must match the canonical PascalCase from MigrationConstants —
    // NormalizeDeliveryCategory canonicalises seed values, and the NuGet
    // XellentDomainMapper compares case-sensitively against the same constant.
    // IncludeEndedCutoff: 2000-01-01 — the El-ekstern contract for this account ended
    // Oct 2025 (after migration). Without this, the account is excluded by the
    // active-contract filter.
    private async Task<SettlMappingService> CreateExtractionServiceAsync()
    {
        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: new DateOnly(2026, 4, 16),
            Commodity: Commodity.Electricity,
            IncludeEndedCutoff: new DateOnly(2000, 1, 1));
        var mapper = new XellentDomainMapper(_seeder.Context, warnings, mapperOptions, MigrationConstants.DataAreaId);
        var loadResult = await mapper.LoadAsync([Account]);
        return new SettlMappingService(loadResult, warnings, new DateTime(2026, 4, 16),
            deliveryCategory: MigrationConstants.DeliveryCategoryElectricity);
    }

    [Fact]
    public async Task ExtractionProducesCorrectXellentData()
    {
        // Verifies that SettlMappingService correctly maps Xellent data to CreateCustomerDto.
        // Fails if EF Core queries, field mapping, or fallback logic regresses.
        var service = await CreateExtractionServiceAsync();
        var customers = await service.ExtractCustomersAsync([Account]);

        Assert.Single(customers);
        var c = customers[0];

        var raw = XellentDbSeeder.LoadRawSeed(Account);
        var cust = raw.CustTables.Single();

        Assert.Equal(Account, c.CustomerNumber);
        Assert.Equal("Person", c.CustomerType);                              // no CVR → Person
        Assert.Null(c.VatIdentificationNumber);                             // empty ExuCvrDisponent1
        Assert.Equal("1602657709", c.NationalIdentificationNumber);         // ExuCprDisponent1
        Assert.Equal("Navnl Navnc", c.Address?.PostalAddress?.Name1?.FullName);
        Assert.Equal("Navnm Navnn Navnc", c.Address?.PostalAddress?.Name2?.FullName); // ExuAdditionalname
        Assert.Equal("ue@da.example.dk", c.Address?.Email);          // CustTable.Email
        Assert.Equal("65262414", c.Address?.Cellphone);                     // CustTable.Cellularphone
        Assert.Equal("Fiktivvejm", c.Address?.PostalAddress?.PhysicalAddress?.StreetName);
        Assert.Equal("83", c.Address?.PostalAddress?.PhysicalAddress?.BuildingNumber);
        Assert.Equal("8960", c.Address?.PostalAddress?.PhysicalAddress?.PostalCode);
        Assert.Equal("Randers SØ", c.Address?.PostalAddress?.PhysicalAddress?.City);
        Assert.Equal("DK", c.Address?.PostalAddress?.PhysicalAddress?.CountryCode);
        Assert.Equal("31876bdc-5a0b-4584-9091-701e5bde6153",
            c.Address?.PostalAddress?.PhysicalAddress?.AddressIdentifier?.Identification);
    }

    [Fact]
    public async Task ExtractionMatchesSettl_WithKnownDifferences()
    {
        // Verifies that extraction output still matches what is in Settl, except for
        // known manual changes. A new unexpected diff signals regression or Settl drift.
        var service = await CreateExtractionServiceAsync();
        var customers = await service.ExtractCustomersAsync([Account]);
        Assert.Single(customers);
        var extracted = customers[0];

        var settlData = SettlSeedData.Load(Account);
        Assert.NotNull(settlData.Customer);

        var unexpected = CompareDiffs(extracted, settlData.Customer!, KnownSettlDiffs);
        Assert.True(unexpected.Count == 0,
            $"Unexpected diffs between extraction and Settl:\n" +
            string.Join("\n", unexpected.Select(d => $"  {d.Field}: extracted={d.Extracted ?? "(null)"}  settl={d.Settl ?? "(null)"}")));
    }

    private static List<(string Field, string? Extracted, string? Settl)> CompareDiffs(
        CreateCustomerDto extracted, CustomerInfoDto settl, ExpectedDiff[] knownDiffs)
    {
        var known = knownDiffs.ToDictionary(d => d.Field, d => d.ExpectedSettlValue);
        var unexpected = new List<(string, string?, string?)>();

        void Check(string field, string? a, string? b)
        {
            a = a?.Trim();
            b = b?.Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;
            if (known.TryGetValue(field, out var expectedSettl) &&
                string.Equals(b, expectedSettl, StringComparison.OrdinalIgnoreCase)) return;
            unexpected.Add((field, a, b));
        }

        Check("CustomerType", extracted.CustomerType, settl.CustomerType);
        Check("VatIdentificationNumber", extracted.VatIdentificationNumber, settl.VatIdentificationNumber);
        Check("NationalIdentificationNumber", extracted.NationalIdentificationNumber, settl.NationalIdentificationNumber);
        Check("FullName", extracted.Address?.PostalAddress?.Name1?.FullName, settl.FullName);
        Check("Email", extracted.Address?.Email, settl.Email);
        Check("PostalCode", extracted.Address?.PostalAddress?.PhysicalAddress?.PostalCode, settl.PostalCode);
        Check("City", extracted.Address?.PostalAddress?.PhysicalAddress?.City, settl.City);

        return unexpected;
    }
}
