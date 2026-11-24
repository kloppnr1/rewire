using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class InvoiceSourceMatchTests
{
    [Fact]
    public void Direct_match_keeps_a_customer_separate_from_its_possible_representative()
    {
        var kind = InvoiceSourceMatch.Match("417016", "417016", "503281", "production");
        Assert.Equal(InvoiceSourceMatch.Kind.Direct, kind);
        Assert.Equal(["417016"], InvoiceSourceMatch.ApprovedSources(kind, "417016", "503281",
            new Dictionary<string, string[]> { ["503281"] = ["417016", "477383", "503281"] }));
    }

    [Fact]
    public void Representative_match_accepts_an_actual_merged_source()
    {
        var kind = InvoiceSourceMatch.Match("503281", "417016", "503281", "production");
        Assert.Equal(InvoiceSourceMatch.Kind.Representative, kind);
        Assert.Equal(["417016", "503281"], InvoiceSourceMatch.ApprovedSources(kind, "417016", "503281",
            new Dictionary<string, string[]> { ["503281"] = ["417016", "503281"] }));
    }

    [Fact]
    public void Neither_match_is_rejected_and_five_digit_account_is_padded()
    {
        Assert.Equal(InvoiceSourceMatch.Kind.None,
            InvoiceSourceMatch.Match("503281", "417016", "477383", "production"));
        Assert.Equal(InvoiceSourceMatch.Kind.Direct,
            InvoiceSourceMatch.Match("941589", "41589", "41589", "production"));
    }
}
