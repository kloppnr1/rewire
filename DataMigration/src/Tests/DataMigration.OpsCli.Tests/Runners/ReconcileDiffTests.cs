using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;

namespace DataMigration.OpsCli.Tests.Runners;

public class JsonValueDiffTests
{
    private sealed record Addr(string Street, string City);
    private sealed record Person(Guid Id, string Name, int Age, Addr Address);

    [Fact]
    public void Identical_objects_produce_no_diff()
    {
        var a = new Person(Guid.NewGuid(), "Ada", 30, new Addr("Main", "Town"));
        var b = a with { Id = Guid.NewGuid() }; // only the excluded Id differs
        Assert.Empty(JsonValueDiff.Diff(a, b));
    }

    [Fact]
    public void Differing_leaf_is_reported_with_path_and_values()
    {
        var a = new Person(Guid.Empty, "Ada", 30, new Addr("Main", "Town"));
        var b = new Person(Guid.Empty, "Ada", 31, new Addr("Main", "Town"));
        var diff = JsonValueDiff.Diff(a, b);
        Assert.Single(diff);
        Assert.Equal("Age", diff[0].Path);
        Assert.Equal("30", diff[0].Intended);
        Assert.Equal("31", diff[0].Actual);
    }

    [Fact]
    public void IntendedJson_is_the_raw_round_trippable_token()
    {
        // Number leaf → bare token (no quotes); string leaf → quoted token. Both must parse back to the
        // exact JsonNode reconcile-apply writes (the Unquote'd Intended display value cannot).
        var numberDiff = JsonValueDiff.Diff(
            new { Age = 30 }, new { Age = 31 });
        Assert.Equal("30", Assert.Single(numberDiff).IntendedJson);

        var stringDiff = JsonValueDiff.Diff(
            new { Name = "Ada" }, new { Name = "Bo" });
        Assert.Equal("\"Ada\"", Assert.Single(stringDiff).IntendedJson);
        // Sanity: it round-trips to the same value the display column shows unquoted.
        Assert.Equal("Ada", System.Text.Json.Nodes.JsonNode.Parse(stringDiff[0].IntendedJson!)!.GetValue<string>());
    }

    [Fact]
    public void Nested_object_diff_uses_dotted_path()
    {
        var a = new Person(Guid.Empty, "Ada", 30, new Addr("Main", "Town"));
        var b = new Person(Guid.Empty, "Ada", 30, new Addr("Main", "Village"));
        var diff = JsonValueDiff.Diff(a, b);
        Assert.Single(diff);
        Assert.Equal("Address.City", diff[0].Path);
    }

    [Fact]
    public void Excluded_keys_are_ignored_at_every_level()
    {
        // Default excludes include "Id" — present at top level here.
        var a = new Person(Guid.NewGuid(), "Ada", 30, new Addr("Main", "Town"));
        var b = new Person(Guid.NewGuid(), "Ada", 30, new Addr("Main", "Town"));
        Assert.Empty(JsonValueDiff.Diff(a, b));
    }

    [Fact]
    public void Intersection_only_skips_keys_absent_on_actual()
    {
        // intended has an extra "Age" key not present on actual → not reported.
        var intended = new { Name = "Ada", Age = 30 };
        var actual = new { Name = "Ada" };
        Assert.Empty(JsonValueDiff.Diff(intended, actual));
    }

    [Fact]
    public void DiffNodes_matches_keys_case_insensitively()
    {
        // PascalCase intended vs camelCase actual (raw JSON) — same field, value differs → 1 diff.
        var intended = System.Text.Json.Nodes.JsonNode.Parse("""{"OrderIdentifier":"A","BuyerReferenceIdentifier":"X"}""");
        var actual = System.Text.Json.Nodes.JsonNode.Parse("""{"orderIdentifier":"B","buyerReferenceIdentifier":"X"}""");
        var diff = JsonValueDiff.DiffNodes(intended, actual, prefix: "PayerBilling");
        Assert.Single(diff);
        Assert.Equal("PayerBilling.OrderIdentifier", diff[0].Path);
        Assert.Equal("A", diff[0].Intended);
        Assert.Equal("B", diff[0].Actual);
    }

