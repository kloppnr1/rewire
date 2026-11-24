namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Turns MobilePay match rows into sheet rows, in the order
/// <see cref="PaymentMatchColumns.MobilePay"/> declares.
///
/// <para>Kept out of the runner so the mapping can be asserted without a Settl environment: the
/// runner's job is the scan, and the scan is exactly what a column test should not have to
/// arrange.</para>
/// </summary>
public static class MobilePayMatchSheet
{
    public static IReadOnlyList<PaymentMatchSheetRow> Build(IEnumerable<MobilePayMatchReportRow> rows) =>
        [.. rows.OrderBy(r => r.Account, StringComparer.Ordinal)
                .ThenBy(r => r.SourceContractNumber, StringComparer.Ordinal)
                .Select(Row)];

    /// <summary>
    /// The first eleven cells are the vocabulary the PBS report uses too; the last four are what
    /// only this report can say.
    /// </summary>
    public static PaymentMatchSheetRow Row(MobilePayMatchReportRow row) => new(
        [
            row.Account,
            row.SourceContractNumber,
            PaymentMatchColumns.ContractKindLabel(row.SourceKind),
            // Slutdatoen er aftalens egen kontrakts. Holdt op mod COS er det den der skiller en
            // levende aftale fra en efterladt.
            PaymentMatchColumns.Date(row.SourceContractEndDate),
            row.AgreementCountOnAccount.ToString(),
            PaymentMatchColumns.StatusLabel(row.Status),
            string.Join("; ", row.Remarks),
            // Målepunktet rækken hviler på — det COS og migreret kontrakt er læst af. Står uanset om
            // rækken matchede; nøglen der nåede Settl er en anden kolonne, fordi de kun nogle gange
            // er det samme.
            row.MeteringPointId,
            PaymentMatchColumns.MatchKeyLabel(row.MatchKey, row.IsJoint),
            row.EnummerSettl ?? "",
            // The E-number comes from whatever payment agreement Settl holds for the metering point,
            // whatever its method. Naming the method keeps a green row from being read as "MobilePay
            // is already set up in Settl".
            row.EnummerSourcePaymentMethod ?? "",
            // Hvilken kontrakt der faktisk blev migreret — så attributionen kan efterprøves i
            // stedet for at blive taget på tro.
            row.MigratedContractNumber,
            // The column that settles a competing-agreements row: is THIS agreement on the contract
            // that was migrated, or on one the customer left behind?
            PaymentMatchColumns.YesNo(row.IsOnMigratedContract),
            // Hvornår kunden skiftede leverandør — her pr. målepunkt, læst fra Settl.
            PaymentMatchColumns.Date(row.Cos),
            row.MobilePayAgreementId,
            PaymentMatchColumns.MobilePayAgreementStatusLabel(row.CancelDate)
        ],
        row.Severity switch
        {
            MobilePayRowSeverity.Blocking => PaymentMatchRowTone.Blocking,
            MobilePayRowSeverity.Review => PaymentMatchRowTone.Review,
            _ => PaymentMatchRowTone.None
        });
}
