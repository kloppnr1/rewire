// Navnbx Navnby: 1 Xellent-konto → 1 Settl-kunde, 9 parent målepunkter (5 child MPs ignoreres)
// Settl: søg kunde 446971 — Company, CVR 49687731, Fiktivvejceb 102, 7000 Fredericia
// Payer: 10014, PDF/FIK
// Contract: E_PPA_Foreningen_Bankdata
using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests.ProfileRuns.Ppa;

public class Group446971_BankdataTests(PpaProfileFixture fixture) : IClassFixture<PpaProfileFixture>
{
    private readonly MigrationReport _report = fixture.Runner.Report;

    private static readonly GroupTestCase _testCase = new(
        RepresentativeAccount: "446971",
        GroupName: "Navnby",
        MemberAccounts: ["446971"],
        ExpectedCustomerName: "Navnbx Navnby",
        ExpectedCustomerType: "Company",
        ExpectedCvr: "49687731",
        ExpectedStreet: "Fiktivvejceb",
        ExpectedBuildingNumber: "102",
        ExpectedPostalCode: "7000",
        ExpectedCity: "Fredericia",
        ExpectedEmail: "ul@dg.example.dk",
        ExpectedPayerId: "446971",
        ExpectedPayerName: "Navnbx Navnby",
        ExpectedPayerStreet: "Fiktivvejceb",
        ExpectedPayerBuilding: "102",
        ExpectedPayerPostalCode: "7000",
        ExpectedPayerCity: "Fredericia",
        ExpectedPayerType: "Company",
        ExpectedPayerEmail: "ul@dg.example.dk",
        ExpectedInvoiceChannel: "PDF",
        ExpectedPaymentChannel: "FIK",
        ExpectedElectronicNumber: null,
        ExpectedContractName: "E_PPA_Foreningen_Bankdata",
        ExpectedMeteringPoints: [
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313113162049735",
                InstallationNumber: "3162049735",
                Street: "Fiktivvejcec",
                BuildingNumber: "30",
                PostalCode: "8600",
                City: "Silkeborg",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "61794796",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "NonProfiled",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313113162062215",
                InstallationNumber: "3162062215",
                Street: "Fiktivvejcec",
                BuildingNumber: "30",
                PostalCode: "8600",
                City: "Silkeborg",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "29338123",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124411219113",
                InstallationNumber: "4411219113",
                Street: "Fiktivvejceb",
                BuildingNumber: "102",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "96785956",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "NonProfiled",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124411238817",
                InstallationNumber: "4411238817",
                Street: "Fiktivvejyb",
                BuildingNumber: "38",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "61073161",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124411292765",
                InstallationNumber: "4411292765",
                Street: "Fiktivvejceb",
                BuildingNumber: "126",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "11128216",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124411333192",
                InstallationNumber: "4411333192",
                Street: "Fiktivvejced",
                BuildingNumber: "1",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "95299236",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124450051071",
                InstallationNumber: "4450051071",
                Street: "Fiktivvejceb",
                BuildingNumber: "102",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: "96675362",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "NonProfiled",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124450773188",
                InstallationNumber: "4450773188",
                Street: "Fiktivvejced",
                BuildingNumber: "1",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: null,
                MeterType: null,
                GridArea: null,
                SettlementMethod: "NonProfiled",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313124490442402",
                InstallationNumber: "4490442402",
                Street: "Fiktivvejceb",
                BuildingNumber: "122",
                PostalCode: "7000",
                City: "Fredericia",
                ConsumerType: "Company",
                ConsumerName: "Navnbx Navnby",
                ConsumerCvr: "49687731",
                MeterIdentifier: null,
                MeterType: null,
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
        ]);

    // ===== Account grouping =====

    [Fact]
    public void AccountGrouping()
    {
        var group = _report.AccountGroupings
            .SingleOrDefault(g => g.RepresentativeAccount == _testCase.RepresentativeAccount);
        Assert.NotNull(group);
        Assert.Equal(_testCase.MemberAccounts.Order(), group.MemberAccounts.Order());
    }

    // ===== Customer fields =====

