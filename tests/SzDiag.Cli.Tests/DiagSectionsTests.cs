using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class DiagSectionsTests
{
    [Fact]
    public void Parse_SpaceSeparatedArgs_AllAccepted()
    {
        // Реальный ввод с живой СЗ: пробел молча резал всё после первого аргумента.
        var (sections, unknown) = DiagSections.Parse(new[] { "storage", "events", "reboots" });

        Assert.Empty(unknown);
        Assert.Equal(new[] { "storage", "events", "reboots" }, sections);
    }

    [Fact]
    public void Parse_CommaSeparated_Split()
    {
        var (sections, unknown) = DiagSections.Parse(new[] { "storage,gpu" });

        Assert.Empty(unknown);
        Assert.Equal(new[] { "storage", "gpu" }, sections);
    }

    [Fact]
    public void Parse_Aliases_ResolvedToCanonical()
    {
        // hw/reboots/whea — именно то, что уходило в CLI на 160306 и игнорировалось.
        var (sections, unknown) = DiagSections.Parse(new[] { "hw", "disks", "bsod" });

        Assert.Empty(unknown);
        Assert.Equal(new[] { "system", "storage", "reliability" }, sections);
    }

    [Fact]
    public void Parse_UnknownName_ReportedNotIgnored()
    {
        var (sections, unknown) = DiagSections.Parse(new[] { "storage", "diskz" });

        Assert.Equal(new[] { "diskz" }, unknown);
        Assert.Equal(new[] { "storage" }, sections);
    }

    [Fact]
    public void Parse_Duplicates_Collapsed()
    {
        var (sections, _) = DiagSections.Parse(new[] { "storage", "disks", "storage" });

        Assert.Equal(new[] { "storage" }, sections);
    }

    [Theory]
    [InlineData("all")]
    public void Parse_All_MeansEverySection(string token)
    {
        var (sections, unknown) = DiagSections.Parse(new[] { token });

        Assert.Null(sections);   // null = «все секции», как при пустом вводе
        Assert.Empty(unknown);
    }

    [Fact]
    public void Parse_NoArgs_MeansEverySection()
    {
        var (sections, unknown) = DiagSections.Parse(Array.Empty<string>());

        Assert.Null(sections);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Aliases_PointToExistingSections()
    {
        Assert.All(DiagSections.Aliases.Values, v => Assert.Contains(v, DiagSections.All));
    }
}
