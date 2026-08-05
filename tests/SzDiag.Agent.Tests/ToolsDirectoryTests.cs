using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class ToolsDirectoryTests
{
    [Theory]
    [InlineData(@"C:\Users\msi-pc\OneDrive\Desktop\Client-test")]     // реальный путь с СЗ 160705
    [InlineData(@"C:\Users\User\OneDrive\Робочий стіл\Client-test")]  // и с 160636
    [InlineData(@"C:\Users\u\Dropbox\szdiag")]
    [InlineData(@"C:\Users\u\Google Drive\szdiag")]
    public void IsCloudSynced_KnownCloudFolders_True(string path)
        => Assert.True(ToolsDirectory.IsCloudSynced(path));

    [Theory]
    [InlineData(@"C:\Client-test")]
    [InlineData(@"C:\Users\User\Desktop\Client-test")]
    [InlineData(@"D:\szdiag\agent")]
    public void IsCloudSynced_LocalFolders_False(string path)
        => Assert.False(ToolsDirectory.IsCloudSynced(path));

    [Fact]
    public void Resolve_LocalDir_KeepsToolsNextToAgent()
    {
        var (dir, moved) = ToolsDirectory.Resolve(@"C:\Client-test");

        Assert.False(moved);
        Assert.Equal(Path.Combine(@"C:\Client-test", "tools"), dir);
    }

    [Fact]
    public void Resolve_CloudDir_MovesToProgramData()
    {
        // 250 МБ инструментов в личном облаке клиента откатить невозможно (бэклог п.63).
        var (dir, moved) = ToolsDirectory.Resolve(@"C:\Users\msi-pc\OneDrive\Desktop\Client-test");

        Assert.True(moved);
        Assert.Contains("szdiag", dir);
        Assert.DoesNotContain("OneDrive", dir);
    }
}
