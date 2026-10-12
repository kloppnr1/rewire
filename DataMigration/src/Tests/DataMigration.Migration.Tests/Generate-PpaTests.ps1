#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Generates PPA group test classes from seed data JSON files.
.DESCRIPTION
    Reads xellent.json, settl.json, contracts.json, and the ppa-customers profile
    to generate C# test classes in ProfileRuns/Ppa/Generated/.
    These files are auto-generated and must not be edited manually.
.EXAMPLE
    pwsh ./Generate-PpaTests.ps1
    pwsh ./Generate-PpaTests.ps1 -OutputDir ./ProfileRuns/Ppa/Generated
#>
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'ProfileRuns' 'Ppa' 'Generated')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$seedDir = Join-Path $PSScriptRoot 'Fixtures' 'SeedData'
$profilePath = Join-Path $PSScriptRoot '..' '..' 'profiles' 'ppa-customers.json'

# --- Load profile ---
$profile = Get-Content $profilePath -Raw | ConvertFrom-Json -Depth 20
$accounts = $profile.Accounts
$groupNumToContract = @{}
foreach ($prop in $profile.Mappings.GroupNum.PSObject.Properties) {
    $groupNumToContract[$prop.Name] = $prop.Value
}

# --- Load contracts.json (MP ID -> contract name reverse lookup) ---
$contractsJson = Get-Content (Join-Path $seedDir 'contracts.json') -Raw | ConvertFrom-Json -Depth 5
$mpToContract = @{}
foreach ($prop in $contractsJson.ContractMeteringPoints.PSObject.Properties) {
    foreach ($mpId in $prop.Value) {
        $mpToContract[$mpId] = $prop.Name
    }
}

# --- Load xellent.json for each account ---
$accountInfos = @{}
foreach ($acct in $accounts) {
    $xellentPath = Join-Path $seedDir $acct 'xellent.json'
    if (-not (Test-Path $xellentPath)) { continue }
    $xel = Get-Content $xellentPath -Raw | ConvertFrom-Json -Depth 10
    $cust = $xel.CustTables[0]
    # Get PayerId from first active contract's Custpurchaseorder (if set)
    $payerIdFromXel = $null
    if ($xel.ExuContractTables) {
        foreach ($ct in $xel.ExuContractTables) {
            if ($ct.Custpurchaseorder -and $ct.Custpurchaseorder -ne '') {
                $payerIdFromXel = $ct.Custpurchaseorder
                break
            }
        }
    }
    $accountInfos[$acct] = @{
        AccountNum       = $cust.Accountnum
        Name             = $cust.Name
        ExuTechnicgroup4 = $cust.ExuTechnicgroup4
        Cvr              = $cust.Exualternativecustomernumber
        Partytype        = $cust.Partytype
        Email            = $cust.Email
        PayerId          = if ($payerIdFromXel) { $payerIdFromXel } else { $cust.Accountnum }
    }
}

# --- Group accounts by (ExuTechnicgroup4, CVR) ---
$groups = @{}
foreach ($acct in $accountInfos.Keys | Sort-Object) {
    $info = $accountInfos[$acct]
    $groupKey = "$($info.ExuTechnicgroup4)|$($info.Cvr)"
    if (-not $groups.ContainsKey($groupKey)) {
        $groups[$groupKey] = [System.Collections.Generic.List[string]]::new()
    }
    $groups[$groupKey].Add($acct)
}

# --- Helper: sanitize name for C# class/file ---
function Get-SanitizedName([string]$name) {
    # Strip common suffixes/prefixes
    $n = $name -replace '\s+A/S$', '' `
               -replace '^Aktieselskabet\s+', '' `
               -replace '\s+Livaktieselskab$', '' `
               -replace '\s+ApS$', '' `
               -replace '\s+I/S$', '' `
               -replace '\s+K/S$', ''
    # Remove non-alphanumeric except spaces
    $n = $n -replace '[^a-zA-Z0-9\s\u00C0-\u024F]', ''
    # PascalCase: split on spaces, capitalize each word
    $parts = $n.Trim() -split '\s+' | ForEach-Object {
        if ($_.Length -gt 0) { $_.Substring(0,1).ToUpper() + $_.Substring(1) } else { '' }
    }
    return ($parts -join '')
}

# --- Helper: format C# string literal ---
function Format-CsString([string]$value) {
    if ($null -eq $value -or $value -eq '') { return 'null' }
    $escaped = $value -replace '\\', '\\' -replace '"', '\"'
    return "`"$escaped`""
}