    [Fact]
    public void Payer_billing_reference_difference_is_surfaced_via_typed_intersection()
    {
        // PayerBillingDto now models the full create-payload field set, so both sides emit the
        // same keys (Never → null keys present). A value-vs-null billing-reference difference is
        // caught under plain intersection — no union/raw-JSON workaround needed.
        var diff = JsonValueDiff.Diff(
            new { OrderIdentifier = "A", AgreementIdentifier = "AGR" },
            new { OrderIdentifier = "A", AgreementIdentifier = (string?)null });
        Assert.Single(diff);
        Assert.Equal("AgreementIdentifier", diff[0].Path);
        Assert.Equal("AGR", diff[0].Intended);
        Assert.Null(diff[0].Actual);
    }

    [Fact]
    public void Payer_billing_stale_reference_on_actual_is_surfaced()
    {
        // Inverse: Settl carries a stale value the intended set cleared (intended now null).
        var diff = JsonValueDiff.Diff(
            new { OrderIdentifier = "A", AgreementIdentifier = (string?)null },
            new { OrderIdentifier = "A", AgreementIdentifier = "X" });
        Assert.Single(diff);
        Assert.Equal("AgreementIdentifier", diff[0].Path);
        Assert.Null(diff[0].Intended);
        Assert.Equal("X", diff[0].Actual);
    }

    [Fact]
    public void No_value_normalisation_every_difference_is_surfaced()
    {
        // null vs "" IS reported — reconcile must not silently treat differences as "expected".
        Assert.Single(JsonValueDiff.Diff(new { Fax = "" }, new { Fax = (string?)null }));
        // open-ended-End representations ARE reported (the End-field drift must be visible).
        Assert.Single(JsonValueDiff.Diff(new { End = "9999-12-31T23:59:58.999Z" }, new { End = "9999-12-30T23:00:00Z" }));
    }

    [Fact]
    public void IncludeEqual_emits_matching_leaves_too()
    {
        // Full-payload mode: every compared leaf surfaces, equal ones included (with Intended == Actual),
        // so the caller can tag them Match. Excluded keys (Id) stay skipped.
        var a = new Person(Guid.NewGuid(), "Ada", 30, new Addr("Main", "Town"));
        var b = new Person(Guid.NewGuid(), "Ada", 30, new Addr("Main", "Town"));
        var diff = JsonValueDiff.Diff(a, b, includeEqual: true);
        // Name, Age, Address.Street, Address.City — but NOT the excluded Id.
        Assert.Equal(4, diff.Count);
        Assert.All(diff, d => Assert.Equal(d.Intended, d.Actual));
        Assert.Contains(diff, d => d.Path == "Name" && d.Intended == "Ada");
        Assert.Contains(diff, d => d.Path == "Address.City" && d.Intended == "Town");
        Assert.DoesNotContain(diff, d => d.Path == "Id");
    }

    [Fact]
    public void IncludeEqual_mixes_matched_and_differing_leaves()
    {
        var a = new Person(Guid.Empty, "Ada", 30, new Addr("Main", "Town"));
        var b = new Person(Guid.Empty, "Ada", 31, new Addr("Main", "Town"));
        var diff = JsonValueDiff.Diff(a, b, includeEqual: true);
        Assert.Equal(4, diff.Count);
        var age = Assert.Single(diff, d => d.Path == "Age");
        Assert.NotEqual(age.Intended, age.Actual); // the one differing leaf
        Assert.Equal(3, diff.Count(d => d.Intended == d.Actual)); // the rest match
    }

    [Fact]
    public void IncludeEqual_stays_intersection_only()
    {
        // An intended-only key is still not invented on the actual side, even in full-payload mode.
        var intended = new { Name = "Ada", Age = 30 };
        var actual = new { Name = "Ada" };
        var diff = JsonValueDiff.Diff(intended, actual, includeEqual: true);
        var only = Assert.Single(diff);
        Assert.Equal("Name", only.Path);
        Assert.DoesNotContain(diff, d => d.Path == "Age");
    }
}

/// <summary>
/// Documents the bug scenario: historical billing/payment config versions always have a different
/// Start (old COS-date) and a concrete End (set by Settl when the next version began). Without
/// excluding these fields, every historical version produces false-positive DateStart rows and
/// non-RepOpenEnded End rows. Apply would then overwrite Start with the intended COS-date — causing
/// Settl to create a NEW billing version instead of updating the existing entry — so reconcile
/// would report the same drift forever.
///
/// Fix: ReconcileRunner passes HistoricalBillingVersionExcludes { "Start", "End" } for historical
/// versions. ReconcileApplier skips Start/End leaves unconditionally (period-management fields that
/// cannot be safely rewritten).
/// </summary>
public class HistoricalBillingVersionDiffTests
{
    private static readonly HashSet<string> HistoricalExcludes =
        new(JsonValueDiff.DefaultExcludes, StringComparer.OrdinalIgnoreCase) { "Start", "End" };

