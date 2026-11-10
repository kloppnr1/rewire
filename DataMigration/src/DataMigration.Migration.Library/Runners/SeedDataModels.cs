using DataMigration.SettlApi.Models;
using DataMigration.XellentData.Entities;

namespace DataMigration.Migration.Runners;

/// <summary>
/// All Settl API data for a single account: customer, payers, metering points, contracts.
/// </summary>
public class SettlAccountSeedData
{
    public CustomerInfoDto? Customer { get; set; }
    public CustomerResponse? CustomerFull { get; set; }

    public List<PayerInfoDto> Payers { get; set; } = [];
    public List<PayerResponse> PayersFull { get; set; } = [];

    public List<MeteringPointInfoDto> MeteringPoints { get; set; } = [];
    public List<string> MeteringPointsRaw { get; set; } = [];

    public List<CustomerContractDto> Contracts { get; set; } = [];
}

/// <summary>
/// Contract-MP mappings from Settl production for a profile.
/// </summary>
public class SettlContractSeedData
{
    public Dictionary<string, List<string>> ContractMeteringPoints { get; set; } = [];
}

/// <summary>
/// Seed data snapshot captured from Xellent production DB for a single account.
/// Contains all tables needed to simulate a full profile run.
/// </summary>
public class XellentSeedData
{
    public List<CustTable> CustTables { get; set; } = [];
    public List<ExuContractTable> ExuContractTables { get; set; } = [];
    public List<ExuContractPartTable> ExuContractPartTables { get; set; } = [];
    public List<DirPartyAddressRelationship> DirPartyAddressRelationships { get; set; } = [];
    public List<DirPartyAddressRelationshi1066> DirPartyAddressRelationshi1066s { get; set; } = [];
    public List<Address> Addresses { get; set; } = [];
    public List<ExuAgreementTable> ExuAgreementTables { get; set; } = [];
    public List<ExuDelpoint> ExuDelpoints { get; set; } = [];
    public List<ExuDelpointContactInfoTable> ExuDelpointContactInfoTables { get; set; } = [];
    public List<AddressZipCode> AddressZipCodes { get; set; } = [];
    public List<DirPartyECommunicationRela2608> DirPartyECommunicationRela2608s { get; set; } = [];
    public List<DirECommunicationAddress> DirECommunicationAddresses { get; set; } = [];
    public List<EgsMeterpointTypecodeperiod> EgsMeterpointTypecodeperiods { get; set; } = [];
    public List<ExuBillingMethodPeriod> ExuBillingMethodPeriods { get; set; } = [];
    public List<ExuDelpoint2delpointrela25505> ExuDelpoint2delpointrela25505s { get; set; } = [];
    public List<ExuStreet> ExuStreets { get; set; } = [];
    public List<ExuMeterTableTrade> ExuMeterTableTrades { get; set; } = [];
    public List<ExuGroupTable> ExuGroupTables { get; set; } = [];
    public List<ExuInstallation> ExuInstallations { get; set; } = [];
    public List<ExuMeterJournalTrans> ExuMeterJournalTrans { get; set; } = [];
}