# --- Helper: derive CustomerType string from Partytype ---
function Get-CustomerType([int]$partytype) {
    if ($partytype -eq 2) { return 'Company' }
    return 'Person'
}

# --- Helper: derive GroupName from customer name ---
function Get-GroupName([string]$name) {
    $n = $name -replace '\s+A/S$', '' `
               -replace '^Aktieselskabet\s+', '' `
               -replace '\s+Livaktieselskab$', '' `
               -replace '\s+ApS$', '' `
               -replace '\s+I/S$', '' `
               -replace '\s+K/S$', ''
    return $n.Trim()
}

# --- Fixed test body template ---
# InvoiceChannel and SettlementMethod always skipped: seed data values may differ
# from extraction output due to PBS agreement state and legacy NonProfiled values.
$testBodyTemplate = @'
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
        // ExpectedInvoiceChannel skipped: Xellent PBS agreement state in seed data
        // may not reflect migration-time state. Settl has the correct post-migration value.
        // ExpectedPaymentChannel
        Assert.Equal(_testCase.ExpectedPaymentChannel, payer.PayerBilling.PaymentChannel);
        // ExpectedElectronicNumber skipped: linked to InvoiceChannel which is also skipped
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
            if (expected.GridArea != null && mp.Grid?.GridAreaIdentification != expected.GridArea)
                errors.Add($"{mpId}.GridArea: expected={expected.GridArea} actual={mp.Grid?.GridAreaIdentification}");

            // SettlementMethod skipped: Settl production may have legacy "NonProfiled" for
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
'@

# --- Ensure output directory exists ---
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$generated = 0

foreach ($groupKey in $groups.Keys | Sort-Object) {
    $memberAccounts = @($groups[$groupKey] | Sort-Object)
    $repAccount = $memberAccounts[0]

    # Load settl.json for representative account
    $settlPath = Join-Path $seedDir $repAccount 'settl.json'
    if (-not (Test-Path $settlPath)) {
        Write-Warning "No settl.json for account $repAccount, skipping group"
        continue
    }
    $settl = Get-Content $settlPath -Raw | ConvertFrom-Json -Depth 50

    if (-not $settl.CustomerFull) {
        Write-Warning "No CustomerFull in settl.json for $repAccount, skipping"
        continue
    }

    $xelInfo = $accountInfos[$repAccount]
    $groupName = Get-GroupName $xelInfo.Name
    $sanitizedName = Get-SanitizedName $xelInfo.Name
    $customerType = Get-CustomerType $xelInfo.Partytype

    # --- Customer fields from settl.json CustomerFull ---
    $cf = $settl.CustomerFull
    $customerName = $cf.Address.PostalAddress.Name1.FullName
    $cvr = $cf.VatIdentificationNumber
    $street = $cf.Address.PostalAddress.PhysicalAddress.StreetName
    $building = $cf.Address.PostalAddress.PhysicalAddress.BuildingNumber
    $postalCode = $cf.Address.PostalAddress.PhysicalAddress.PostalCode
    $city = $cf.Address.PostalAddress.PhysicalAddress.City
    $email = $cf.Address.Email

    # --- Payer fields from settl.json PayersFull + Payers ---
    $payerFull = $null
    $payerBasic = $null
    if ($settl.PayersFull -and $settl.PayersFull.Count -gt 0) {
        $payerFull = $settl.PayersFull[0]
    }
    if ($settl.Payers -and $settl.Payers.Count -gt 0) {
        $payerBasic = $settl.Payers[0]
    }

    $payerName = if ($payerFull) { $payerFull.PayerInformation.PostalAddress.Name1.FullName } else { $customerName }
    $payerStreet = if ($payerFull) { $payerFull.PayerInformation.PostalAddress.PhysicalAddress.StreetName } else { $street }
    $payerBuilding = if ($payerFull) { $payerFull.PayerInformation.PostalAddress.PhysicalAddress.BuildingNumber } else { $building }
    $payerPostalCode = if ($payerFull) { $payerFull.PayerInformation.PostalAddress.PhysicalAddress.PostalCode } else { $postalCode }
    $payerCity = if ($payerFull) { $payerFull.PayerInformation.PostalAddress.PhysicalAddress.City } else { $city }
    $payerType = if ($payerFull -and $payerFull.PayerBilling) { $payerFull.PayerBilling.PayerType } else { $customerType }
    $payerEmail = if ($payerFull -and $payerFull.PayerBilling) { $payerFull.PayerBilling.Email } else { $email }
    $invoiceChannel = if ($payerBasic) { $payerBasic.InvoiceChannel } else { 'PDF' }
    $paymentChannel = if ($payerBasic) { $payerBasic.PaymentChannel } else { 'FIK' }
    $electronicNumber = if ($payerBasic) { $payerBasic.ElectronicNumber } else { $null }

    # --- KnownDiffs: detect by comparing xellent email vs settl email ---
    $knownDiffs = @()
    $xelEmail = $xelInfo.Email
    if ($email -and $xelEmail -and $email -ne $xelEmail) {
        $knownDiffs += @{ Field = 'Email'; Value = $email }
    }

    # --- Parse MeteringPointsRaw for all member accounts (parent MPs only) ---
    $mpDetails = @()
    foreach ($memberAcct in $memberAccounts) {
        $memberSettlPath = Join-Path $seedDir $memberAcct 'settl.json'
        if (-not (Test-Path $memberSettlPath)) { continue }
        $memberSettl = Get-Content $memberSettlPath -Raw | ConvertFrom-Json -Depth 50
        if (-not $memberSettl.PSObject.Properties['MeteringPointsRaw'] -or -not $memberSettl.MeteringPointsRaw) { continue }
        foreach ($rawStr in $memberSettl.MeteringPointsRaw) {
            $raw = if ($rawStr -is [string]) { $rawStr | ConvertFrom-Json -Depth 50 } else { $rawStr }
            $mpId = $raw.identification.meteringPointIdentification
            if (-not $mpId) { continue }
            # Only include parent MPs (skip child MPs)
            $relationType = if ($raw.PSObject.Properties['meteringPointRelationType']) { $raw.meteringPointRelationType } else { 'Parent' }
            if ($relationType -ne 'Parent') { continue }
            # Safe property access helper
            $instAddr = $raw.installationAddress
            $addr = if ($instAddr -and $instAddr.PSObject.Properties['address']) { $instAddr.address } else { $null }
            $consumer = if ($raw.PSObject.Properties['consumer']) { $raw.consumer } else { $null }
            $firstParty = if ($consumer -and $consumer.PSObject.Properties['firstConsumerParty']) { $consumer.firstConsumerParty } else { $null }
            $meterChars = if ($raw.PSObject.Properties['meterCharacteristics']) { $raw.meterCharacteristics } else { $null }
            $meters = if ($meterChars -and $meterChars.PSObject.Properties['meters']) { $meterChars.meters } else { $null }
            $hasMeter = $meters -and $meters.Count -gt 0
            $grid = if ($raw.PSObject.Properties['grid']) { $raw.grid } else { $null }
            $gridArea = if ($grid -and $grid.PSObject.Properties['gridAreaIdentification']) { $grid.gridAreaIdentification } else { $null }
            $settlMethod = if ($raw.PSObject.Properties['settlementMethod']) { $raw.settlementMethod } else { $null }
            $connInfo = if ($raw.PSObject.Properties['connectionInfo']) { $raw.connectionInfo } else { $null }

            $mpDetails += @{
                MeteringPointIdentification = $mpId
                InstallationNumber          = $raw.identification.installationNumber
                Street                      = if ($addr) { $addr.streetName } else { $null }
                BuildingNumber              = if ($addr) { $addr.buildingNumber } else { $null }
                PostalCode                  = if ($addr) { $addr.postalCode } else { $null }
                City                        = if ($addr) { $addr.city } else { $null }
                ConsumerType                = if ($consumer) { $consumer.customerType } else { $null }
                ConsumerName                = if ($firstParty -and $firstParty.PSObject.Properties['name']) { $firstParty.name.fullName } else { $null }
                ConsumerCvr                 = if ($firstParty) { $firstParty.vatIdentificationNumber } else { $null }
                MeterIdentifier             = if ($hasMeter) { $meters[0].meterIdentifier } else { $null }
                MeterType                   = if ($hasMeter) { $meters[0].meterType } else { $null }
                GridArea                    = $gridArea
                SettlementMethod            = if ($settlMethod) { $settlMethod.settlementMethod } else { $null }
                ConnectionStatus            = if ($connInfo) { $connInfo.connectionStatus } else { $null }
            }
        }
    }
    # Sort by MP ID for deterministic output
    $mpDetails = @($mpDetails | Sort-Object { $_.MeteringPointIdentification })

    # --- Determine contract name from first MP ---
    $contractName = $null
    if ($mpDetails.Count -gt 0) {
        $firstMpId = $mpDetails[0].MeteringPointIdentification
        if ($mpToContract.ContainsKey($firstMpId)) {
            $contractName = $mpToContract[$firstMpId]
        }
    }
    if (-not $contractName) {
        # Fallback: use GroupNum -> ContractName mapping from profile
        $groupNum = $xelInfo.ExuTechnicgroup4
        if ($groupNumToContract.ContainsKey($groupNum)) {
            $contractName = $groupNumToContract[$groupNum]
        }
    }
    if (-not $contractName) {
        Write-Warning "No contract name found for group $repAccount ($groupName), skipping"
        continue
    }

    # --- Build comment header ---
    $memberCount = $memberAccounts.Count
    $mpCount = $mpDetails.Count
    $mpSuffix = if ($mpCount -gt 0) { ", $mpCount $(if ($mpCount -eq 1) {'malepunkt'} else {'malepunkter'})" } else { '' }
    $commentLine1 = "// $groupName`: $memberCount Xellent-$(if ($memberCount -eq 1) {'konto'} else {'konti'}) -> 1 Settl-kunde$mpSuffix"
    $commentLine2 = "// Settl: sog kunde $repAccount - $customerType, $(if ($cvr) {"CVR $cvr, "})$street $building, $postalCode $city"
    $commentLine3 = "// Payer: $repAccount, $invoiceChannel/$paymentChannel"
    $commentLine4 = "// Contract: $contractName"

    # --- Build MemberAccounts array literal ---
    $memberLiterals = $memberAccounts | ForEach-Object { "`"$_`"" }
    $memberArrayStr = $memberLiterals -join ','

    # --- Build ExpectedMeteringPoints array literal ---
    $mpLiterals = @()
    foreach ($mp in $mpDetails) {
        $mpLines = @()
        $mpLines += "            new ExpectedMeteringPoint("
        $mpLines += "                MeteringPointIdentification: $(Format-CsString $mp.MeteringPointIdentification),"
        $mpLines += "                InstallationNumber: $(Format-CsString $mp.InstallationNumber),"
        $mpLines += "                Street: $(Format-CsString $mp.Street),"
        $mpLines += "                BuildingNumber: $(Format-CsString $mp.BuildingNumber),"
        $mpLines += "                PostalCode: $(Format-CsString $mp.PostalCode),"
        $mpLines += "                City: $(Format-CsString $mp.City),"
        $mpLines += "                ConsumerType: $(Format-CsString $mp.ConsumerType),"
        $mpLines += "                ConsumerName: $(Format-CsString $mp.ConsumerName),"
        $mpLines += "                ConsumerCvr: $(Format-CsString $mp.ConsumerCvr),"
        $mpLines += "                MeterIdentifier: $(Format-CsString $mp.MeterIdentifier),"
        $mpLines += "                MeterType: $(Format-CsString $mp.MeterType),"
        $mpLines += "                GridArea: $(Format-CsString $mp.GridArea),"
        $mpLines += "                SettlementMethod: $(Format-CsString $mp.SettlementMethod),"
        $mpLines += "                ConnectionStatus: $(Format-CsString $mp.ConnectionStatus))"
        $mpLiterals += ($mpLines -join "`n")
    }
    $mpArrayStr = if ($mpLiterals.Count -gt 0) {
        "`n" + ($mpLiterals -join ",`n") + ","
    } else {
        ""
    }

    # --- Build KnownDiffs literal ---
    $knownDiffsStr = ''
    if ($knownDiffs.Count -gt 0) {
        $diffLiterals = $knownDiffs | ForEach-Object {
            "new ExpectedDiff($(Format-CsString $_.Field), $(Format-CsString $_.Value))"
        }
        $knownDiffsStr = "`n        KnownDiffs: [$($diffLiterals -join ', ')]"
    }

    # --- Build _testCase literal ---
    $testCaseStr = @"
    private static readonly GroupTestCase _testCase = new(
        RepresentativeAccount: $(Format-CsString $repAccount),
        GroupName: $(Format-CsString $groupName),
        MemberAccounts: [$memberArrayStr],
        ExpectedCustomerName: $(Format-CsString $customerName),
        ExpectedCustomerType: $(Format-CsString $customerType),
        ExpectedCvr: $(Format-CsString $cvr),
        ExpectedStreet: $(Format-CsString $street),
        ExpectedBuildingNumber: $(Format-CsString $building),
        ExpectedPostalCode: $(Format-CsString $postalCode),
        ExpectedCity: $(Format-CsString $city),
        ExpectedEmail: $(Format-CsString $email),
        ExpectedPayerId: $(Format-CsString $xelInfo.PayerId),
        ExpectedPayerName: $(Format-CsString $payerName),
        ExpectedPayerStreet: $(Format-CsString $payerStreet),
        ExpectedPayerBuilding: $(Format-CsString $payerBuilding),
        ExpectedPayerPostalCode: $(Format-CsString $payerPostalCode),
        ExpectedPayerCity: $(Format-CsString $payerCity),
        ExpectedPayerType: $(Format-CsString $payerType),
        ExpectedPayerEmail: $(Format-CsString $payerEmail),
        ExpectedInvoiceChannel: $(Format-CsString $invoiceChannel),
        ExpectedPaymentChannel: $(Format-CsString $paymentChannel),
        ExpectedElectronicNumber: $(Format-CsString $electronicNumber),
        ExpectedContractName: $(Format-CsString $contractName),
        ExpectedMeteringPoints: [$mpArrayStr
        ]$(if ($knownDiffsStr) { ",$knownDiffsStr" }));
"@

    # --- Assemble full C# file ---
    $className = "Group${repAccount}_${sanitizedName}Tests"
    $fileContent = @"
$commentLine1
$commentLine2
$commentLine3
$commentLine4
// Auto-generated by Generate-PpaTests.ps1 — do not edit manually
using DataMigration.MigrationCli.Runners;
using DataMigration.MigrationCli.Tests.Fixtures;

namespace DataMigration.MigrationCli.Tests.ProfileRuns.Ppa.Generated;

public class ${className}(PpaProfileFixture fixture) : IClassFixture<PpaProfileFixture>
{
    private readonly MigrationReport _report = fixture.Runner.Report;

$testCaseStr

$testBodyTemplate
}
"@

    # --- Write file ---
    $filePath = Join-Path $OutputDir "$className.cs"
    $fileContent | Set-Content -Path $filePath -Encoding UTF8 -NoNewline
    Write-Host "Generated: $className.cs"
    $generated++
}

Write-Host "`nDone. Generated $generated test files in $OutputDir"
