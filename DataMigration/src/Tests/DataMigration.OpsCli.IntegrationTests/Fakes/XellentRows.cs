using DataMigration.Core;
using DataMigration.XellentData.Entities;

namespace DataMigration.OpsCli.IntegrationTests.Fakes;

/// <summary>
/// Synthetic Xellent rows for one delivery: account → contract → contract part → agreement →
/// delivery point (metering point) → meter-point type. The keys line up the way every reader in
/// the OpsCli joins them (data area, company and agreement number all the way through). No names,
/// addresses or national identifiers.
/// </summary>
public static class XellentRows
{
    public static readonly DateTime Open = MigrationConstants.EndDateNull;
    private static long _recId = 5_000_000;

    public sealed record Delivery(
        string Account, string Contract, string MeteringPoint,
        DateTime ContractStart, DateTime ContractEnd,
        string Company = "for", string Product = "V Variabel", string? MainContract = null,
        string TypeCode = "E17/Forb", DateTime? PartStart = null, DateTime? PartEnd = null)
    {
        public string Agreement => "A" + Contract;
        public string Delpoint => "D" + MeteringPoint[^6..];
    }

    public static object[] For(Delivery d, bool withCustomer = true)
    {
        var rows = new List<object>();
        if (withCustomer)
            rows.Add(new CustTable { Accountnum = d.Account, Dataareaid = MigrationConstants.DataAreaId, Recid = ++_recId });
        rows.Add(new ExuContractTable
        {
            Contractnum = d.Contract, Dataareaid = MigrationConstants.DataAreaId, Custaccount = d.Account,
            Contractstartdate = d.ContractStart, Contractenddate = d.ContractEnd, Companyid = d.Company,
            Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Referencetomaincontract = d.MainContract ?? "",
            Pbstransdate = Open, Mobilepayagreementcanceldate = Open, Billingplannum = "month",
            Contracttemplatenum = "V_Privat", Recid = ++_recId,
        });
        rows.Add(Part(d.Contract, d.Agreement, d.Company, MigrationConstants.DeliveryCategoryElectricity, d.Product,
            d.PartStart ?? d.ContractStart, d.PartEnd ?? d.ContractEnd));
        rows.Add(new ExuAgreementTable
        {
            Agreementnum = d.Agreement, Dataareaid = MigrationConstants.DataAreaId, Companyid = d.Company,
            Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Startdate = d.ContractStart, Enddate = Open,
            Recid = ++_recId,
        });
        rows.Add(new ExuDelpoint
        {
            Num = d.Delpoint, Attachmentnum = d.Agreement, Dataareaid = MigrationConstants.DataAreaId, Companyid = d.Company,
            Deliverycategory = MigrationConstants.DeliveryCategoryElectricity, Meteringpoint = d.MeteringPoint,
            Custaccount = d.Account, Recid = ++_recId,
        });
        rows.Add(new EgsMeterpointTypecodeperiod
        {
            Dataareaid = MigrationConstants.DataAreaId, Deliverycategory = MigrationConstants.DeliveryCategoryElectricity,
            Companyid = d.Company, Attachmentnum = d.Agreement, Delpointnum = d.Delpoint,
            Startdate = new DateTime(2000, 1, 1), Enddate = Open, Meterpointtypecode = d.TypeCode, Recid = ++_recId,
        });
        return [.. rows];
    }

    public static ExuContractPartTable Part(string contract, string agreement, string company, string category,
        string product, DateTime start, DateTime end) => new()
        {
            Contractnum = contract, Instagreenum = agreement, Dataareaid = MigrationConstants.DataAreaId, Companyid = company,
            Deliverycategory = category, Productnum = product, Startdate = start, Enddate = end, Recid = ++_recId,
        };

    /// <summary>A contract carrying only a charger subscription part (Charge_Abo) for the account.</summary>
    public static object[] ChargeSubscription(string account, string contract, string product, DateTime start, DateTime end) =>
    [
        new ExuContractTable
        {
            Contractnum = contract, Dataareaid = MigrationConstants.DataAreaId, Custaccount = account,
            Contractstartdate = start, Contractenddate = Open, Companyid = "for",
            Deliverycategory = MigrationConstants.DeliveryCategoryChargeSubscription,
            Pbstransdate = Open, Mobilepayagreementcanceldate = Open, Recid = ++_recId,
        },
        Part(contract, "A" + contract, "for", MigrationConstants.DeliveryCategoryChargeSubscription, product, start, end),
    ];
}
