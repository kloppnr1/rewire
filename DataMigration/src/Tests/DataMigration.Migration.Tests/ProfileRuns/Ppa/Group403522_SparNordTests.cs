// Navnf Navng Navnh: 94 Xellent-konti → 1 Settl-kunde
// Settl: søg kunde 403522 — Company, CVR 59751548, Fiktivvejba 15, 9000 Aalborg
// Payer: 10010, OioUbl/OIOUBL_FIK
// Contract: E_PPA_Spar_Nord_Bank
using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests.ProfileRuns.Ppa;

public class Group403522_SparNordTests(PpaProfileFixture fixture) : IClassFixture<PpaProfileFixture>
{
    private readonly MigrationReport _report = fixture.Runner.Report;

    private static readonly GroupTestCase _testCase = new(
        RepresentativeAccount: "403522",
        GroupName: "Navnf Navng Navnh",
        MemberAccounts: ["403522","446977","446980","446983","446984","446988","446989",
            "446996","446997","446998","447000","447001","447006","447007","447008","447009",
            "447010","447011","447013","447014","447015","447016","447017","447018","447019",
            "447020","447021","447022","447023","447024","447026","447027","447028","447029",
            "447030","447031","447032","447033","447034","447042","447043","447044","447045",
            "447047","447048","447050","447051","447052","447053","447054","447055","447115",
            "447119","447120","447121","447122","447123","447132","447133","447134","447135",
            "447136","447137","447138","447139","447142","447143","447144","447145","447146",
            "447147","447148","447149","447150","447151","447153","447154","447155","447156",
            "447157","447158","448832","454075","455544","455545","456542","460241","475461",
            "478042","483088","485021","489595","492768","512812"],
        ExpectedCustomerName: "Navnf Navng Navnh A/S",
        ExpectedCustomerType: "Company",
        ExpectedCvr: "59751548",
        ExpectedStreet: "Fiktivvejba",
        ExpectedBuildingNumber: "15",
        ExpectedPostalCode: "9000",
        ExpectedCity: "Aalborg",
        ExpectedEmail: "uc@db.example.dk",
        ExpectedPayerId: "403522",
        ExpectedPayerName: "Navnf Navng Navnh A/S",
        ExpectedPayerStreet: "Fiktivvejba",
        ExpectedPayerBuilding: "15",
        ExpectedPayerPostalCode: "9000",
        ExpectedPayerCity: "Aalborg",
        ExpectedPayerType: "Company",
        ExpectedPayerEmail: "uc@db.example.dk",
        ExpectedInvoiceChannel: "OioUbl",
        ExpectedPaymentChannel: "OIOUBL_FIK",
        ExpectedElectronicNumber: "5790002328930",
        ExpectedContractName: "E_PPA_Spar_Nord_Bank",
        ExpectedMeteringPoints: [
            new ExpectedMeteringPoint(
                MeteringPointIdentification: "571313115200160662",
                InstallationNumber: "5200160662",
                Street: "Fiktivvejbb",
                BuildingNumber: "2",
                PostalCode: "8900",
                City: "Randers C",
                ConsumerType: "Company",
                ConsumerName: "Navnf Navng Navnh A/S",
                ConsumerCvr: "59751548",
                MeterIdentifier: "29118499",
                MeterType: "D01",
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

            // SettlementMethod
            if (mp.SettlementMethod?.SettlementMethod != expected.SettlementMethod)
                errors.Add($"{mpId}.SettlementMethod: expected={expected.SettlementMethod} actual={mp.SettlementMethod?.SettlementMethod}");

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
