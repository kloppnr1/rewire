using DataMigration.Domain.Xellent;

namespace DataMigration.Migration.Tests.Extraction;

public class ExtractionMappingsTests
{
    [Fact]
    public void ExpandDataHubCodesToXellent_E17_returnsForbAndForb()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["E17"]);
        Assert.Equal(new[] { "E17/Forb", "FORB" }, result);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_E18_returnsProd()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["E18"]);
        Assert.Equal(new[] { "PROD" }, result);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_E17AndE18_unionsBoth()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["E17", "E18"]);
        Assert.NotNull(result);
        Assert.Contains("E17/Forb", result!);
        Assert.Contains("FORB", result!);
        Assert.Contains("PROD", result!);
        Assert.Equal(3, result!.Length);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_isCaseInsensitive()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["e17"]);
        Assert.Equal(new[] { "E17/Forb", "FORB" }, result);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_trimsWhitespace()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent([" E18 "]);
        Assert.Equal(new[] { "PROD" }, result);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_skipsEmptyEntries()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["", "E17", "  "]);
        Assert.Equal(new[] { "E17/Forb", "FORB" }, result);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_null_returnsNull()
    {
        Assert.Null(ExtractionMappings.ExpandDataHubCodesToXellent(null));
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_emptyList_returnsNull()
    {
        Assert.Null(ExtractionMappings.ExpandDataHubCodesToXellent([]));
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_onlyEmptyStrings_returnsNull()
    {
        Assert.Null(ExtractionMappings.ExpandDataHubCodesToXellent(["", "  "]));
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_unknownCode_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExtractionMappings.ExpandDataHubCodesToXellent(["E17", "UNKNOWN"]));
        Assert.Contains("UNKNOWN", ex.Message);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_d05_returnsD05M1()
    {
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["D05"]);
        Assert.Equal(new[] { "D05 M1" }, result);
    }

    [Fact]
    public void ExpandDataHubCodesToXellent_d14_returnsD14EV()
    {
        // D14 er gyldig som DataHub-kode i listen, men extraction-querien ekskluderer
        // alle Meterpointtypecode der starter med "D14" — så filteret giver 0 i praksis.
        var result = ExtractionMappings.ExpandDataHubCodesToXellent(["D14"]);
        Assert.Equal(new[] { "D14 EV" }, result);
    }
}