    [Fact]
    public void Without_exclusion_historical_Start_appears_as_DateStart_drift()
    {
        // Reproduces the pre-fix bug: every historical version would show Start drift.
        var intended = new { Start = "2026-01-01T00:00:00Z", End = (string?)null, Currency = "DKK" };
        var actual   = new { Start = "2022-06-01T00:00:00Z", End = "2025-12-31T22:00:00Z",  Currency = "DKK" };

        var diff = JsonValueDiff.Diff(intended, actual);
        Assert.Contains(diff, d => d.Path == "Start"); // DateStart — not suppressed
    }

    [Fact]
    public void Without_exclusion_historical_End_appears_as_non_RepOpenEnded_drift()
    {
        // Pre-fix: intended End = null, actual End = concrete date. IsNoEnd(null) = true but
        // IsNoEnd("2025-12-31T22:00:00Z") = false → not RepOpenEnded → shows in report.
        var intended = new { End = (string?)null, Currency = "DKK" };
        var actual   = new { End = "2025-12-31T22:00:00Z", Currency = "DKK" };

        var diff = JsonValueDiff.Diff(intended, actual);
        Assert.Contains(diff, d => d.Path == "End");

        // Verify it would NOT be categorised as RepOpenEnded (both NoEnd required).
        var row = new ReconcileDriftRow("R1", "MeteringPoint", "K1",
            "BillingConfiguration.End", null, "2025-12-31T22:00:00Z", DriftKind.FieldDiff, SubType: "Billing");
        Assert.NotEqual(DriftCategorizer.RepOpenEnded, DriftCategorizer.Categorize(row));
    }

    [Fact]
    public void With_historical_excludes_Start_and_End_are_suppressed_content_fields_still_diff()
    {
        // Post-fix: historical versions use HistoricalBillingVersionExcludes. Start/End are gone;
        // a real Currency drift (the kind of content error apply CAN fix) still surfaces.
        var intended = new { Start = "2026-01-01T00:00:00Z", End = (string?)null, Currency = "DKK" };
        var actual   = new { Start = "2022-06-01T00:00:00Z", End = "2025-12-31T22:00:00Z", Currency = "EUR" };

        var diff = JsonValueDiff.Diff(intended, actual, HistoricalExcludes);
        Assert.DoesNotContain(diff, d => d.Path == "Start");
        Assert.DoesNotContain(diff, d => d.Path == "End");
        Assert.Contains(diff, d => d.Path == "Currency"); // content drift still visible
    }

    [Fact]
    public void With_historical_excludes_a_matching_historical_version_produces_no_diff()
    {
        var intended = new { Start = "2026-01-01T00:00:00Z", End = (string?)null, Currency = "DKK" };
        var actual   = new { Start = "2022-06-01T00:00:00Z", End = "2025-12-31T22:00:00Z", Currency = "DKK" };

        var diff = JsonValueDiff.Diff(intended, actual, HistoricalExcludes);
        Assert.Empty(diff); // only Start/End differ — both excluded
    }
}

public class DriftCategorizerTests
{
    private static ReconcileDriftRow Row(string entity, string field, string? intended, string? actual, DriftKind kind = DriftKind.FieldDiff)
        => new("R1", entity, "K1", field, intended, actual, kind);

