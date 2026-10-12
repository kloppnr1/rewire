using ClosedXML.Excel;

namespace DataMigration.OpsCli.Services;

public record ExemptionRow(string MeteringPointIdentification, DateOnly Date, string ProductElementCode);

public static class ExemptionExcelWriter
{
    private static readonly string[] HeaderRow =
    [
        "Action", "Date", "OverwriteFutureIdentifiers", "MeteringPointIdentification", "CountryCode",
        "ValidateMeteringPointIdentification", "InstallationNo", "OverwriteFutureConsumer",
        "FullName1FirstName1", "LastName1", "FullName2FirstName2", "LastName2", "CustomerType",
        "CVR1", "CVR2", "CPR1", "CPR2", "SupplyStart",
        "OverwriteFutureInstallationAddress", "InstallationStreetName", "InstallationStreetCode",
        "InstallationBuildingNumber", "InstallationBuildingFloor", "InstallationPostalCode",
        "InstallationCity", "InstallationCitySubDivisionName", "InstallationMunicipalityCode",
        "InstallationCountryCode", "InstallationRoomIdentification",
        "OverwriteFuturePaymentConfiguration", "InvoiceCcEmail", "InvoiceCcElectronicNumber",
        "BillingType", "OrderNumber", "ElectronicNumber", "AgreementIdentifier", "OrderIdentifier",
        "BookkeepingReference", "OverwriteFutureBillingConfiguration", "JointInvoice",
        "JointInvoiceNumber", "Currency", "NextInvoiceManual", "AutomaticApprovalOfDraft",
        "OverwriteFutureSettlementMethod", "SettlementMethod", "OverwriteFutureGrid",
        "MarketBalanceAreaIdentification", "GridAreaIdentification", "ResidualProfile", "ProfileType",
        "BalanceSupplierIdentification", "BalanceSupplierIdentificationCodeKind", "NetworkOperator",
        "MeterOperator", "OverwriteFutureStatusInfo", "Status", "InDelivery",
        "TransmissionSystemOperator", "OverwriteFutureMiscellaneous", "IndustryCode", "ProductCode",
        "DisableMarketCommunication", "VisibleInSalesSystem", "VisibleInWebPortal",
        "RelatesToProduction", "OverwriteFutureNotes", "AdditionalInfo1", "AdditionalInfo2", "Remark",
        "OverwriteFutureDistributionSystemOperator", "ContractNumber", "InvoiceSender",
        "PaymentOfNetworkUsage", "GridUsageContract", "OverwriteFutureInvoiceConfiguration",
        "PaymentChannel", "InvoiceChannel", "OverwriteFutureSettlementInfo", "ExemptFromVat",
        "IgnoreInBilling", "OverwriteFutureEquipment", "VoltageLevel", "TransformationLevel",
        "OverwriteFutureRenewable", "ObligationToReceiveProduction",
        "Exemption", "ProductElementCode", "IsSystemService"
    ];

    private const int ColAction = 1;
    private const int ColDate = 2;
    private const int ColMpId = 4;
    private const int ColCountryCode = 5;
    private const int ColExemption = 87;
    private const int ColProductElementCode = 88;

    public static void Write(string outputPath, IEnumerable<ExemptionRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");

        sheet.Cell(1, 1).Value = "Type";
        sheet.Cell(1, 2).Value = "MeteringPoint";
        sheet.Cell(1, 3).Value = "Version";
        sheet.Cell(1, 4).Value = 16;
        sheet.Cell(1, 5).Value = "Identification";
        sheet.Cell(1, 6).Value = "MeteringPointIdentification";

        for (var i = 0; i < HeaderRow.Length; i++)
            sheet.Cell(2, i + 1).Value = HeaderRow[i];

        var rowIdx = 3;
        foreach (var row in rows)
        {
            sheet.Cell(rowIdx, ColAction).Value = "Update";
            sheet.Cell(rowIdx, ColDate).Value = row.Date.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
            sheet.Cell(rowIdx, ColMpId).Value = row.MeteringPointIdentification;
            sheet.Cell(rowIdx, ColCountryCode).Value = "DK";
            sheet.Cell(rowIdx, ColExemption).Value = 1;
            sheet.Cell(rowIdx, ColProductElementCode).Value = row.ProductElementCode;
            rowIdx++;
        }

        workbook.SaveAs(outputPath);
    }
}
