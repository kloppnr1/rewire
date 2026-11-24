using System.Text.Json;
using DataMigration.OpsCli.Services;
using DataMigration.Migration.Configuration;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class MigrationRunModelTests : IDisposable
{
    private const string Mp = "571313104402063390";
    private const string OtherMp = "571313104402063437";
    private readonly string root = Path.Combine(Path.GetTempPath(), "migration-model-" + Guid.NewGuid());
    private string Reports => Path.Combine(root, "reports");
    private string Output => Path.Combine(root, "model");
    public MigrationRunModelTests() => Directory.CreateDirectory(Reports);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Completed_failed_runs_are_included_but_dry_and_partial_runs_are_excluded()
    {
        await Write("finished", false, false, false, Mp);
        await Write("dry", false, true, true, OtherMp);
        await Write("unfinished", true, false, true, OtherMp);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        Assert.Equal(Mp, Assert.Single(loaded.Model.MeteringPoints).Key);
        var run = Assert.Single(loaded.Model.Runs);
        Assert.False(run.Success);
        Assert.Equal(1, run.ActionCounts["ContractMpsFailed"]);
        var manifest = JsonSerializer.Deserialize<MigrationModelManifest>(await File.ReadAllTextAsync(Path.Combine(Output, "manifest.json")))!;
        Assert.True(manifest.Complete);
        Assert.Equal(3, manifest.Sources.Count);
        Assert.Single(manifest.Sources, s => s.Included);
        Assert.All(manifest.Sources, s => Assert.Equal(64, s.Sha256.Length));
    }

    [Fact]
    public async Task Model_is_scope_independent_and_loads_without_original_reports()
    {
        await Write("one", false, false, true, Mp);
        await Write("two", false, false, true, OtherMp);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        Directory.Delete(Reports, true);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        Assert.Equal(2, loaded.Model.MeteringPoints.Count);
        Assert.Equal("456192", Assert.Single(loaded.Model.MeteringPoints[Mp].Observations.Single().Accounts));
        Assert.Equal(DateOnly.MaxValue, loaded.Model.MeteringPoints[Mp].Observations.Single().Contracts.Single().End);
        var prepared = MigrationRunModel.Apply(Source(), loaded, [Mp]);
        prepared = await MigrationRunModel.ApplyDetailsAsync(prepared, loaded, [Mp], default);
        Assert.Equal(new DateOnly(2016, 4, 1), prepared.MigrationRelationships[Mp].LinkStart);
        Assert.Equal(new DateOnly(2016, 4, 1), Assert.Single(prepared.Customers).Customer.Address!.Start);
        Assert.Equal("Synthetic customer", prepared.Customers[0].Customer.Address!.PostalAddress!.Name1!.FullName);
        Assert.Equal("Synthetic payer", prepared.Customers[0].Payers[0].PayerInformation!.PostalAddress!.Name1!.FullName);
        Assert.Equal(new DateOnly(2016, 4, 1), prepared.Customers[0].Payers[0].PayerInformation!.Start);
    }

    [Fact]
    public async Task Archived_details_are_checked_before_using_report_values()
    {
        await Write("one", false, false, true, Mp);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var source = MigrationRunModel.Apply(Source(), loaded, [Mp]);
        await File.AppendAllTextAsync(Path.Combine(Output, loaded.Model.Runs[0].DetailsFile!), "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunModel.ApplyDetailsAsync(source, loaded, [Mp], default));
    }

    [Theory]
    [InlineData("OrganizationIdentifier")]
    [InlineData("VatIdentificationNumber")]
    public async Task Payer_vat_is_read_from_report_identity_field(string field)
    {
        await Write("one", false, false, true, Mp);
        var file = Path.Combine(Reports, "one.report.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        root["Result"]!["Customers"]![0]!["Payers"]![0]![field] = "36665030";
        await File.WriteAllTextAsync(file, root.ToJsonString());
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var source = MigrationRunModel.ApplyForImport(Source(), loaded, [Mp]);
        source = await MigrationRunModel.ApplyDetailsAsync(source, loaded, [Mp], default);
        Assert.Equal("36665030", Assert.Single(source.Customers).Payers.Single().PayerBilling!.VatIdentificationNumber);
    }

    [Fact]
    public async Task Unreadable_report_never_publishes_complete_model()
    {
        await Write("one", false, false, true, Mp);
        await File.WriteAllTextAsync(Path.Combine(Reports, "broken.report.json"), "{");
        await Assert.ThrowsAnyAsync<JsonException>(() => MigrationRunModel.BuildAsync(Reports, Output, default));
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }

    [Fact]
    public async Task Changed_model_and_existing_output_are_rejected()
    {
        await Write("one", false, false, true, Mp);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        await Assert.ThrowsAsync<IOException>(() => MigrationRunModel.BuildAsync(Reports, Output, default));
        var file = Path.Combine(Output, "migration-model.json");
        await File.AppendAllTextAsync(file, " ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunModel.LoadAsync(file, default));
    }

    [Fact]
    public async Task Original_five_digit_and_padded_report_identifiers_are_one_account()
    {
        var windows = new[] { new { ContractName = "V Variabel", ProductNum = "V Variabel", Start = "2026-05-01" } };
        var data = new { RunId = "padding", WrittenAt = "2026-09-01T10:00:00Z", Partial = false,
            Result = new { IsDryRun = false, Success = true, Customers = new[] { new { AccountNum = "912345", AddressStart = "2016-04-01",
                Payers = new[] { new { PayerIdentifier = "912345" } },
                MeteringPoints = new[] { new { MeteringPointId = Mp, IdentificationStart = "2016-04-01", Contracts = windows } } } } },
            Report = new { DryRun = false, AccountMeteringPoints = new Dictionary<string, string[]> { ["12345"] = [Mp] },
                MpContractWindows = new Dictionary<string, object> { [Mp] = windows } } };
        await File.WriteAllTextAsync(Path.Combine(Reports, "padding.report.json"), JsonSerializer.Serialize(data));
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var observation = Assert.Single(loaded.Model.MeteringPoints[Mp].Observations);
        Assert.Equal("12345", Assert.Single(observation.Accounts));
        Assert.Empty(observation.Findings);
    }

    [Theory]
    [InlineData(false, "", 1)]
    [InlineData(true, "Completed migration reports disagree.", 2)]
    public async Task Matching_reports_share_basis_and_conflicts_are_not_chosen_by_recency(bool conflict, string error, int expected)
    {
        await Write("one", false, false, true, Mp);
        await Write("two", false, false, true, Mp, conflict ? "Other contract" : "V Variabel");
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var applied = MigrationRunModel.Apply(Source(), loaded, [Mp]);
        Assert.Equal(expected, applied.ExpectedContracts!.Count);
        Assert.Equal(2, applied.MigrationReports[Mp].Length);
        if (conflict) Assert.Equal(error, applied.ContractMappingErrors[Mp]);
        else Assert.Empty(applied.ContractMappingErrors);
        Assert.Equal(loaded.Sha256, applied.MigrationModelSha256);
    }

    [Fact]
    public async Task Missing_mp_and_changed_xellent_do_not_fall_back_to_snapshot_contract()
    {
        await Write("one", false, false, true, Mp);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var source = Source() with { ExpectedContracts = [new(Mp, "Changed contract", new(2026, 5, 1), DateOnly.MaxValue, "V Variabel", "source")] };
        var applied = MigrationRunModel.Apply(source, loaded, [Mp, OtherMp]);
        Assert.Equal("V Variabel", Assert.Single(applied.ExpectedContracts!).ContractName);
        Assert.Equal("Migration report and Xellent contracts or dates disagree.", applied.ContractMappingErrors[Mp]);
        Assert.Equal("MP missing from completed migration reports.", applied.ContractMappingErrors[OtherMp]);
    }

    [Fact]
    public void Source_findings_are_scoped_to_the_affected_account_or_mp()
    {
        var errors = new Dictionary<string, string>();
        var values = CustomerPayerRepairCapture.AttributeSourceErrors([
            "Customer 456192: invalid email format 'invalid'", "MP " + OtherMp + ": invalid DAR identifier 'invalid'"], [Mp], ["456192"], errors);
        Assert.Empty(errors);
        Assert.Equal("Source customer/payer values require review.", values["456192"]);
        CustomerPayerRepairCapture.AttributeSourceErrors(["MP " + Mp + ": invalid DAR identifier 'invalid'"], [Mp], ["456192"], errors);
        Assert.Equal("Source MP address requires review.", errors[Mp]);
    }

    [Fact]
    public void Customer_payer_end_boundary_does_not_overlap_new_start_and_max_end_is_open()
    {
        var owner = Guid.NewGuid(); var start = new DateOnly(2026, 9, 1);
        Assert.True(CustomerPayerRepair.NeedsLink([(owner, start.AddMonths(-1), start)], owner, start));
        Assert.False(CustomerPayerRepair.NeedsLink([(owner, start.AddMonths(-1), DateOnly.MaxValue)], owner, start));
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.NeedsLink([(owner, start.AddMonths(-1), start.AddDays(1))], owner, start));
    }

    [Fact]
    public async Task Full_import_uses_latest_completed_report_and_xellent_differences_are_warnings()
    {
        await Write("one", false, false, true, Mp);
        await Write("two", false, false, true, Mp, "Latest contract");
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var applied = MigrationRunModel.ApplyForImport(Source() with { Routes = [] }, loaded, [Mp]);
        Assert.True(applied.IncludeAllMigrationRecords);
        Assert.Empty(applied.ContractMappingErrors);
        Assert.Equal("Latest contract", Assert.Single(applied.ExpectedContracts!).ContractName);
        Assert.Equal("two.report.json", Assert.Single(applied.MigrationReports[Mp]));
        Assert.Contains("Latest completed migration report selected.", applied.MigrationWarnings[Mp]);
        Assert.Contains("Migration report and Xellent account disagree.", applied.MigrationWarnings[Mp]);
        applied = await MigrationRunModel.ApplyDetailsAsync(applied, loaded, [Mp], default);
        Assert.Single(applied.Customers);
        Assert.Equal(new DateOnly(2016, 4, 1), applied.Customers[0].Customer.Address!.Start);
    }

    [Theory]
    [InlineData("V LadVariP", "O HCEjeUL3", false)]
    [InlineData("V LadVariP", "O HCEjeUL4", false)]
    [InlineData("V LadVariP", "O HCEjeUL5", false)]
    [InlineData("V LadVarUA", "O HCEjeUL3", true)]
    [InlineData("V LadVarUA", "O HCEjeUL4", true)]
    [InlineData("V LadVarUA", "O HCEjeUL5", true)]
    public async Task Confirmed_homecharge_mapping_preserves_periods_and_flags_ua(string product, string chargerProduct, bool manual)
    {
        await Write("one", false, false, true, Mp, product);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var original = loaded.Model.MeteringPoints[Mp].Observations.Single();
        original.Evidence["EvChargersByMeteringPoint"] = JsonSerializer.SerializeToElement(new[] { new { Parts = new[] { new { ProductCode = chargerProduct } } } });
        var result = MigrationRunModel.ApplyForImport(Source(), loaded, [Mp]);
        var contract = Assert.Single(result.ExpectedContracts!);
        Assert.Equal("HomeChargeLadeEjeMel(O HCEjeMEL)", contract.ContractName);
        Assert.Equal(original.Contracts[0].Start, contract.Start);
        Assert.Equal(original.Contracts[0].End, contract.End);
        Assert.Equal(product, original.Contracts[0].ContractName);
        Assert.Contains(chargerProduct, contract.MappingNote!);
        Assert.Equal(manual, result.MigrationWarnings[Mp].Any(w => w.StartsWith("Manual UA follow-up after import:")));
        Assert.Equal(manual, result.MigrationWarnings[Mp].Select(RepairEvidenceReports.Reason).Any(w => w.StartsWith("Manuel opfølgning efter import: UA-periode")));
    }

    [Fact]
    public async Task Resolved_contract_names_are_not_remapped_by_homecharge_evidence()
    {
        await Write("one", false, false, true, Mp);
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        loaded.Model.MeteringPoints[Mp].Observations.Single().Evidence["EvChargersByMeteringPoint"] =
            JsonSerializer.SerializeToElement(new[] { new { Parts = new[] { new { ProductCode = "O HCEjeUL3" } } } });
        var result = MigrationRunModel.ApplyForImport(Source(), loaded, [Mp]);
        Assert.Equal("V Variabel", Assert.Single(result.ExpectedContracts!).ContractName);
        Assert.Null(result.ExpectedContracts![0].MappingNote);
    }

    [Theory]
    [InlineData("V P Hybel", "Partner_HybelStenhøj")]
    [InlineData("VGGSpot10", "E_VGGSpot10_Lb+1d")]
    public async Task Shared_product_mappings_resolve_model_fallback_names(string product, string target)
    {
        await Write("one", false, false, true, Mp, product);
        var file = Path.Combine(Reports, "one.report.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        root["Report"]!["MpContractWindows"]![Mp]![0]!["ProductNum"] = product;
        root["Report"]!["MpPaymentTerms"] = JsonSerializer.SerializeToNode(new Dictionary<string, string> { [Mp] = "Lbm+1d" });
        await File.WriteAllTextAsync(file, root.ToJsonString());
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var mappings = new MappingOptions { ProductNum = new() { [product] = product == "VGGSpot10"
            ? new() { ByPaymentTerm = new() { ["lbm+1d"] = target } } : ProductNumContractEntry.FromString(target) } };
        var result = MigrationRunModel.ApplyForImport(Source(), loaded, [Mp], mappings);
        var contract = Assert.Single(result.ExpectedContracts!);
        Assert.Equal(target, contract.ContractName);
        Assert.Equal(new DateOnly(2026, 5, 1), contract.Start);
        Assert.Contains("Bekræftet produktregel", contract.MappingNote!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_charger_fallback_requires_explicit_archived_lookup_evidence(bool lookupsRecorded)
    {
        await Write("one", false, false, true, Mp, "V LadVariP");
        var file = Path.Combine(Reports, "one.report.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        if (lookupsRecorded)
        {
            root["Report"]!["EvChargersByAccount"] = new System.Text.Json.Nodes.JsonObject();
            root["Report"]!["EvChargersByMeteringPoint"] = new System.Text.Json.Nodes.JsonObject();
        }
        await File.WriteAllTextAsync(file, root.ToJsonString());
        await MigrationRunModel.BuildAsync(Reports, Output, default);
        var loaded = await MigrationRunModel.LoadAsync(Path.Combine(Output, "migration-model.json"), default);
        var result = await MigrationRunModel.ApplyDetailsAsync(MigrationRunModel.ApplyForImport(Source(), loaded, [Mp]), loaded, [Mp], default);
        var contract = Assert.Single(result.ExpectedContracts!);
        Assert.Equal(lookupsRecorded ? "LadeAbo_Afventer_Installation" : "V LadVariP", contract.ContractName);
        Assert.Equal(new DateOnly(2026, 5, 1), contract.Start);
    }

    [Theory]
    [InlineData("V Variabel", "V Variabel UA")]
    [InlineData("V Kvartal", "V Kvartal UA")]
    [InlineData("LadeAbo_Afventer_Installation", "LadeAbo_Afventer_Installation_ElUA")]
    public void Model_discount_selects_ua_during_discount_and_ordinary_afterwards(string ordinary, string ua)
    {
        var start = new DateOnly(2026, 9, 1); var end = new DateOnly(2027, 3, 31);
        var result = AborabatRepair.ResolveModelDiscounts([
            new(Mp, "V AboRabat", start, end, "V AboRabat", "report"),
            new(Mp, ordinary, start, DateOnly.MaxValue, "V LadVariP", "report")]);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.ContractName == ua && c.Start == start && c.End == end);
        Assert.Contains(result, c => c.ContractName == ordinary && c.Start == end.AddDays(1) && c.End == DateOnly.MaxValue);
        Assert.DoesNotContain(result, c => c.ContractName == "V AboRabat");
    }

    private static RepairXellentEvidence Source() => new(DateTimeOffset.UtcNow, new(2026, 8, 31), [],
        [new(Mp, "456192", "456192", "contract", new(2026, 5, 1), DateOnly.MaxValue) { SourceAccount = "456192" }])
    { ExpectedContracts = [new(Mp, "V Variabel", new(2026, 5, 1), DateOnly.MaxValue, "V Variabel", "source")] };

    private async Task Write(string name, bool partial, bool dry, bool success, string mp, string contract = "V Variabel")
    {
        var data = new
        {
            RunId = name, WrittenAt = "2026-09-01T10:00:00Z", Partial = partial,
            Result = new { IsDryRun = dry, Success = success, ContractMpsFailed = success ? 0 : 1,
                Customers = new[] { new { AccountNum = "456192", AddressStart = "2016-04-01", Name = "Synthetic customer", CustomerType = "Company", CountryCode = "DK",
                    StreetName = "Test Street", BuildingNumber = "1", PostalCode = "8900", City = "Test City",
                    Payers = new[] { new { PayerIdentifier = "456192", PayerName = "Synthetic payer", PayerStreetName = "Test Street", PayerBuildingNumber = "1", PayerPostalCode = "8900", PayerCity = "Test City", InvoiceChannel = "Paper", PaymentChannel = "FIK", PayerType = "Company" } },
                    MeteringPoints = new[] { new { MeteringPointId = mp, IdentificationStart = "2016-04-01" } } } } },
            Report = new
            {
                DryRun = dry,
                AccountMeteringPoints = new Dictionary<string, string[]> { ["456192"] = [mp] },
                MpContractWindows = new Dictionary<string, object[]> { [mp] = [new { ContractName = contract, ProductNum = "V Variabel", Start = "2026-05-01", IsJoined = false }] }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(Reports, name + ".report.json"), JsonSerializer.Serialize(data));
    }
}
