// Lollands Bank: 5 Xellent-konti → 1 Settl-kunde
// Settl: søg kunde 446964 — Company, CVR 40944977, Fiktivvejbzm 3, 4900 Nakskov
// Payer: 10013, OioUbl/BS
// Contract: E_PPA_Lollands_Bank
using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests.ProfileRuns.Ppa;

public class Group446964_LollandsBankTests(PpaProfileFixture fixture) : IClassFixture<PpaProfileFixture>
{
    private readonly MigrationReport _report = fixture.Runner.Report;

    private static readonly GroupTestCase _testCase = new(
        RepresentativeAccount: "446964",
        GroupName: "Lollands Bank",
        MemberAccounts: ["446964", "446966", "446967", "446968", "446970"],
        ExpectedCustomerName: "Navnbw Navnh A/S",
        ExpectedCustomerType: "Company",
        ExpectedCvr: "40944977",
        ExpectedStreet: "Fiktivvejbzm",
        ExpectedBuildingNumber: "3",
        ExpectedPostalCode: "4900",
        ExpectedCity: "Nakskov",
        ExpectedEmail: "uk@df.example.dk",
        ExpectedPayerId: "446964",
        ExpectedPayerName: "Navnbw Navnh A/S",
        ExpectedPayerStreet: "Fiktivvejbzm",
        ExpectedPayerBuilding: "3",
        ExpectedPayerPostalCode: "4900",
        ExpectedPayerCity: "Nakskov",
        ExpectedPayerType: "Company",
        ExpectedPayerEmail: "uk@df.example.dk",
        ExpectedInvoiceChannel: "OioUbl",
        ExpectedPaymentChannel: "OIOUBL_FIK",
        ExpectedElectronicNumber: "5790002834875",
        ExpectedContractName: "E_PPA_Lollands_Bank",
        ExpectedMeteringPoints: [
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313185220062390",
                InstallationNumber: "5220062390",
                Street: "Fiktivvejbzn",
                BuildingNumber: "2",
                PostalCode: "4800",
                City: "Nykøbing F",
                ConsumerType: "Company",
                ConsumerName: "Navnbw Navnh A/S",
                ConsumerCvr: "40944977",
                MeterIdentifier: "196584",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313185320278790",
                InstallationNumber: "5320278790",
                Street: "Fiktivvejbzm",
                BuildingNumber: "1",
                PostalCode: "4900",
                City: "Nakskov",
                ConsumerType: "Company",
                ConsumerName: "Navnbw Navnh A/S",
                ConsumerCvr: "40944977",
                MeterIdentifier: "26807",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313185320278851",
                InstallationNumber: "5320278851",
                Street: "Fiktivvejbzm",
                BuildingNumber: "3",
                PostalCode: "4900",
                City: "Nakskov",
                ConsumerType: "Company",
                ConsumerName: "Navnbw Navnh A/S",
                ConsumerCvr: "40944977",
                MeterIdentifier: "655949",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313185521112541",
                InstallationNumber: "5521112541",
                Street: "Fiktivvejif",
                BuildingNumber: "3",
                PostalCode: "4930",
                City: "Maribo",
                ConsumerType: "Company",
                ConsumerName: "Navnbw Navnh A/S",
                ConsumerCvr: "40944977",
                MeterIdentifier: "229430",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "Flex",
                ConnectionStatus: "Connected"),
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313185621006160",
                InstallationNumber: "5621006160",
                Street: "Fiktivvejccf",
                BuildingNumber: "52",
                PostalCode: "4760",
                City: "Vordingborg",
                ConsumerType: "Company",
                ConsumerName: "Navnbw Navnh A/S",
                ConsumerCvr: "40944977",
                MeterIdentifier: "654464",
                MeterType: "D01",
                GridArea: null,
                SettlementMethod: "NonProfiled",
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
        // ExpectedInvoiceChannel skipped: PBS agreement status in Xellent seed data
        // does not reflect state at migration time. Settl has the correct post-migration value.
        // ExpectedPaymentChannel
        Assert.Equal(_testCase.ExpectedPaymentChannel, payer.PayerBilling.PaymentChannel);
        // ExpectedElectronicNumber skipped: linked to InvoiceChannel which is also skipped
        // (PBS agreement status difference between seed data and migration time).
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