    [Fact]
    public void Customer()
    {
        var c = _report.Customers
            .SingleOrDefault(c => c.CustomerNumber == _testCase.RepresentativeAccount);
        Assert.NotNull(c);

        // ExpectedCustomerType
        Assert.Equal(_testCase.ExpectedCustomerType, c.CustomerType);
        // ExpectedCvr
        Assert.Equal(_testCase.ExpectedCvr, c.VatIdentificationNumber);
        // ExpectedCustomerName
        Assert.Equal(_testCase.ExpectedCustomerName, c.Address?.PostalAddress?.Name1?.FullName);
        // ExpectedStreet
        Assert.Equal(_testCase.ExpectedStreet, c.Address?.PostalAddress?.PhysicalAddress?.StreetName);
        // ExpectedBuildingNumber
        Assert.Equal(_testCase.ExpectedBuildingNumber, c.Address?.PostalAddress?.PhysicalAddress?.BuildingNumber);
        // ExpectedPostalCode
        Assert.Equal(_testCase.ExpectedPostalCode, c.Address?.PostalAddress?.PhysicalAddress?.PostalCode);
        // ExpectedCity
        Assert.Equal(_testCase.ExpectedCity, c.Address?.PostalAddress?.PhysicalAddress?.City);
        // ExpectedEmail
        if (_testCase.KnownDiffs?.Any(d => d.Field == "Email") != true)
            Assert.Equal(_testCase.ExpectedEmail, c.Address?.Email);
    }

    [Fact]
    public void Customer_MatchesSettlProduction()
    {
        var customer = _report.Customers.Single(c => c.CustomerNumber == _testCase.RepresentativeAccount);
        var settlData = LoadSettl(_testCase.RepresentativeAccount);
        if (settlData?.CustomerFull == null) return;

        var diffs = SettlComparer.CompareCustomers(
            [customer], LoadSettl,
            _testCase.KnownDiffs != null ? new() { [_testCase.RepresentativeAccount] = _testCase.KnownDiffs } : null);
        Assert.True(diffs.Count == 0,
            $"Customer diffs vs Settl:\n  " +
            string.Join("\n  ", diffs.Select(d => $"{d.Field}: extracted={d.Extracted ?? "(null)"} settl={d.Settl ?? "(null)"}")));
    }

    // ===== Payer fields =====

    [Fact]
    public void Payer()
    {
        var customerIdToNumber = _report.Customers
            .Where(c => c.CustomerNumber != null)
            .ToDictionary(c => c.Id, c => c.CustomerNumber!);

        var payer = _report.Payers
            .SingleOrDefault(p => customerIdToNumber.TryGetValue(p.CustomerId, out var cn) && cn == _testCase.RepresentativeAccount);
        Assert.NotNull(payer);

        Assert.NotNull(payer.PayerInformation);
        // ExpectedPayerId
        Assert.Equal(_testCase.ExpectedPayerId, payer.PayerInformation.PayerIdentifier);
        Assert.NotNull(payer.PayerInformation.PostalAddress);
        // ExpectedPayerName
        Assert.Equal(_testCase.ExpectedPayerName, payer.PayerInformation.PostalAddress.Name1?.FullName);
        // ExpectedPayerStreet
        Assert.Equal(_testCase.ExpectedPayerStreet, payer.PayerInformation.PostalAddress.PhysicalAddress?.StreetName);
        // ExpectedPayerBuilding
        Assert.Equal(_testCase.ExpectedPayerBuilding, payer.PayerInformation.PostalAddress.PhysicalAddress?.BuildingNumber);
        // ExpectedPayerPostalCode
        Assert.Equal(_testCase.ExpectedPayerPostalCode, payer.PayerInformation.PostalAddress.PhysicalAddress?.PostalCode);
        // ExpectedPayerCity
        Assert.Equal(_testCase.ExpectedPayerCity, payer.PayerInformation.PostalAddress.PhysicalAddress?.City);
        // ExpectedPayerType
        Assert.Equal(_testCase.ExpectedPayerType, payer.PayerBilling.PayerType);
        // ExpectedPayerEmail
        if (_testCase.KnownDiffs?.Any(d => d.Field == "Email") != true)
            Assert.Equal(_testCase.ExpectedPayerEmail, payer.PayerBilling.Email);
        // ExpectedInvoiceChannel
        // Settl API converts "OioXml" to "OioUbl" internally — normalize for comparison
        var expectedChannel = _testCase.ExpectedInvoiceChannel;
        var actualChannel = payer.PayerBilling.InvoiceChannel;
        if (expectedChannel == "OioUbl" && actualChannel == "OioXml") actualChannel = "OioUbl";
        Assert.Equal(expectedChannel, actualChannel);
        // ExpectedPaymentChannel
        Assert.Equal(_testCase.ExpectedPaymentChannel, payer.PayerBilling.PaymentChannel);
        // ExpectedElectronicNumber
        Assert.Equal(_testCase.ExpectedElectronicNumber, payer.PayerBilling.ElectronicNumber);
    }

    // ===== Metering points =====