    [Theory]
    [InlineData("Customer", "Address.Fax", "", null, DriftKind.FieldDiff, DriftCategorizer.RepNullEmpty)]
    [InlineData("Billing", "BillingConfiguration.End", "9999-12-31T23:59:58.999Z", "0001-01-01T00:00:00Z", DriftKind.FieldDiff, DriftCategorizer.RepOpenEnded)]
    [InlineData("Billing", "BillingConfiguration.Start", "2026-01-01T00:00:00Z", "2024-01-01T00:00:00Z", DriftKind.FieldDiff, DriftCategorizer.DateStart)]
    [InlineData("MeteringPoint", "EstimatedAnnualVolume", "170928", "174068", DriftKind.FieldDiff, DriftCategorizer.AnnualVolume)]
    [InlineData("MeteringPoint", "InDelivery", "False", "True", DriftKind.FieldDiff, DriftCategorizer.DeliveryFlag)]
    [InlineData("Billing", "PaymentConfiguration.BuyerReference", "Fiktivvejcyn 40", "", DriftKind.FieldDiff, DriftCategorizer.BillingReference)]
    [InlineData("Payer", "PayerBilling.OrderIdentifier", "", null, DriftKind.FieldDiff, DriftCategorizer.RepNullEmpty)] // both sides empty (""/null) → benign null-vs-empty, suppressed (not Billing-reference drift)
    [InlineData("Payer", "PayerBilling.AgreementIdentifier", "", "", DriftKind.FieldDiff, DriftCategorizer.RepNullEmpty)] // both sides empty ("— → —") → suppressed, not Billing-reference drift
    [InlineData("Billing", "PaymentConfiguration.ElectronicNumber", "5798000560819", "", DriftKind.FieldDiff, DriftCategorizer.BillingReference)] // MP-level ref
    [InlineData("Billing", "PaymentConfiguration.Start", "2026-01-01T00:00:00Z", "2024-01-01T00:00:00Z", DriftKind.FieldDiff, DriftCategorizer.DateStart)] // billing date, NOT a reference
    [InlineData("MeteringPoint", "SettlementMethod", "Flex", "NonProfiled", DriftKind.FieldDiff, DriftCategorizer.SettlementMethod)]
    [InlineData("Payer", "IsPrimaryCustomerPayer", "true", "false", DriftKind.FieldDiff, DriftCategorizer.PrimaryPayerFlag)]
    [InlineData("Customer", "Address.PostalAddress.PhysicalAddress.StreetName", "A", "B", DriftKind.FieldDiff, DriftCategorizer.Address)]
    [InlineData("Payer", "PayerInformation.PostalAddress.PhysicalAddress.StreetName", "Vester Alle", "Vester Allé", DriftKind.FieldDiff, DriftCategorizer.AddressDarNormalized)] // DAR accent-normalisation
    [InlineData("Payer", "PayerInformation.PostalAddress.PhysicalAddress.StreetName", "St. Knuds Vej", "St Knuds Vej", DriftKind.FieldDiff, DriftCategorizer.AddressDarNormalized)] // punctuation/whitespace only
    [InlineData("Payer", "PayerInformation.PostalAddress.PhysicalAddress.StreetName", "Jens Baggesens Vej", "Jens Baggesens Vej 90K", DriftKind.FieldDiff, DriftCategorizer.Address)] // structural split → stays real drift
    [InlineData("Payer", "PayerInformation.PostalAddress.PhysicalAddress.BuildingNumber", "90K", "2", DriftKind.FieldDiff, DriftCategorizer.Address)] // structural → stays real drift
    [InlineData("Payer", "PayerInformation.PostalAddress.PhysicalAddress.StreetName", "Vester Alle", "", DriftKind.FieldDiff, DriftCategorizer.Address)] // value vs empty is a presence change, not normalisation
    [InlineData("Customer", "Address.Email", "a@x", "b@x", DriftKind.FieldDiff, DriftCategorizer.Contact)]
    [InlineData("Exemption", "pe-id", "100% exempt", null, DriftKind.MissingInSettl, DriftCategorizer.Missing)]
    [InlineData("Customer", "SG1", "member", null, DriftKind.MissingInSettl, DriftCategorizer.Missing)] // settlement-group: missing membership
    [InlineData("Customer", "SG2", null, "member (create would not assign)", DriftKind.Anomaly, DriftCategorizer.Anomaly)] // settlement-group: extra membership
    [InlineData("MeteringPoint", "settl-indelivery-no-xellent-match", null, "InDelivery in Settl", DriftKind.MissingInXellent, DriftCategorizer.MissingXellent)] // exists in Xellent but not reproduced → drift
    [InlineData("MeteringPoint", "settl-native-meteringpoint", null, "InDelivery in Settl (no Xellent origin)", DriftKind.MissingInXellent, DriftCategorizer.SettlNative)] // created directly in Settl → benign

    public void Categorizes_each_row(string entity, string field, string? intended, string? actual, DriftKind kind, string expected)
        => Assert.Equal(expected, DriftCategorizer.Categorize(Row(entity, field, intended, actual, kind)));

