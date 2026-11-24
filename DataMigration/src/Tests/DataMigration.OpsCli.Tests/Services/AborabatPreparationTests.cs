using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class AborabatPreparationTests
{
    private static readonly DateOnly Reference = new(2026, 8, 31);

    [Fact]
    public async Task Old_charger_snapshot_requests_fresh_xellent_address_evidence()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                [Part("V AboRabat", "2026-07-01", "2026-09-30", "Live"),
                 Part("V LadVariP", "2026-07-01", "2026-09-30", "Live")], [],
                [new XellentChargerAssignmentRow("MP1",
                    new EvCharger("I1", "A1", "C1", "Fiktivvejewj 1", [])
                    { AddressMatchesMeteringPoint = false }, Reference)]);
            var store = StateSourceSnapshotStore.Open(directory, "production");
            await store.BeginUpdateAsync(CancellationToken.None);
            await store.WriteAsync("xellent-charger-assignments",
                [new XellentChargerAssignmentRow("MP1",
                    new EvCharger("I1", "A1", "C1", "Fiktivvejewj 1", [])
                    { AddressMatchesMeteringPoint = false }, Reference)], CancellationToken.None);
            await store.CompleteAsync(CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None));
            Assert.Contains("--only xellent", error.Message);
            Assert.Contains("--reference-date 2026-08-31", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Live_and_deleted_products_create_separate_targets_from_Xellent_dates()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-07-01", "2026-09-30", "Live"),
                 Part("V Variabel", "2026-07-01", "2026-08-31", "Live")],
                logged:
                [Part("V Kvartal", "2026-09-01", "2026-09-30", "Log")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Equal(7, plan.Version);
            Assert.Equal(0, plan.ExcludedCount);
            Assert.Equal(3, plan.Review.Count);
            Assert.All(plan.Review, row =>
            {
                Assert.Equal("Resolved", row.Status);
                Assert.Empty(row.Reason);
            });
            var item = Assert.Single(plan.Items);
            Assert.Equal(2, item.Targets.Count);
            Assert.Contains(item.Targets, t => t.ContractName == "V Variabel UA"
                && t.Start == new DateOnly(2026, 7, 1) && t.End == new DateOnly(2026, 8, 31));
            Assert.Contains(item.Targets, t => t.ContractName == "V Kvartal UA"
                && t.Start == new DateOnly(2026, 9, 1) && t.End == new DateOnly(2026, 9, 30));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task End_date_change_logs_restore_the_end_of_the_matching_current_part()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live"),
                 Part("V Variabel", "2026-06-01", "2026-08-31", "Live")],
                logged:
                [Part("V AboRabat", "2026-06-01", "2026-11-30", "Log", "Slutdato er ændret"),
                 Part("V Variabel", "2026-06-01", "2026-11-30", "Log", "Slutdato er ændret")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            var target = Assert.Single(Assert.Single(plan.Items).Targets);
            Assert.Equal("V Variabel UA", target.ContractName);
            Assert.Equal(new DateOnly(2026, 11, 30), target.End);
            Assert.Contains(plan.Review, row => row.ProductNumber == "V AboRabat"
                && row.Source == "Aktuel" && row.End == "2026-11-30"
                && row.EffectiveEnd == "2026-11-30");
            var discount = Assert.Single(plan.Review, row => row.ProductNumber == "V AboRabat");
            Assert.Contains("Xellent-log 1, 31-08-2026 09:30:00", discount.Evidence);
            Assert.Contains("Registreret slutdato: 30-11-2026", discount.Evidence);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Ended_parts_and_other_customers_do_not_enter_repair_or_review()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-08-01", "2026-11-30", "Live"),
                 Part("V Variabel", "2026-08-01", "2026-11-30", "Live"),
                 Part("V Kvartal", "2024-01-01", "2024-10-13", "Live"),
                 Part("V AboRabat", "2022-04-01", "2024-10-13", "Live") with
                     { Account = "A2", ContractNumber = "C2" },
                 Part("V Kvartal", "2022-04-01", "2024-10-13", "Live") with
                     { Account = "A2", ContractNumber = "C2" },
                 Part("V Kvartal", "2026-09-01", "2026-12-31", "Live") with
                     { Account = "A2", ContractNumber = "C2" }],
                logged:
                [Part("V Kvartal", "2022-04-01", "1900-01-01", "Log", "Slutdato er ændret") with
                    { Account = "A2", ContractNumber = "C2" }]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            var target = Assert.Single(Assert.Single(plan.Items).Targets);
            Assert.Equal("V Variabel UA", target.ContractName);
            Assert.Equal(2, plan.Review.Count);
            Assert.All(plan.Review, row =>
            {
                Assert.Equal("Resolved", row.Status);
                Assert.Equal("A1", row.Account);
                Assert.DoesNotContain("2024", row.Start);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Only_ended_discounts_are_out_of_scope()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2022-04-01", "2024-10-13", "Live"),
                 Part("V Kvartal", "2022-04-01", "2024-10-13", "Live")],
                logged: []);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Empty(plan.Items);
            Assert.Equal(0, plan.ExcludedCount);
            Assert.Empty(plan.Review);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Expired_Xellent_discount_with_active_Settl_link_is_delete_only()
    {
        var directory = NewDirectory();
        try
        {
            var staleLink = new SettlContractLinkRow("MP1", Guid.NewGuid(), "V AboRabat",
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(1)),
                DateTimeOffset.MaxValue, Guid.NewGuid());
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-03-01", "2026-05-31", "Live"),
                    Part("V Variabel", "2026-03-01", "2026-05-31", "Live")],
                logged: [], extraLinks: [staleLink]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            var item = Assert.Single(plan.Items);
            Assert.Empty(item.Targets);
            Assert.Equal(staleLink.AssociationId, item.OldAssociationId);
            Assert.Equal(0, plan.ExcludedCount);
            var review = Assert.Single(plan.Review);
            Assert.Equal("Resolved", review.Status);
            Assert.Contains("slettes", review.PossibleSettlContracts);
            Assert.Contains(plan.Parts, p => p.Product == "V AboRabat"
                && p.End == new DateOnly(2026, 5, 31));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Two_relevant_customers_on_one_MP_are_not_combined_into_a_repair()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-08-01", "2026-11-30", "Live"),
                 Part("V Variabel", "2026-08-01", "2026-11-30", "Live"),
                 Part("V AboRabat", "2026-09-01", "2026-12-31", "Live") with
                     { Account = "A2", ContractNumber = "C2" },
                 Part("V Kvartal", "2026-09-01", "2026-12-31", "Live") with
                     { Account = "A2", ContractNumber = "C2" }],
                logged: []);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Empty(plan.Items);
            Assert.Equal(1, plan.ExcludedCount);
            Assert.All(plan.Review, row =>
            {
                Assert.Equal("Unresolved", row.Status);
                Assert.Equal("Kan ikke udledes", row.PossibleSettlContracts);
                Assert.Contains("Flere Xellent-kunder", row.Reason);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Snapshot_without_log_change_types_must_be_refreshed()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live")],
                logged: [Part("V AboRabat", "2026-06-01", "2026-11-30", "Log", null)]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None));
            Assert.Contains("--only xellent", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Conflicting_current_and_restored_products_are_unresolved()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-08-01", "1900-01-01", "Live"),
                 Part("V Variabel", "2026-08-01", "1900-01-01", "Live")],
                logged:
                [Part("V Kvartal", "2026-08-01", "1900-01-01", "Log")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Empty(plan.Items);
            Assert.All(plan.Review, row => Assert.Contains("samme periode", row.Reason));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Review_CSV_has_one_row_per_effective_part_including_restored_deletions()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-07-01", "2026-09-30", "Live"),
                 Part("V Variabel", "2026-07-01", "2026-09-30", "Live")],
                logged:
                [Part("V Fordel", "2026-08-01", "2026-09-30", "Log"),
                 Part("V AboRabat", "2026-07-01", "2026-11-30", "Log", "Slutdato er ændret")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Empty(plan.Items);
            Assert.Equal(1, plan.ExcludedCount);
            Assert.Equal(3, plan.Review.Count);
            Assert.All(plan.Review, row => Assert.Equal("Unresolved", row.Status));
            Assert.Contains(plan.Review, row => row.ProductNumber == "V AboRabat"
                && row.Source == "Aktuel" && row.End == "2026-11-30"
                && row.CountsAsPart == "Ja");
            Assert.DoesNotContain(plan.Review, row => row.ProductNumber == "V AboRabat" && row.Source == "Log");
            Assert.Contains(plan.Review, row => row.ProductNumber == "V Fordel"
                && row.Source == "Log"
                && row.ChangeType == "Fremtidig kontraktdel slettet ved migrering"
                && row.CountsAsPart == "Ja");
            var csvPath = Path.Combine(directory, "review.csv");
            await AborabatRepair.WriteReviewCsvAsync(plan, csvPath, CancellationToken.None);
            var lines = await File.ReadAllLinesAsync(csvPath);
            Assert.Equal(4, lines.Length);
            var csv = string.Join('\n', lines);
            Assert.Contains("Xellent-kundenummer", csv);
            Assert.Contains("Xellent-kontraktnummer", csv);
            Assert.Contains("Status", csv);
            Assert.Contains("Uafklaret", csv);
            Assert.Contains("Logændring", csv);
            Assert.DoesNotContain("aftalenummer", csv, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("installationsnummer", csv, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("A1", csv);
            Assert.Contains("C1", csv);
            Assert.Contains("V Fordel", csv);
            Assert.Contains("Fremtidig kontraktdel slettet ved migrering", csv);
            Assert.Contains("Xellent-grundlag for slutdato", csv);
            Assert.Contains("Grundlag for kontraktvalg", csv);
            Assert.Contains("Slutdato er ændret", csv);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task August_electricity_window_does_not_restore_charger_deletions()
    {
        var directory = NewDirectory();
        try
        {
            var charger = new EvCharger("I1", "A1", "CC1", null, [], true);
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-09-01", "2026-12-31", "Live"),
                 Part("V LadVariP", "2026-09-01", "2026-12-31", "Live")],
                logged: [],
                chargers: [new("MP1", charger, Reference)],
                subscriptions:
                [new("A1", "CC1", "OAboEje0", new DateTime(2026, 9, 1),
                    new DateTime(2026, 12, 31), "Log", new DateTime(2026, 8, 31, 9, 30, 0),
                    "Kontraktdel er slettet"),
                 new("A1", "CC1", "OAboEjeMEL", new DateTime(2026, 11, 1),
                    new DateTime(2026, 8, 31), "Log", new DateTime(2026, 8, 31, 9, 30, 0),
                    "Kontraktdel er slettet")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Equal(0, plan.ExcludedCount);
            var target = Assert.Single(Assert.Single(plan.Items).Targets);
            Assert.Equal("V Variabel UA", target.ContractName);
            Assert.Equal(new DateOnly(2026, 9, 1), target.Start);
            Assert.Equal(new DateOnly(2026, 12, 31), target.End);
            Assert.DoesNotContain(plan.Review, row => row.PartType == "Laderabonnement");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Completed_gap_before_reference_is_not_imported()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-06-01", "2026-11-30", "Live"),
                    Part("V LadVariP", "2026-06-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new("MP1", new EvCharger("I1", "A1", "CC1", null, [], true), Reference)],
                subscriptions: [new("A1", "CC1", "OAboEje0", new DateTime(2026, 6, 4),
                    new DateTime(2026, 12, 3), "Live", null)],
                extraLinks: [new("OTHER", Guid.NewGuid(), "LadeAbo_Eje0kr_Variabel",
                    DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid())]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Equal(0, plan.ExcludedCount);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.DoesNotContain(targets, t => t.End < Reference);
            Assert.DoesNotContain(targets, t => t.ContractName == "V Variabel UA");
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Eje0kr_VariabelUA"
                && t.Start == new DateOnly(2026, 6, 4) && t.End == new DateOnly(2026, 11, 30));
            Assert.Contains(targets, t => t.ContractName == "V Variabel"
                && t.Start == new DateOnly(2026, 12, 4) && t.End == DateOnly.MaxValue);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Existing_pending_installation_link_uses_pending_UA_during_discount()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live"),
                    Part("V LadVariP", "2026-06-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new(null, new EvCharger("I0", "OTHER", "C0", null, []), Reference)],
                extraLinks:
                [new("MP1", Guid.NewGuid(), "LadeAbo_Afventer_Installation",
                    new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.FromHours(2)),
                    DateTimeOffset.MaxValue, Guid.NewGuid()),
                 new("OTHER", Guid.NewGuid(), "LadeAbo_Afventer_Installation_ElUA",
                    DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid())]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Equal(0, plan.ExcludedCount);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Afventer_Installation_ElUA"
                && t.Start == new DateOnly(2026, 6, 1) && t.End == Reference);
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Afventer_Installation"
                && t.Start == new DateOnly(2026, 9, 1) && t.End == DateOnly.MaxValue);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Charger_on_another_MP_is_shown_as_evidence_not_assigned_to_this_MP()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-07-01", "2026-12-31", "Live"),
                    Part("V LadVariP", "2026-07-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new("MP2", new EvCharger("I1", "A1", "CC1", "Road  21", [], true), Reference)],
                subscriptions: [new("A1", "CC1", "OAboEje0", new DateTime(2026, 7, 1),
                    new DateTime(2026, 12, 31), "Live", null)]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Empty(plan.Items);
            var review = Assert.Single(plan.Review, r => r.ProductNumber == "V AboRabat");
            Assert.Contains("MP2", review.Reason);
            Assert.Contains("Road  21", review.Reason);
            Assert.Contains(plan.Assignments, a => a.MeteringPoint == "MP2" && a.InstallationNumber == "I1");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Electricity_before_pending_installation_uses_V_Variabel()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-09-01", "2026-11-30", "Live"),
                    Part("V LadVariP", "2026-08-17", "2026-08-31", "Live"),
                    Part("V LadVariP", "2026-09-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new(null, new EvCharger("I0", "OTHER", "C0", null, []), Reference)],
                extraLinks:
                [new("MP1", Guid.NewGuid(), "LadeAbo_Afventer_Installation",
                    new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)),
                    DateTimeOffset.MaxValue, Guid.NewGuid()),
                 new("OTHER", Guid.NewGuid(), "LadeAbo_Afventer_Installation_ElUA",
                    DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid())]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Equal(0, plan.ExcludedCount);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.Contains(targets, t => t.ContractName == "V Variabel"
                && t.Start == new DateOnly(2026, 8, 17) && t.End == Reference);
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Afventer_Installation_ElUA"
                && t.Start == new DateOnly(2026, 9, 1) && t.End == new DateOnly(2026, 11, 30));
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Afventer_Installation"
                && t.Start == new DateOnly(2026, 12, 1) && t.End == DateOnly.MaxValue);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Discount_before_electricity_start_does_not_require_unsupported_UA_period()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-05-01", "2026-10-31", "Live"),
                    Part("V LadVariP", "2026-06-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new("MP1", new EvCharger("I1", "A1", "CC1", null, [], true), Reference)],
                subscriptions: [new("A1", "CC1", "OAboEje0", new DateTime(2026, 5, 11),
                    new DateTime(2026, 11, 10), "Live", null)],
                extraLinks: [new("OTHER", Guid.NewGuid(), "LadeAbo_Eje0kr_Variabel",
                    DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid())]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Equal(0, plan.ExcludedCount);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Eje0kr_VariabelUA"
                && t.Start == new DateOnly(2026, 6, 1) && t.End == new DateOnly(2026, 10, 31));
            Assert.DoesNotContain(targets, t => t.Start < new DateOnly(2026, 6, 1));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Later_charger_end_date_log_restores_period_at_reference_date()
    {
        var directory = NewDirectory();
        try
        {
            var charger = new EvCharger("I1", "A1", "CC1", null, [], true);
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-06-01", "2026-11-30", "Live"),
                 Part("V LadVariP", "2026-06-01", "2026-11-30", "Live")],
                logged: [],
                chargers: [new("MP1", charger, Reference)],
                subscriptions:
                [new("A1", "CC1", "OAboEje0", new DateTime(2026, 6, 1),
                    new DateTime(2026, 8, 31), "Live", null),
                 new("A1", "CC1", "OAboEje0", new DateTime(2026, 6, 1),
                    new DateTime(2026, 11, 30), "Log", new DateTime(2026, 9, 4, 9, 30, 0),
                    "Slutdato er ændret")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            var target = Assert.Single(Assert.Single(plan.Items).Targets);
            Assert.Equal(new DateOnly(2026, 11, 30), target.End);
            Assert.Equal(0, plan.ExcludedCount);
            Assert.Equal(3, plan.Review.Count);
            Assert.Contains(plan.Review, row => row.PartType == "Laderabonnement"
                && row.Source == "Aktuel" && row.End == "2026-11-30"
                && row.EffectiveEnd == "2026-11-30");
            Assert.DoesNotContain(plan.Review, row => row.PartType == "Laderabonnement" && row.Source == "Log");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Earliest_change_after_reference_supplies_the_previous_end_date()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live"),
                 Part("V Variabel", "2026-06-01", "2026-08-31", "Live")],
                logged:
                [Part("V AboRabat", "2026-06-01", "2026-11-30", "Log", EndChanged())
                    with { LoggedAt = new DateTime(2026, 8, 31, 9, 17, 0) },
                 Part("V AboRabat", "2026-06-01", "2026-10-31", "Log", EndChanged())
                    with { LoggedAt = new DateTime(2026, 9, 4, 9, 17, 0) },
                 Part("V Variabel", "2026-06-01", "2026-11-30", "Log", EndChanged())
                    with { LoggedAt = new DateTime(2026, 8, 31, 9, 17, 1) }]);

            var target = Assert.Single(Assert.Single((await AborabatRepair.PrepareAsync(
                directory, "production", CancellationToken.None)).Items).Targets);
            Assert.Equal(new DateOnly(2026, 11, 30), target.End);
        }
        finally { Directory.Delete(directory, recursive: true); }

        static string EndChanged() => "Slutdato er ændret";
    }

    [Fact]
    public async Task Charger_deletion_after_September_window_is_not_restored()
    {
        var directory = NewDirectory();
        try
        {
            var charger = new EvCharger("I1", "A1", "CC1", null, [], true);
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-05-01", "2026-08-31", "Live"),
                 Part("V LadVariP", "2026-05-01", "2026-08-31", "Live")],
                logged:
                [Part("V AboRabat", "2026-05-01", "2026-10-31", "Log", "Slutdato er ændret"),
                 Part("V LadVariP", "2026-05-01", "1900-01-01", "Log", "Slutdato er ændret")],
                chargers: [new("MP1", charger, Reference)],
                subscriptions:
                [new("A1", "CC1", "OAboEje0", new DateTime(2026, 4, 27),
                    new DateTime(2026, 8, 31), "Live", null),
                 new("A1", "CC1", "OAboEje0", new DateTime(2026, 4, 27),
                    new DateTime(2026, 10, 25), "Log", new DateTime(2026, 9, 4, 13, 35, 33),
                    "Slutdato er ændret") { LogEmployeeId = "Xellent" },
                 new("A1", "CC1", "OAboEjeMEL", new DateTime(2026, 10, 26),
                    new DateTime(1900, 1, 1), "Log", new DateTime(2026, 9, 4, 13, 35, 44),
                    "Kontraktdel er slettet") { LogEmployeeId = "Xellent" }]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.Equal(3, targets.Count);
            Assert.Contains(targets, t => t.ContractName == "V Variabel UA"
                && t.Start == new DateOnly(2026, 10, 26) && t.End == new DateOnly(2026, 10, 31));
            Assert.Contains(targets, t => t.ContractName == "V Variabel"
                && t.Start == new DateOnly(2026, 11, 1) && t.End == DateOnly.MaxValue);
            Assert.DoesNotContain(plan.Review, row => row.ProductNumber == "OAboEjeMEL");
            Assert.Contains(plan.Review, row => row.ProductNumber == "OAboEje0"
                && row.Source == "Aktuel" && row.EffectiveEnd == "2026-10-25");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task September_charger_termination_restores_future_part_from_earlier_end_log()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-05-01", "2026-10-31", "Live"),
                 Part("V LadVariP", "2026-05-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new("MP1", new EvCharger("I1", "A1", "CC1", null, [], true), Reference)],
                subscriptions:
                [new("A1", "CC1", "OAboEje0", new DateTime(2026, 5, 1),
                    new DateTime(2026, 8, 31), "Live", null),
                 new("A1", "CC1", "OAboEje0", new DateTime(2026, 5, 1),
                    new DateTime(2026, 10, 31), "Log", new DateTime(2026, 9, 4, 12, 53, 54),
                    "Slutdato er ændret") { LogEmployeeId = "Xellent", LogRecordId = 101 },
                 new("A1", "CC1", "OAboEjeMEL", new DateTime(2026, 11, 1),
                    new DateTime(1900, 1, 1), "Log", new DateTime(2026, 9, 4, 12, 53, 54),
                    "Slutdato er ændret") { LogEmployeeId = "Xellent", LogRecordId = 102 },
                 new("A1", "CC1", "OAboEjeMEL", new DateTime(2026, 11, 1),
                    new DateTime(2026, 8, 31), "Log", new DateTime(2026, 9, 4, 13, 34, 32),
                    "Kontraktdel er slettet") { LogEmployeeId = "Xellent", LogRecordId = 103 }],
                extraLinks: [new SettlContractLinkRow("OTHER", Guid.NewGuid(),
                    "LadeAbo_Eje_Variabel", DateTimeOffset.UtcNow,
                    DateTimeOffset.MaxValue, Guid.NewGuid())]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Equal(0, plan.ExcludedCount);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Eje0kr_VariabelUA"
                && t.End == new DateOnly(2026, 10, 31));
            Assert.Contains(targets, t => t.ContractName == "LadeAbo_Eje_Variabel"
                && t.Start == new DateOnly(2026, 11, 1) && t.End == DateOnly.MaxValue);
            Assert.Contains(plan.Review, row => row.ProductNumber == "OAboEjeMEL"
                && row.Source == "Log" && row.End == "Åben"
                && row.ChangeType == "Fremtidig kontraktdel slettet ved migrering"
                && row.Evidence.Contains("102"));
            Assert.Contains(plan.Parts, p => p.Product == "OAboEjeMEL"
                && p.Record.Contains("103") && p.EndRecord.Contains("102"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task September_deletion_itself_can_record_an_open_end()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-09-01", "2026-12-31", "Live"),
                 Part("V LadVariP", "2026-09-01", "1900-01-01", "Live")],
                logged: [],
                chargers: [new("MP1", new EvCharger("I1", "A1", "CC1", null, [], true), Reference)],
                subscriptions:
                [new("A1", "CC1", "OAboEjeMEL", new DateTime(2026, 9, 1),
                    new DateTime(1900, 1, 1), "Log", new DateTime(2026, 9, 4, 13, 35, 43),
                    "Kontraktdel er slettet") { LogEmployeeId = "Xellent" }],
                extraLinks: [new SettlContractLinkRow("OTHER", Guid.NewGuid(),
                    "LadeAbo_Eje_Variabel", DateTimeOffset.UtcNow,
                    DateTimeOffset.MaxValue, Guid.NewGuid())]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);

            Assert.Equal(0, plan.ExcludedCount);
            Assert.Contains(plan.Review, row => row.ProductNumber == "OAboEjeMEL"
                && row.Source == "Log" && row.End == "Åben");
            Assert.Contains(Assert.Single(plan.Items).Targets,
                t => t.ContractName == "LadeAbo_Eje_VariabelUA"
                    && t.Start == new DateOnly(2026, 9, 1)
                    && t.End == new DateOnly(2026, 12, 31));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Named_user_batch_change_does_not_restore_its_old_end_date()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live"),
                 Part("V Variabel", "2026-06-01", "2026-08-31", "Live")],
                logged:
                [Part("V AboRabat", "2026-06-01", "2026-11-30", "Log", "Slutdato er ændret")
                    with { LogEmployeeId = "PERSON" },
                 Part("V Variabel", "2026-06-01", "2026-11-30", "Log", "Slutdato er ændret")
                    with { LogEmployeeId = "PERSON" }]);

            var target = Assert.Single(Assert.Single((await AborabatRepair.PrepareAsync(
                directory, "production", CancellationToken.None)).Items).Targets);
            Assert.Equal(new DateOnly(2026, 8, 31), target.End);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Old_snapshot_without_part_identity_cannot_generate_an_import()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live")
                    with { CompanyId = null }], logged: []);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None));
            Assert.Contains("--only xellent", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Part_genuinely_ended_before_reference_drops_out_after_restoration()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live:
                [Part("V AboRabat", "2022-04-01", "2026-08-31", "Live"),
                 Part("V Variabel", "2022-04-01", "2026-08-31", "Live")],
                logged:
                [Part("V AboRabat", "2022-04-01", "2024-10-13", "Log", "Slutdato er ændret"),
                 Part("V Variabel", "2022-04-01", "2024-10-13", "Log", "Slutdato er ændret")]);

            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Empty(plan.Items);
            Assert.Equal(0, plan.ExcludedCount);
            Assert.Empty(plan.Review);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("2026-07-25")]
    [InlineData("2026-08-31")]
    [InlineData("2026-09-01")]
    public async Task Migration_restoration_is_independent_of_reference_date(string date)
    {
        var directory = NewDirectory();
        try
        {
            var reference = DateOnly.Parse(date);
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-06-01", "2026-08-31", "Live")],
                logged:
                [Part("V AboRabat", "2026-06-01", "2026-11-30", "Log", "Slutdato er ændret"),
                 Part("V Variabel", "2026-06-01", "2026-11-30", "Log")],
                chargers: [new(null, new EvCharger("I0", "A1", "C0", null, []), reference)]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None, reference);
            var target = Assert.Single(Assert.Single(plan.Items).Targets);
            Assert.Equal(new DateOnly(2026, 11, 30), target.End);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("2026-08-31T09:13:48", null, false)]
    [InlineData("2026-08-31T09:13:49", null, true)]
    [InlineData("2026-08-31T09:59:32", "", true)]
    [InlineData("2026-08-31T09:59:33", null, false)]
    [InlineData("2026-09-04T13:35:33", null, false)]
    [InlineData("2026-08-31T09:30:00", "PERSON", false)]
    public async Task Only_blank_user_deletions_inside_batch_window_are_restored(
        string timestamp, string? employee, bool restored)
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                live: [Part("V AboRabat", "2026-09-01", "2026-11-30", "Live")],
                logged: [Part("V Variabel", "2026-09-01", "2026-11-30", "Log")
                    with { LoggedAt = DateTime.Parse(timestamp), LogEmployeeId = employee }]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            Assert.Equal(restored ? 1 : 0, plan.Items.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("2026-07-01", "1900-01-01", "2026-12-31", 2, 1)]
    [InlineData("2027-01-01", "1900-01-01", "2026-12-31", 1, 0)]
    [InlineData("2026-06-01", "1900-01-01", "2026-12-31", 2, 1)]
    [InlineData(null, "2027-03-31", "2026-12-31", 3, 0)]
    [InlineData(null, "1900-01-01", "1900-01-01", 1, 0)]
    public async Task Charger_discount_transitions_to_ordinary_and_reconciles_existing_link(
        string? existingStart, string subscriptionEnd, string discountEnd, int creates, int deletes)
    {
        var directory = NewDirectory();
        try
        {
            var normalId = Guid.NewGuid();
            var normalLink = new SettlContractLinkRow(existingStart is null ? "OTHER" : "MP1",
                normalId, "LadeAbo_Leje_Variabel",
                new DateTimeOffset(DateTime.Parse(existingStart ?? "2026-07-01"),
                    TimeSpan.FromHours(existingStart == "2027-01-01" ? 1 : 2)),
                DateTimeOffset.MaxValue, Guid.NewGuid());
            await WriteSnapshot(directory,
                [Part("V AboRabat", "2026-07-01", discountEnd, "Live"),
                 Part("V LadVariP", "2026-07-01", "1900-01-01", "Live")], [],
                [new("MP1", new EvCharger("I1", "A1", "CC1", null, [], true), Reference)],
                [new("A1", "CC1", "OAboLejMEL", new DateTime(2026, 6, 30),
                    DateTime.Parse(subscriptionEnd), "Live", null)], [normalLink]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            var item = Assert.Single(plan.Items);
            Assert.Equal(creates, item.Targets.Count);
            Assert.Equal(deletes, item.ReplacedLinks.Count);
            var ua = Assert.Single(item.Targets, t => t.ContractName == "LadeAbo_Leje_VariabelUA");
            Assert.Equal(discountEnd == "1900-01-01" ? DateOnly.MaxValue : new DateOnly(2026, 12, 31), ua.End);
            if (discountEnd != "1900-01-01" && existingStart != "2027-01-01")
            {
                var tail = Assert.Single(item.Targets, t => t.ContractName == "LadeAbo_Leje_Variabel"
                    && t.Start == new DateOnly(2027, 1, 1));
                Assert.Equal(subscriptionEnd == "1900-01-01" ? DateOnly.MaxValue : new DateOnly(2027, 3, 31), tail.End);
            }
            Assert.DoesNotContain(item.Targets, t => t.End < Reference);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Ordinary_follow_on_tracks_later_product_changes()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                [Part("V AboRabat", "2026-07-01", "2026-12-31", "Live"),
                 Part("V Variabel", "2026-07-01", "2027-03-31", "Live"),
                 Part("V Kvartal", "2027-04-01", "1900-01-01", "Live")], []);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            var targets = Assert.Single(plan.Items).Targets;
            Assert.Equal(3, targets.Count);
            Assert.Contains(targets, t => t.ContractName == "V Variabel" && t.Start == new DateOnly(2027, 1, 1)
                && t.End == new DateOnly(2027, 3, 31));
            Assert.Contains(targets, t => t.ContractName == "V Kvartal" && t.Start == new DateOnly(2027, 4, 1)
                && t.End == DateOnly.MaxValue);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Explicit_UA_product_remains_UA_after_discount_ends()
    {
        var directory = NewDirectory();
        try
        {
            await WriteSnapshot(directory,
                [Part("V AboRabat", "2026-07-01", "2026-12-31", "Live"),
                 Part("V Vari UA", "2026-07-01", "1900-01-01", "Live")], []);
            var target = Assert.Single(Assert.Single((await AborabatRepair.PrepareAsync(
                directory, "production", CancellationToken.None)).Items).Targets);
            Assert.Equal("V Variabel UA", target.ContractName);
            Assert.Equal(DateOnly.MaxValue, target.End);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("2026-10-31", "1900-01-01", "2026-11-01")]
    [InlineData("2026-12-31", "2027-03-31", "2027-01-01")]
    public async Task Electricity_continues_without_charger_and_replaces_wrong_Settl_continuation(
        string discountEnd, string electricityEnd, string ordinaryStart)
    {
        var directory = NewDirectory();
        try
        {
            var wrongLink = new SettlContractLinkRow("MP1", Guid.NewGuid(), "LadeAbo_Eje_Variabel",
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(1)),
                DateTimeOffset.MaxValue, Guid.NewGuid());
            await WriteSnapshot(directory,
                [Part("V AboRabat", "2026-05-01", discountEnd, "Live"),
                 Part("V LadVariP", "2026-05-01", electricityEnd, "Live")], [],
                [new("MP1", new EvCharger("I1", "A1", "CC1", null, [], true), Reference)],
                [new("A1", "CC1", "OAboEje0", new DateTime(2026, 5, 1), new DateTime(2026, 10, 31), "Live", null)],
                [wrongLink]);
            var plan = await AborabatRepair.PrepareAsync(directory, "production", CancellationToken.None);
            var item = Assert.Single(plan.Items);
            Assert.Equal(wrongLink, Assert.Single(item.ReplacedLinks));
            Assert.DoesNotContain(item.Targets, t => t.ContractName == wrongLink.ContractName);
            Assert.All(plan.Review, row =>
            {
                Assert.Contains("Laderabonnementet slutter 31-10-2026", row.ContractBasis);
                Assert.Contains("Elproduktet V LadVariP fortsætter", row.ContractBasis);
                Assert.Contains("Aktuel Xellent-kontraktdel", row.Evidence);
            });
            Assert.Contains(item.Targets, t => t.ContractName == "LadeAbo_Eje0kr_VariabelUA"
                && t.End == new DateOnly(2026, 10, 31));
            Assert.Contains(item.Targets, t => t.ContractName == "V Variabel"
                && t.Start == DateOnly.Parse(ordinaryStart)
                && t.End == (electricityEnd == "1900-01-01" ? DateOnly.MaxValue : DateOnly.Parse(electricityEnd)));
            if (discountEnd == "2026-12-31")
                Assert.Contains(item.Targets, t => t.ContractName == "V Variabel UA"
                    && t.Start == new DateOnly(2026, 11, 1) && t.End == new DateOnly(2026, 12, 31));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-preparation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static XellentContractPartRow Part(string product, string start, string end,
        string source, string? changeType = "Kontraktdel er slettet") =>
        new("MP1", "A1", "C1", "G1", product, DateTime.Parse(start), DateTime.Parse(end),
            source, source == "Log" ? new DateTime(2026, 8, 31, 9, 30, 0) : null,
            source == "Log" ? changeType : null)
        { CompanyId = "HO", AmrCode = "A" };

    private static async Task WriteSnapshot(string directory, List<XellentContractPartRow> live,
        List<XellentContractPartRow> logged,
        List<XellentChargerAssignmentRow>? chargers = null,
        List<XellentChargeSubscriptionRow>? subscriptions = null,
        List<SettlContractLinkRow>? extraLinks = null)
    {
        var store = StateSourceSnapshotStore.Open(directory, "production");
        await store.BeginAsync(CancellationToken.None);
        await store.WriteAsync("xellent-contract-parts", live, CancellationToken.None);
        await store.WriteAsync("xellent-contract-part-logs", logged.Select((p, i) =>
            p with { LogRecordId = p.LogRecordId ?? i + 1 }).ToList(), CancellationToken.None);
        await store.WriteAsync("xellent-charge-subscriptions", (subscriptions ?? [])
            .Select((p, i) => p with { AgreementNumber = p.AgreementNumber ?? "I1",
                CompanyId = p.CompanyId ?? "HO", AmrCode = p.AmrCode ?? "A",
                LogRecordId = p.Source == "Log" ? p.LogRecordId ?? i + 1 : null })
            .ToList(), CancellationToken.None);
        await store.WriteAsync("xellent-charger-assignments", (chargers ??
            [new(null, new EvCharger("I0", "A1", "C0", null, []), Reference)])
            .Select(a => a with { AddressEvidence = a.AddressEvidence ??
                new XellentChargerAddressEvidence(null, a.Charger.Address, null,
                    "EXU_INSTALLATION[test]", []) }).ToList(), CancellationToken.None);
        await store.WriteAsync("settl-contract-links", new[]
        {
            new SettlContractLinkRow("MP1", Guid.NewGuid(), "V AboRabat",
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2020, 12, 31, 0, 0, 0, TimeSpan.Zero), Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "V Variabel UA",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "V Kvartal UA",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "LadeAbo_Eje0kr_VariabelUA",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "LadeAbo_Eje_VariabelUA",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "LadeAbo_Leje_VariabelUA",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "V Variabel",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid()),
            new SettlContractLinkRow("OTHER", Guid.NewGuid(), "V Kvartal",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, Guid.NewGuid())
        }.Concat(extraLinks ?? []).ToArray(), CancellationToken.None);
        await store.CompleteAsync(CancellationToken.None);
    }
}
