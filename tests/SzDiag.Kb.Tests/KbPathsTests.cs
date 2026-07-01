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
        Assert.Equal(Path.Combine("/vault", "Заказы", "A-1.md"), p.OrderNote("A-1"));
        Assert.Equal(Path.Combine("/vault", "Компоненты", "SSD.md"), p.ComponentNote("SSD"));
    }

    [Fact]
    public void ReportDir_UnderSzReports()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "СЗ", "156864", "reports", "20260701-120000"),
            p.ReportDir("156864", "20260701-120000"));
    }
}