    [Fact]
    public void PayerLink_relink_fielddiff_is_its_own_category()
        => Assert.Equal(DriftCategorizer.PayerLink, DriftCategorizer.Categorize(
            new ReconcileDriftRow("R1", "MeteringPoint", "571313100000000001", "linked",
                "payer 100001", "payer 100002", DriftKind.FieldDiff, SubType: "PayerLink")));

    [Fact]
    public void PayerLink_missing_stays_missing_in_settl()
        => Assert.Equal(DriftCategorizer.Missing, DriftCategorizer.Categorize(
            new ReconcileDriftRow("R1", "MeteringPoint", "571313100000000001", "linked",
                "payer 100001", null, DriftKind.MissingInSettl, SubType: "PayerLink")));

    [Fact]
    public void PayerLink_extra_stays_missing_in_xellent()
        => Assert.Equal(DriftCategorizer.MissingXellent, DriftCategorizer.Categorize(
            new ReconcileDriftRow("R1", "MeteringPoint", "571313100000000001", "linked",
                null, "payer 100002", DriftKind.MissingInXellent, SubType: "PayerLink")));

    [Fact]
    public void PriceList_period_fielddiff_is_its_own_category()
        => Assert.Equal(DriftCategorizer.PriceListPeriod, DriftCategorizer.Categorize(
            new ReconcileDriftRow("R1", "Customer", "K1", "PE (ST 0500)",
                "2026-01-01 .. 2026-12-31", "2026-01-01 .. 2027-12-31", DriftKind.FieldDiff, SubType: "PriceList")));

    [Fact]
    public void PriceList_missing_stays_missing_in_settl_not_period()
        => Assert.Equal(DriftCategorizer.Missing, DriftCategorizer.Categorize(
            new ReconcileDriftRow("R1", "Customer", "K1", "PE", "ST 0500", null,
                DriftKind.MissingInSettl, SubType: "PriceList")));

    [Theory]
    // Match rows use the same field-based categorisation as FieldDiff rows — kind=Match is the
    // indicator that the field matched, the category tells the user WHAT matched.
    [InlineData("MeteringPoint", "SettlementMethod", "NonProfiled", "NonProfiled", DriftCategorizer.SettlementMethod)]
    [InlineData("MeteringPoint", "InDelivery", "True", "True", DriftCategorizer.DeliveryFlag)]
    [InlineData("Customer", "Address.PhysicalAddress.StreetName", "Fiktivvejewj", "Fiktivvejewj", DriftCategorizer.AddressDarNormalized)]
    [InlineData("Customer", "Name", "Firma A", "Firma A", DriftCategorizer.Other)]
    public void Match_rows_use_field_based_category(string entity, string field, string? intended, string? actual, string expected)
        => Assert.Equal(expected, DriftCategorizer.Categorize(Row(entity, field, intended, actual, DriftKind.Match)));
}

public class ReconcileCosCellTests
{
    private static ReconcileDriftRow Row(string entity, DateOnly? cos = null, DateOnly? from = null, DateOnly? to = null)
        => new("R1", entity, "K1", "f", null, null, DriftKind.FieldDiff, Cos: cos, CosFrom: from, CosTo: to);

    [Fact]
    public void MeteringPoint_row_shows_its_own_single_cos()
        => Assert.Equal("2026-05-01", ReconcileRunner.CosCell(Row("MeteringPoint", cos: new DateOnly(2026, 5, 1))));

    [Fact]
    public void Customer_row_shows_period_when_min_and_max_differ()
        => Assert.Equal("2026-05-01 .. 2026-06-03",
            ReconcileRunner.CosCell(Row("Customer", from: new DateOnly(2026, 5, 1), to: new DateOnly(2026, 6, 3))));

    [Fact]
    public void Customer_row_collapses_period_when_min_equals_max()
        => Assert.Equal("2026-05-01",
            ReconcileRunner.CosCell(Row("Customer", from: new DateOnly(2026, 5, 1), to: new DateOnly(2026, 5, 1))));

    [Fact]
    public void Non_mp_row_without_range_is_blank()
        => Assert.Equal("", ReconcileRunner.CosCell(Row("Payer")));
}

