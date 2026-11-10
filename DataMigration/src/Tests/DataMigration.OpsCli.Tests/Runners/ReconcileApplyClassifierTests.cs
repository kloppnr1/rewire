using DataMigration.OpsCli.Services.Reconcile;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReconcileApplyClassifierTests
{
    [Theory]
    // Live Settl writes.
    [InlineData("Payer", "Billing", "FieldDiff", "PayerBilling.ElectronicNumber")]
    [InlineData("MeteringPoint", "Billing", "FieldDiff", "BillingConfiguration.Currency")]
    [InlineData("MeteringPoint", "Billing", "MissingInSettl", "*")]
    [InlineData("MeteringPoint", "Exemption", "MissingInSettl", "guid")]
    [InlineData("Customer", "SettlementGroup", "MissingInSettl", "GroupName")]
    [InlineData("MeteringPoint", "CustomerLink", "MissingInSettl", "linked")]
    [InlineData("MeteringPoint", "", "FieldDiff", "EstimatedAnnualVolume")]
    [InlineData("Customer", "", "FieldDiff", "Address.PostalAddress.Name1.FullName")]
    [InlineData("Payer", "", "FieldDiff", "PayerInformation.PostalAddress.Name1.FullName")]
    [InlineData("Customer", "", "MissingInSettl", "*")]
    [InlineData("Payer", "", "MissingInSettl", "*")]
    [InlineData("MeteringPoint", "", "MissingInSettl", "exists")]
    [InlineData("Customer", "PriceList", "MissingInSettl", "code")]
    // Contract→MP links — the one Anomaly with a real delete API (RemoveContractMeteringPoint).
    [InlineData("MeteringPoint", "ContractLink", "MissingInSettl", "ContractLink[a1b2]")]
    [InlineData("MeteringPoint", "ContractLink", "FieldDiff", "ContractLink[a1b2].End")]
    [InlineData("MeteringPoint", "ContractLink", "Anomaly", "ContractLink[a1b2]")]
    public void Applicable_rows(string entity, string subType, string kind, string field)
        => Assert.True(ReconcileApplyClassifier.Classify(entity, subType, kind, field).Applicable);

    [Fact]
    public void ContractLink_anomaly_reason_names_the_delete()
    {
        var r = ReconcileApplyClassifier.Classify("MeteringPoint", "ContractLink", "Anomaly", "ContractLink[a1b2]");
        Assert.True(r.Applicable);
        Assert.Contains("RemoveContractMeteringPoint", r.Reason);
    }

    [Theory]
    // The blanket "no delete API" rule still holds for every other Anomaly.
    [InlineData("MeteringPoint", "PriceList", "Anomaly", "code")]
    [InlineData("MeteringPoint", "CustomerLink", "Anomaly", "linked")]
    [InlineData("MeteringPoint", "PayerLink", "Anomaly", "linked")]
    [InlineData("Customer", "SettlementGroup", "Anomaly", "GroupName")]
    [InlineData("MeteringPoint", "", "Anomaly", "contract-enddate-not-cos-minus-1")]
    public void Other_anomalies_stay_non_applicable(string entity, string subType, string kind, string field)
        => Assert.False(ReconcileApplyClassifier.Classify(entity, subType, kind, field).Applicable);

    [Theory]
    // No write path.
    [InlineData("MeteringPoint", "", "FieldDiff", "SettlementMethod")]   // master, not annual volume
    [InlineData("MeteringPoint", "", "FieldDiff", "MeteringGridAreaId")]
    [InlineData("MeteringPoint", "", "MissingInXellent", "exists")]
    [InlineData("Customer", "PriceList", "Match", "code")]               // Match never writes
    [InlineData("MeteringPoint", "Exemption", "Match", "guid")]
    // Payer↔MP link drift is surfaced for visibility only — no reconcile-apply write path (yet).
    [InlineData("MeteringPoint", "PayerLink", "MissingInSettl", "linked")]
    [InlineData("MeteringPoint", "PayerLink", "FieldDiff", "linked")]     // re-link (wrong payer)
    [InlineData("MeteringPoint", "PayerLink", "MissingInXellent", "linked")]
    public void Non_applicable_rows(string entity, string subType, string kind, string field)
        => Assert.False(ReconcileApplyClassifier.Classify(entity, subType, kind, field).Applicable);

    [Fact]
    public void Every_classification_has_a_reason()
    {
        var r = ReconcileApplyClassifier.Classify("Customer", "", "FieldDiff", "x");
        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }
}
