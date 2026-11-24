using DataMigration.OpsCli.Services.Reconcile;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReconcileApplySelectionTests
{
    // Header in ReconcileRunner.WriteCsv order.
    private const string Header =
        "\"Category\";\"RepresentativeAccount\";\"RepresentativeName\";\"MemberAccounts\";\"MemberNames\";" +
        "\"Entity\";\"SubType\";\"EntityKey\";\"EntityName\";\"COS\";\"Field\";\"Intended\";\"Actual\";\"Kind\";\"IntendedJson\"";

    private static string Row(string category, string rep, string members, string entity, string subType,
        string entityKey, string cos, string field, string intended, string actual, string kind,
        string? intendedJson = null)
    {
        var cols = new List<string>
        {
            category, rep, "RepName", members, "MemberNames", entity, subType, entityKey, "EntityName",
            cos, field, intended, actual, kind,
        };
        // Pre-carry CSVs have no IntendedJson column at all (14 cols) — only append when supplied so the
        // back-compat path (column absent → null) stays exercised.
        if (intendedJson != null) cols.Add(intendedJson);
        return string.Join(";", cols.Select(v => "\"" + v.Replace("\"", "\"\"") + "\""));
    }

    private static string Csv(params string[] rows) => string.Join("\r\n", new[] { Header }.Concat(rows)) + "\r\n";

    [Fact]
    public void ParseCsv_reads_rows_after_header_with_quoted_fields()
    {
        var csv = Csv(
            Row("Billing reference", "100200", "100200,100201", "MeteringPoint", "Billing",
                "571313100000000001", "2026-05-01", "PaymentConfiguration.ElectronicNumber", "5790001", "", "FieldDiff"));

        var rows = ReconcileApplySelection.ParseCsv(csv);

        var r = Assert.Single(rows);
        Assert.Equal("100200", r.RepresentativeAccount);
        Assert.Equal("100200,100201", r.MemberAccounts);
        Assert.Equal("MeteringPoint", r.Entity);
        Assert.Equal("Billing", r.SubType);
        Assert.Equal("571313100000000001", r.EntityKey);
        Assert.Equal("PaymentConfiguration.ElectronicNumber", r.Field);
        Assert.Equal("2026-05-01", r.Cos);
        Assert.Equal("FieldDiff", r.Kind);
    }

    [Fact]
    public void ParseCsv_handles_embedded_separator_and_escaped_quote()
    {
        // EntityName-like field with ';' and a '"' inside a quoted value must not break parsing.
        var weird = Row("Other field value", "1", "1", "Customer", "", "1", "2026-01-01",
            "Address.Street", "A; B \"x\"", "", "FieldDiff");
        var rows = ReconcileApplySelection.ParseCsv(Csv(weird));
        Assert.Single(rows);
        Assert.Equal("Address.Street", rows[0].Field);
    }

    [Fact]
    public void BuildTargets_groups_by_representative_and_derives_min_cos()
    {
        var rows = ReconcileApplySelection.ParseCsv(Csv(
            Row("Billing reference", "100200", "100200,100201", "MeteringPoint", "Billing",
                "571313100000000001", "2026-06-01", "PaymentConfiguration.ElectronicNumber", "5790001", "", "FieldDiff"),
            Row("Billing reference", "100200", "100200,100201", "Payer", "Billing",
                "100200", "2026-05-01 .. 2026-07-01", "PayerBilling.OrderIdentifier", "PO-1", "", "FieldDiff")));

        var targets = ReconcileApplySelection.BuildTargets(rows, out var dropped);

        Assert.Equal(0, dropped);
        var t = Assert.Single(targets);
        Assert.Equal("100200", t.RepresentativeAccount);
        Assert.Equal(new[] { "100200", "100201" }, t.Accounts);
        // Min across the MP single-date (2026-06-01) and the period floor (2026-05-01).
        Assert.Equal(new DateOnly(2026, 5, 1), t.Cos);
        Assert.Equal(2, t.Rows.Count);
    }

    [Fact]
    public void BuildTargets_uses_period_floor_when_only_payer_rows_present()
    {
        var rows = ReconcileApplySelection.ParseCsv(Csv(
            Row("Billing reference", "300", "300", "Payer", "Billing",
                "300", "2026-07-15 .. 2026-08-01", "PayerBilling.AgreementIdentifier", "AG-9", "", "FieldDiff")));

        var targets = ReconcileApplySelection.BuildTargets(rows, out var dropped);

        Assert.Equal(0, dropped);
        Assert.Equal(new DateOnly(2026, 7, 15), Assert.Single(targets).Cos);
    }

    [Fact]
    public void BuildTargets_falls_back_to_representative_when_memberaccounts_blank()
    {
        var rows = ReconcileApplySelection.ParseCsv(Csv(
            Row("Billing reference", "500", "", "Payer", "Billing",
                "500", "2026-05-01", "PayerBilling.OrderIdentifier", "PO", "", "FieldDiff")));

        var t = Assert.Single(ReconcileApplySelection.BuildTargets(rows, out _));
        Assert.Equal(new[] { "500" }, t.Accounts);
    }

    [Fact]
    public void ParseCsv_reads_optional_IntendedJson_column_when_present()
    {
        var json = "{\"ProductElementId\":\"11111111-1111-1111-1111-111111111111\",\"ExemptionPercentage\":1}";
        var rows = ReconcileApplySelection.ParseCsv(Csv(
            Row("Exemption missing", "100200", "100200", "MeteringPoint", "Exemption",
                "571313100000000001", "2026-05-01", "11111111-1111-1111-1111-111111111111",
                "100% exempt", "", "MissingInSettl", intendedJson: json)));

        Assert.Equal(json, Assert.Single(rows).IntendedJson);
    }

    [Fact]
    public void ParseCsv_IntendedJson_is_null_for_pre_carry_csv()
    {
        // 14-column row (no IntendedJson column) — old cache/CSV must still parse, carried intended null.
        var rows = ReconcileApplySelection.ParseCsv(Csv(
            Row("Billing reference", "100200", "100200", "Payer", "Billing",
                "100200", "2026-05-01", "PayerBilling.OrderIdentifier", "PO-1", "", "FieldDiff")));

        Assert.Null(Assert.Single(rows).IntendedJson);
    }

    [Fact]
    public void BuildTargets_carries_IntendedJson_onto_apply_rows()
    {
        var json = "{\"ProductElementId\":\"22222222-2222-2222-2222-222222222222\",\"ExemptionPercentage\":1}";
        var rows = ReconcileApplySelection.ParseCsv(Csv(
            Row("Exemption missing", "100200", "100200", "MeteringPoint", "Exemption",
                "571313100000000001", "2026-05-01", "22222222-2222-2222-2222-222222222222",
                "100% exempt", "", "MissingInSettl", intendedJson: json)));

        var t = Assert.Single(ReconcileApplySelection.BuildTargets(rows, out _));
        Assert.Equal(json, Assert.Single(t.Rows).IntendedJson);
    }

}