public class ReconcileEndEquivalentTests
{
    // Settl stores a price-list association period as a half-open interval [Start, End): the import
    // takes the inclusive Xellent end the export wrote and persists the upper bound as the day after.
    // The intended side is the raw inclusive end, so the equivalent actual is intendedEnd + 1.
    [Fact]
    public void Inclusive_intended_matches_exclusive_actual_one_day_later()
        => Assert.True(ReconcileRunner.EndEquivalent(new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 1)));

    [Fact]
    public void Same_concrete_date_is_not_equivalent_actual_must_be_plus_one()
        => Assert.False(ReconcileRunner.EndEquivalent(new DateOnly(2027, 1, 31), new DateOnly(2027, 1, 31)));

    [Fact]
    public void Real_end_drift_stays_flagged()
        => Assert.False(ReconcileRunner.EndEquivalent(new DateOnly(2026, 6, 30), new DateOnly(2029, 7, 1)));

    [Fact]
    public void Both_open_ended_are_equivalent()
        => Assert.True(ReconcileRunner.EndEquivalent(new DateOnly(1900, 1, 1), null));

    [Fact]
    public void Open_intended_vs_concrete_actual_is_not_equivalent()
        => Assert.False(ReconcileRunner.EndEquivalent(new DateOnly(1900, 1, 1), new DateOnly(2026, 5, 27)));

    [Fact]
    public void Concrete_intended_vs_open_actual_is_not_equivalent()
        => Assert.False(ReconcileRunner.EndEquivalent(new DateOnly(2026, 5, 27), null));
}

public class CosSlicerTests
{
    [Fact]
    public void RefDate_is_cos_minus_one_day()
    {
        Assert.Equal(new DateOnly(2024, 12, 31), CosSlicer.RefDateFor(new DateOnly(2025, 1, 1)));
    }
}

public class ReconcileResultStoreTests
{
    [Fact]
    public async Task Save_then_load_round_trips_rows_and_metadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reconcile-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReconcileResultStore(dir);
            var rows = new List<ReconcileDriftRow>
            {
                new("267992", "MeteringPoint", "571313100000000001", "PaymentConfiguration.BuyerReference",
                    "Kontraktnr. 1", "E3456", DriftKind.FieldDiff, SubType: "Billing", Cos: new DateOnly(2026, 4, 1),
                    MemberAccounts: "267992,267993", RepresentativeName: "Firma A A/S",
                    MemberNames: "Firma A A/S,Filial B"),
                new("267992", "Customer", "267992", "*", "create payload", null, DriftKind.MissingInSettl,
                    MemberAccounts: "267992,267993", RepresentativeName: "Firma A A/S",
                    MemberNames: "Firma A A/S,Filial B", EntityName: "Firma A A/S"),
            };
            var result = new ReconcileResult("production", "hol", new DateTime(2026, 6, 4, 8, 30, 0, DateTimeKind.Utc), rows);

            await store.SaveAsync(result);
            var loaded = await store.LoadAsync("production");

            Assert.NotNull(loaded);
            Assert.Equal(result.Env, loaded!.Env);
            Assert.Equal(result.DataAreaId, loaded.DataAreaId);
            Assert.Equal(result.GeneratedAtUtc, loaded.GeneratedAtUtc);
            Assert.Equal(rows, loaded.Rows); // record value-equality incl. SubType + DriftKind + Cos + MemberAccounts
            // COS survives the round-trip as an ISO date on the MeteringPoint row (null on Customer).
            Assert.Equal(new DateOnly(2026, 4, 1), loaded.Rows[0].Cos);
            Assert.Null(loaded.Rows[1].Cos);
            // MemberAccounts (merge-group, representative-level) survives on every row under the rep.
            Assert.Equal("267992,267993", loaded.Rows[0].MemberAccounts);
            Assert.Equal("267992,267993", loaded.Rows[1].MemberAccounts);
            // Names survive: rep name + member names (parallel to accounts) on every row; EntityName on the Customer row.
            Assert.Equal("Firma A A/S", loaded.Rows[1].RepresentativeName);
            Assert.Equal("Firma A A/S,Filial B", loaded.Rows[1].MemberNames);
            Assert.Equal("Firma A A/S", loaded.Rows[1].EntityName);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_returns_null_when_no_cache_for_env()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reconcile-cache-test-" + Guid.NewGuid().ToString("N"));
        var store = new ReconcileResultStore(dir);
        Assert.Null(await store.LoadAsync("featuretest"));
    }
}
