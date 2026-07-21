using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbPathsTests
{
    [Fact]
    public void HomeNote_IsSzFolderPlusSzMd()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "СЗ", "156864", "156864.md"), p.HomeNote("156864"));
    }

    [Fact]
    public void EntityNotes_UnderNamedFolders()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "Замовлення", "A-1.md"), p.OrderNote("A-1"));
        Assert.Equal(Path.Combine("/vault", "Компоненти", "SSD.md"), p.ComponentNote("SSD"));
    }

    [Fact]
    public void ReportDir_UnderSzReports()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "СЗ", "156864", "reports", "20260701-120000"),
            p.ReportDir("156864", "20260701-120000"));
    }

    [Fact]
    public void Summary_IsSzFolderPlusVyvodMd()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "СЗ", "156864", "висновок.md"), p.Summary("156864"));
    }

    [Fact]
    public void SymptomNote_UnderSymptomsFolder()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "Симптоми", "Фризи під навантаженням.md"),
            p.SymptomNote("Фризи під навантаженням"));
    }

    [Fact]
    public void SafeEntityName_ReplacesPathSeparatorsAndInvalidChars()
    {
        // Свободный текст сущности не должен уносить имя файла в подпапки или ломать путь.
        Assert.Equal("ПК ASUS - Ryzen 5", KbPaths.SafeEntityName("ПК ASUS / Ryzen 5"));
        Assert.Equal("a-b-c", KbPaths.SafeEntityName("a/b\\c"));
        Assert.DoesNotContain(":", KbPaths.SafeEntityName("model: x"));
    }

    [Fact]
    public void DeviceNote_SanitizesSlashesInFileName()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "Пристрої", "ПК ASUS - Ryzen.md"),
            p.DeviceNote("ПК ASUS / Ryzen"));
    }
}