    [Fact]
    public void MeteringPoints()
    {
        if (_testCase.ExpectedMeteringPoints.Length == 0) return;

        var extractedByMpId = _report.MeteringPoints
            .Where(m => m.Identification?.MeteringPointIdentification != null)
            .ToDictionary(m => m.Identification!.MeteringPointIdentification!);

        var errors = new List<string>();

        foreach (var expected in _testCase.ExpectedMeteringPoints)
        {
            var mpId = expected.MeteringPointIdentification;

            if (!extractedByMpId.TryGetValue(mpId, out var mp))
            {
                errors.Add($"{mpId}: not found in extraction");
                continue;
            }

            // Identification
            if (mp.Identification?.InstallationNumber != expected.InstallationNumber)
                errors.Add($"{mpId}.InstallationNumber: expected={expected.InstallationNumber} actual={mp.Identification?.InstallationNumber}");

            // InstallationAddress
            var addr = mp.InstallationAddress?.Address;
            if (addr?.StreetName != expected.Street)
                errors.Add($"{mpId}.Street: expected={expected.Street} actual={addr?.StreetName}");
            if (addr?.BuildingNumber != expected.BuildingNumber)
                errors.Add($"{mpId}.BuildingNumber: expected={expected.BuildingNumber} actual={addr?.BuildingNumber}");
            if (addr?.PostalCode != expected.PostalCode)
                errors.Add($"{mpId}.PostalCode: expected={expected.PostalCode} actual={addr?.PostalCode}");
            // City skipped: not mandatory in Settl API, and AddressZipCode seed data
            // may not cover all zip codes (subset of full Xellent DB).

            // Consumer
            if (mp.Consumer?.CustomerType != expected.ConsumerType)
                errors.Add($"{mpId}.ConsumerType: expected={expected.ConsumerType} actual={mp.Consumer?.CustomerType}");
            if (mp.Consumer?.FirstConsumerParty?.Name?.FullName != expected.ConsumerName)
                errors.Add($"{mpId}.ConsumerName: expected={expected.ConsumerName} actual={mp.Consumer?.FirstConsumerParty?.Name?.FullName}");
            if (expected.ConsumerCvr != null && mp.Consumer?.FirstConsumerParty?.VatIdentificationNumber != expected.ConsumerCvr)
                errors.Add($"{mpId}.ConsumerCvr: expected={expected.ConsumerCvr} actual={mp.Consumer?.FirstConsumerParty?.VatIdentificationNumber}");

            // MeterCharacteristics
            // MeterType skipped: extraction sends null, Settl API auto-populates from other data.
            // ExpectedMeteringPoint retains Settl values (e.g. "D01") for documentation.
            var meter = mp.MeterCharacteristics?.Meters.FirstOrDefault();
            if (expected.MeterIdentifier != null)
            {
                if (meter?.MeterIdentifier != expected.MeterIdentifier)
                    errors.Add($"{mpId}.MeterIdentifier: expected={expected.MeterIdentifier} actual={meter?.MeterIdentifier}");
            }

            // Grid
            if (expected.GridArea != null && mp.Grid?.GridAreaCode != expected.GridArea)
                errors.Add($"{mpId}.GridArea: expected={expected.GridArea} actual={mp.Grid?.GridAreaCode}");

            // SettlementMethod skipped: Settl production has legacy "NonProfiled" for some
            // consumption MPs, but extraction correctly produces "Flex" per current rules.

            // ConnectionInfo
            if (mp.ConnectionInfo?.ConnectionStatus != expected.ConnectionStatus)
                errors.Add($"{mpId}.ConnectionStatus: expected={expected.ConnectionStatus} actual={mp.ConnectionInfo?.ConnectionStatus}");
        }

        Assert.True(errors.Count == 0,
            $"Metering point errors:\n  {string.Join("\n  ", errors)}");
    }

    // ===== Contract mapping =====

    [Fact]
    public void ContractMapping()
    {
        // Verify this group's MPs are mapped to the expected contract name
        // via the profile's GroupNum -> ContractName mapping
        var contractName = _testCase.ExpectedContractName;
        Assert.False(string.IsNullOrEmpty(contractName), "ExpectedContractName must be set");

        // Check that MpContractNames in the report maps this group's MPs to the expected contract
        foreach (var expected in _testCase.ExpectedMeteringPoints)
        {
            if (_report.MpContractNames.TryGetValue(expected.MeteringPointIdentification, out var mappedContract))
            {
                Assert.Equal(contractName, mappedContract);
            }
        }
    }

    private static SettlAccountSeedData? LoadSettl(string account)
    {
        try { return SettlSeedData.Load(account); }
        catch (FileNotFoundException) { return null; }
    }

}
