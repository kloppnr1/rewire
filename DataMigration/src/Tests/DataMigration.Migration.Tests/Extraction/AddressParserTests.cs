using DataMigration.Domain.Xellent;

namespace DataMigration.Migration.Tests.Extraction;

public class AddressParserTests
{
    [Theory]
    [InlineData("Fiktivvejhp 12", "Fiktivvejhp", "12")]
    [InlineData("Fiktivgadee 18", "Fiktivgadee", "18")]
    [InlineData("Fiktivvejbmg 1", "Fiktivvejbmg", "1")]
    [InlineData("Fiktivvejht 99", "Fiktivvejht", "99")]
    [InlineData("Fiktivvejdgn 5", "Fiktivvejdgn", "5")]
    public void ParseStreetAddress_StandardDanishAddresses(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("H.C. Andersens Boulevard 27", "H.C. Andersens Boulevard", "27")]
    [InlineData("St. Knuds Vej 42", "St. Knuds Vej", "42")]
    [InlineData("Dr. Louises Allé 15", "Dr. Louises Allé", "15")]
    public void ParseStreetAddress_StreetNamesWithDots(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("Fiktivvejcee  123", "Fiktivvejcee", "123")]
    [InlineData("Fiktivvejctb   45", "Fiktivvejctb", "45")]
    [InlineData("  Fiktivvejzl  88  ", "Fiktivvejzl", "88")]
    public void ParseStreetAddress_MultipleSpaces(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("Åboulevarden 21", "Åboulevarden", "21")]
    [InlineData("Fiktivgadef 7", "Fiktivgadef", "7")]
    [InlineData("Fiktivgadeg 33", "Fiktivgadeg", "33")]
    public void ParseStreetAddress_DanishCharacters(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData(null, "", "")]
    [InlineData("   ", "", "")]
    public void ParseStreetAddress_EmptyOrWhitespace(string? input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input!);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("Fiktivvejcbz", "Fiktivvejcbz", "")]
    [InlineData("Kongens Fiktivvejvj", "Kongens Fiktivvejvj", "")]
    [InlineData("Fiktivvejbu", "Fiktivvejbu", "")]
    public void ParseStreetAddress_NoNumber(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("Fiktivgadeh 21B", "Fiktivgadeh", "21B")]
    [InlineData("Fiktivgadei 15A", "Fiktivgadei", "15A")]
    [InlineData("Langelinie Allé 43", "Langelinie Allé", "43")]
    public void ParseStreetAddress_NumbersWithLetterSuffix(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("Fiktivvejzl 2, 1. tv", "Fiktivvejzl", "2")]
    [InlineData("Fiktivvejbhk 10, st.", "Fiktivvejbhk", "10")]
    public void ParseStreetAddress_WithFloorDoorInfo_StripsAfterComma(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Theory]
    [InlineData("Fiktivvejeqw 16 A", "Fiktivvejeqw", "16A")]
    [InlineData("Fiktivvejhw 3 B", "Fiktivvejhw", "3B")]
    public void ParseStreetAddress_SeparateLetterSuffix_MergedIntoBuildingNumber(string input, string expectedStreet, string expectedNumber)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
        Assert.Equal(expectedStreet, streetName);
        Assert.Equal(expectedNumber, buildingNumber);
    }

    [Fact]
    public void ParseStreetAddress_RealWorldExamples()
    {
        var testCases = new[]
        {
            ("Fiktivvejhp  12", "Fiktivvejhp", "12"),
            ("H C Fiktivgadej  40", "H C Fiktivgadej", "40"),
            ("Fiktivvejdh  5", "Fiktivvejdh", "5"),
        };

        foreach (var (input, expectedStreet, expectedNumber) in testCases)
        {
            var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input);
            Assert.Equal(expectedStreet, streetName);
            Assert.Equal(expectedNumber, buildingNumber);
        }
    }
}
