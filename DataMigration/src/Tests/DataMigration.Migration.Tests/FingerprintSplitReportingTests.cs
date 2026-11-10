using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Fingerprint-splits og email-advarslen siger begge det samme: "de her konti bliver til hver
/// sin Settl-kunde fordi fingeraftrykket varierer". Er sammenlægningen slået fra, bliver hver
/// konto sin egen kunde uanset fingeraftryk — så påstanden er falsk og hører ikke i rapporten.
/// Samme profil, samme data, kun flaget skifter.
/// </summary>
public class FingerprintSplitReportingTests
{
    private const string Profile = "ppa-customers";

    private const string EmailWarningField = "Email / DirECommunication";

    [Fact]
    public void Merge_on_reports_at_least_one_of_the_two()
    {
        using var run = new ProfileTestRunner(Profile, offline: true, legacyTg4Merge: false);

        // Uden dette ville testen nedenfor bestå selvom afsnittene aldrig blev regnet ud i
        // første omgang — så er der intet at slå fra, og garantien er værdiløs.
        var emailEntries = run.Report.ValidationEntries.Count(e => e.XellentField == EmailWarningField);
        Assert.True(
            run.Report.FingerprintSplits.Count > 0 || emailEntries > 0,
            "Profilen skal give mindst ét split eller én email-advarsel, ellers tester den næste sag ingenting");
        Assert.False(run.Report.RepresentativeMergeDisabled);
        Assert.NotEmpty(run.Report.CustomerEmailFacts);
    }

    [Fact]
    public void Merge_off_reports_neither()
    {
        using var run = new ProfileTestRunner(Profile, offline: true, legacyTg4Merge: false, disableMerge: true);

        Assert.True(run.Report.RepresentativeMergeDisabled);
        Assert.Empty(run.Report.FingerprintSplits);
        Assert.DoesNotContain(run.Report.ValidationEntries, e => e.XellentField == EmailWarningField);

        // Råstoffet bæres heller ikke med: uden en udregning der bruger det, er det én post per
        // konto per batch af ren vægt i rapporten.
        Assert.Empty(run.Report.Fingerprints);
        Assert.Empty(run.Report.CustomerEmailFacts);
    }

    [Fact]
    public void Merge_off_changes_nothing_else_about_the_run()
    {
        using var mergeOn = new ProfileTestRunner(Profile, offline: true, legacyTg4Merge: false);
        using var mergeOff = new ProfileTestRunner(Profile, offline: true, legacyTg4Merge: false, disableMerge: true);

        // Alt andet end de to afsnit er upåvirket af selve rapporteringen. Kundetallet ændrer sig
        // godt nok med merge — det er hele pointen med flaget — men målepunkterne er de samme,
        // og ingen af kørslerne må fejle.
        Assert.Equal(0, mergeOn.ExitCode);
        Assert.Equal(0, mergeOff.ExitCode);
        Assert.Equal(mergeOn.Report.MeteringPointsExtracted, mergeOff.Report.MeteringPointsExtracted);
        Assert.True(mergeOff.Report.CustomersExtracted >= mergeOn.Report.CustomersExtracted);
    }
}
